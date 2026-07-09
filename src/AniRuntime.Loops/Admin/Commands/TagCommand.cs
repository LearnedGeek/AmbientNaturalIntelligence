using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;

namespace AniRuntime.Loops.Admin.Commands;

/// <summary>
/// <c>///tag &lt;label&gt;</c> — AC5 confabulation tagger. Attaches a free-form
/// research label (e.g. "confabulation", "temporal confusion", "repetition")
/// to the most recent (Mark message + Ani reply) pair, persisting both to
/// the <c>confabulation_flags</c> table for pattern analysis.
///
/// <para>
/// **Stale-tag fallback (May 6, 2026)** — when <c>///tag</c> arrives more
/// than <c>ConversationTimeoutMinutes</c> after the last real message, the
/// inbound perception source auto-closes the active thread and opens a
/// new thread for the tag itself. The naive "get active thread" path
/// then returns the tag-only thread which has no Ani content. The fix
/// pulls a wider net of recent threads, iterates newest→oldest within a
/// <see cref="StaleTagLookbackHours"/> window, and picks the first thread
/// that contains at least one Ani message.
/// </para>
///
/// <para>
/// **Issue #93 Phase 2 (2026-07-06)** — the pre-Issue-93 regex intent
/// sniff (matching <c>confab / fabricat / walk back / made up /
/// hallucination</c>) is replaced by <see cref="ITagIntentClassifier"/>,
/// which handles BOTH directions of the substrate-correction loop:
/// negative (invalidate — pre-existing) AND positive (confirm — new,
/// promotes an Interior record into the retrieval-biased pool). The
/// regex vocabulary was inert against Mark's actual tag idiom ("broken
/// output" / "weather confusion" / "json response?"). Below the
/// <see cref="MinConfidenceForSubstrateMutation"/> threshold the flag is
/// still saved for audit but no substrate mutation happens.
/// </para>
/// </summary>
public sealed class TagCommand : IAdminCommand
{
    private const int StaleTagLookbackHours = 12;
    private const int StaleTagSearchLimit   = 5;

    /// <summary>
    /// Issue #93 Phase 2 — floor under which a classifier verdict does NOT
    /// trigger a substrate mutation. Applies to both confirm and invalidate:
    /// low-confidence "invalidate" doesn't touch Validity; low-confidence
    /// "confirm" doesn't touch ConfirmedAt. The flag is still saved either
    /// way so the tag is captured for audit / eval. 0.60 matches the
    /// classifier prompt's own confidence-range guidance ("plausible-but-
    /// ambiguous 0.60–0.85").
    /// </summary>
    internal const float MinConfidenceForSubstrateMutation = 0.60f;

    private readonly IConversationService  _conversations;
    private readonly IMemoryPersistence    _persist;
    private readonly ITagIntentClassifier  _classifier;
    private readonly ILogger<TagCommand>   _log;

    public TagCommand(
        IConversationService  conversations,
        IMemoryPersistence    persist,
        ITagIntentClassifier  classifier,
        ILogger<TagCommand>   log)
    {
        _conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
        _persist       = persist       ?? throw new ArgumentNullException(nameof(persist));
        _classifier    = classifier    ?? throw new ArgumentNullException(nameof(classifier));
        _log           = log           ?? throw new ArgumentNullException(nameof(log));
    }

    public string Name => "tag";

    public string HelpText =>
        "tag <label> — Tag the last reply with a label (e.g. confabulation, temporal\n              confusion, repetition) — saved to confabulation_flags table";

    /// <summary>
    /// Matches <c>"tag &lt;anything&gt;"</c> form rather than exact-name. The
    /// trimmed input is the lower-cased post-`///` content; the actual
    /// label keeps its case because we don't lower-case the original
    /// message before passing it to <see cref="ExecuteAsync"/>.
    /// </summary>
    public bool Matches(string trimmedInput) =>
        trimmedInput.StartsWith("tag ", StringComparison.OrdinalIgnoreCase);

