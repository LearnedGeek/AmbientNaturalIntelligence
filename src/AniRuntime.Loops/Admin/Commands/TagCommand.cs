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

        // Issue #93 Phase 2.1 (2026-07-09) — CLASSIFIER-AS-SIGNAL discipline.
        //
        // Mark's 2026-07-09 21:12 CDT observation after two consecutive real-
        // world misclassifications (`reminder:check all caps and misspelled
        // words` — a neutral research note, not an invalidation; `todo:fix
        // text formatting` — display bug, not a substrate correction) plus
        // the misread on the pronoun-attribution tag (Ani's framing device
        // was coherent, my substrate-invalidation was wrong): *"using regex
        // for something like this is hurting us. We can[not] apply an
        // imprecise determination like regex to a deterministic rating."*
        //
        // The classifier is qwen3:14b, not regex — but Mark's point is the
        // architectural class: **imprecise verdict → deterministic mutation
        // is the wrong shape**, regardless of whether the imprecise verdict
        // came from regex or an LLM. Same failure mode killed the aborted
        // retroactive sweep (299 flags on a bad substrate view).
        //
        // Retired: `MinConfidenceForSubstrateMutation` threshold + the
        // TryInvalidateAsync / TryConfirmAsync auto-mutation paths.
        //
        // Kept: the classifier call itself. Verdict + confidence + reason
        // are logged as observational metadata via TAG_INTENT_VERDICT +
        // TAG_INTENT_APPLIED. Every tag saves to confabulation_flags
        // (SaveConfabulationFlagAsync above) with Mark's raw note. Substrate
        // mutations move to explicit surfaces (future UI review, or
        // explicit `///invalidate` / `///confirm` admin command) when the
        // aggregate signal is worth acting on.
        var verdict = await _classifier.ClassifyAsync(note, aniReply, markMessage, ct)
            .ConfigureAwait(false);

        _log.LogInformation(
            "TAG_INTENT_APPLIED intent={Intent} confidence={Confidence:F2} " +
            "note={Note} reason={Reason} mutation=none",
            verdict.Intent, verdict.Confidence, note, verdict.Reason);

        return $"Tagged [{note}]:\n" +
               $"→ \"{markMessage[..Math.Min(60, markMessage.Length)]}\"\n" +
               $"← \"{aniReply[..Math.Min(60, aniReply.Length)]}\"\n" +
               $"Classifier: {verdict.Intent.ToString().ToLowerInvariant()} @ {verdict.Confidence:F2} — saved as observation, no substrate change.";
    }

    internal static bool IsValidTagAnchor(ConversationThread? thread) =>
        thread is not null && thread.Messages.Any(m => m.Role == Roles.Ani);
}
