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
/// </summary>
public sealed class TagCommand : IAdminCommand
{
    private const int StaleTagLookbackHours = 12;
    private const int StaleTagSearchLimit   = 5;

    private readonly IConversationService _conversations;
    private readonly IMemoryPersistence _persist;
    private readonly ILogger<TagCommand> _log;

    public TagCommand(
        IConversationService conversations,
        IMemoryPersistence persist,
        ILogger<TagCommand> log)
    {
        _conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
        _persist       = persist       ?? throw new ArgumentNullException(nameof(persist));
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

        return $"Tagged [{note}]:\n→ \"{markMessage[..Math.Min(60, markMessage.Length)]}\"\n← \"{aniReply[..Math.Min(60, aniReply.Length)]}\"";
    }

    internal static bool IsValidTagAnchor(ConversationThread? thread) =>
        thread is not null && thread.Messages.Any(m => m.Role == Roles.Ani);
}