    public async Task<string> ExecuteAsync(string trimmedInput, CancellationToken ct)
    {
        // Strip "tag " prefix to get the note. Defensive against missing space.
        var note = trimmedInput.Length > 4 ? trimmedInput[4..].Trim() : string.Empty;

        _log.LogInformation("[TAG] {Note}", note);

        if (string.IsNullOrWhiteSpace(note))
            return "Usage: ///tag <label>  — e.g. ///tag confabulation";

        var thread = await _conversations.GetActiveThreadAsync(ct).ConfigureAwait(false);
        if (!IsValidTagAnchor(thread))
        {
            var lookbackCutoff = DateTimeOffset.UtcNow.AddHours(-StaleTagLookbackHours);
            var recent = await _conversations.GetRecentThreadsAsync(StaleTagSearchLimit, ct).ConfigureAwait(false);
            thread = recent
                .Where(t => t.LastMessageAt >= lookbackCutoff)
                .FirstOrDefault(IsValidTagAnchor);
        }

        if (thread is null || !IsValidTagAnchor(thread))
            return $"Tagged [{note}] — but no Ani-content thread found within last {StaleTagLookbackHours}h to anchor against.";

        // Find the last Ani reply and the preceding Mark message.
        string? aniReply    = null;
        string? markMessage = null;

        for (var i = thread.Messages.Count - 1; i >= 0; i--)
        {
            if (aniReply is null && thread.Messages[i].Role == Roles.Ani)
                aniReply = thread.Messages[i].Content;
            else if (aniReply is not null && thread.Messages[i].Role == Roles.Mark)
            {
                markMessage = thread.Messages[i].Content;
                break;
            }
        }

        if (aniReply is null)
            return $"Tagged [{note}] — no Ani reply found in recent conversation.";

        markMessage ??= "(no preceding message found)";

        await _persist.SaveConfabulationFlagAsync(
            markMessage, aniReply, topicCategory: note, notes: null, ct: ct).ConfigureAwait(false);

        // Issue #93 Phase 2 (2026-07-06) — LLM-classified intent replaces the
        // regex sniff. The classifier handles both directions of the substrate-
        // correction loop: invalidate (Validity='invalid_confabulation') AND
        // confirm (ConfirmedAt=<now>, ConfirmedBy='mark-tag'). Below the
        // confidence floor the flag is saved for audit but no substrate
        // mutation happens — matches the classifier fail-open contract.
        var verdict = await _classifier.ClassifyAsync(note, aniReply, markMessage, ct)
            .ConfigureAwait(false);

        _log.LogInformation(
            "TAG_INTENT_APPLIED intent={Intent} confidence={Confidence:F2} " +
            "note={Note} reason={Reason}",
            verdict.Intent, verdict.Confidence, note, verdict.Reason);

        var substrateSuffix = string.Empty;

        if (verdict.Confidence >= MinConfidenceForSubstrateMutation)
        {
            substrateSuffix = verdict.Intent switch
            {
                TagIntent.Invalidate => await TryInvalidateAsync(aniReply, ct).ConfigureAwait(false),
                TagIntent.Confirm    => await TryConfirmAsync(aniReply, ct).ConfigureAwait(false),
                _                    => string.Empty,
            };
        }
        else if (verdict.Intent is TagIntent.Confirm or TagIntent.Invalidate)
        {
            substrateSuffix =
                $"\nIntent classified as {verdict.Intent.ToString().ToLowerInvariant()} " +
                $"but confidence {verdict.Confidence:F2} < {MinConfidenceForSubstrateMutation:F2} — flag saved, no substrate change.";
        }

        return $"Tagged [{note}]:\n→ \"{markMessage[..Math.Min(60, markMessage.Length)]}\"\n← \"{aniReply[..Math.Min(60, aniReply.Length)]}\"{substrateSuffix}";
    }

    private async Task<string> TryInvalidateAsync(string aniReply, CancellationToken ct)
    {
        try
        {
            var updated = await _persist.MarkConversationTurnInvalidAsync(aniReply, ct)
                .ConfigureAwait(false);
            return updated > 0
                ? $"\nSubstrate-invalidated {updated} record(s)."
                : "\nNo matching substrate record to invalidate.";
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Walk-back substrate-invalidation failed — flag was still saved");
            return "\nFlag saved, but substrate invalidation failed (see logs).";
        }
    }

    private async Task<string> TryConfirmAsync(string aniReply, CancellationToken ct)
    {
        try
        {
            var updated = await _persist.MarkConversationTurnConfirmedAsync(
                aniReply, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
            return updated > 0
                ? $"\nSubstrate-confirmed {updated} record(s)."
                : "\nNo matching substrate record to confirm.";
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Confirm-tag substrate-confirmation failed — flag was still saved");
            return "\nFlag saved, but substrate confirmation failed (see logs).";
        }
    }

    internal static bool IsValidTagAnchor(ConversationThread? thread) =>
        thread is not null && thread.Messages.Any(m => m.Role == Roles.Ani);
}
