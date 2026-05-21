using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Loops.Context;

/// <summary>
/// Production implementation of <see cref="IConversationContextBuilder"/>.
/// Extracted from <c>ContextBuilder</c> 2026-05-19 as the third sub-builder
/// of the SRP decomposition (`ANI-Testability-Architecture-Plan.md` §2).
///
/// Owns three conversation-history fields previously inline in
/// <c>ContextBuilder.BuildContextSnapshotAsync</c>:
/// <see cref="ConversationContextResult.RecentConversationSummary"/>
/// (prose form extracted from recent episodic memory by content-prefix match),
/// <see cref="ConversationContextResult.StructuredConversationSummary"/>
/// (Theme J Phase J.2 step 2 — per-speaker per-turn from
/// <see cref="IConversationService"/>),
/// <see cref="ConversationContextResult.RecentClosedConversation"/>
/// (Vibe Loop V1.4 — most recent <see cref="ClosedConversationRecord"/> from
/// <see cref="IClosedConversationStore"/>).
///
/// Behavior preservation: missing upstream services and retrieval failures
/// each independently degrade the corresponding field to null, matching the
/// pre-extraction inline shape exactly.
/// </summary>
public sealed class ConversationContextBuilder : IConversationContextBuilder
{
    private readonly IConversationService?                   _conversation;
    private readonly IClosedConversationStore?               _closedStore;
    private readonly AniOptions                              _aniOptions;
    private readonly ILogger<ConversationContextBuilder>     _log;

    public ConversationContextBuilder(
        IOptions<AniOptions>                    aniOptions,
        ILogger<ConversationContextBuilder>     log,
        IConversationService?                   conversation = null,
        IClosedConversationStore?               closedStore  = null)
    {
        _aniOptions     = aniOptions.Value;
        _log            = log;
        _conversation   = conversation;
        _closedStore    = closedStore;
    }

    public async Task<ConversationContextResult> BuildAsync(
        CharacterStateDoc               characterState,
        IReadOnlyList<MemoryRecord>     recentMemory,
        CancellationToken               ct)
    {
        // Recent conversation summary — prose form pulled from recent episodic
        // memory by content-prefix match. Feeds inner thoughts, outreach
        // decisions, and outreach messages.
        var conversationSummary = recentMemory
            .Where(m => m.Type == MemoryType.Episodic
                     && m.Content.StartsWith(MemoryPrefixes.ConversationSummary))
            .Select(m => m.Content)
            .FirstOrDefault();

        // Theme J Phase J.2 step 2 (Apr 27, 2026): structured per-speaker
        // summary alongside the prose form. The thread carries Role/Content/
        // SentAt structurally so this is a direct map — no fragile prose parse.
        StructuredConversationSummary? structuredSummary = null;
        if (_conversation is not null)
        {
            try
            {
                var recentThreads = await _conversation
                    .GetRecentThreadsAsync(limit: 1, ct).ConfigureAwait(false);
                var thread = recentThreads.FirstOrDefault();
                if (thread is { Messages.Count: > 0 })
                {
                    var contactName = characterState.PrimaryContactName ?? Roles.Mark;
                    var companionName = string.IsNullOrWhiteSpace(characterState.Name)
                        ? Roles.Ani
                        : characterState.Name;

                    var turns = thread.Messages
                        .Select(m => new SummaryTurn(
                            At:      m.SentAt,
                            Speaker: m.Role == Roles.Ani ? companionName : contactName,
                            Content: m.Content))
                        .ToList();

                    structuredSummary = new StructuredConversationSummary(
                        FirstTurnAt: turns[0].At,
                        LastTurnAt:  turns[^1].At,
                        Turns:       turns);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "J.2: failed to load structured conversation summary — falling back to prose-only");
            }
        }

        // Vibe Loop V1.4 (Apr 29, 2026): most recent ClosedConversationRecord.
        // Outreach prompts (decision + composition) read this in preference to
        // verbatim-prose surfaces — its Gist is paraphrased by V1.2's anti-
        // parrot prompt, so the outreach path becomes structurally verbatim-free.
        ClosedConversationRecord? recentClosed = null;
        if (_closedStore is not null)
        {
            try
            {
                var recent = await _closedStore
                    .GetRecentAsync(limit: 1, ct).ConfigureAwait(false);
                recentClosed = recent.FirstOrDefault();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "Vibe Loop V1.4: failed to load most recent ClosedConversationRecord — outreach falls back to structured-summary path");
            }
        }

        // Theme J Phase J.0 (Apr 24, 2026): instrument the conversation-summary
        // surface. J.2 step 2 visibility: report whether the structured form
        // is available alongside the prose. Once consumers migrate to the
        // structured field (J.2 step 3+), structuredSummary should be present
        // for every cycle that has a recent conversation.
        if (_aniOptions.GuardRefactorBaselineLoggingEnabled)
        {
            if (!string.IsNullOrEmpty(conversationSummary))
            {
                var firstNewline = conversationSummary.IndexOf('\n');
                var prefixLine = firstNewline > 0
                    ? conversationSummary[..firstNewline]
                    : conversationSummary;
                _log.LogInformation(
                    "J0_SUMMARY_STATE chars={Chars} prefix={Prefix} body={Body}",
                    conversationSummary.Length, prefixLine, conversationSummary);
            }
            else
            {
                _log.LogInformation("J0_SUMMARY_STATE chars=0 prefix=(none) body=(empty)");
            }

            if (structuredSummary is null)
            {
                _log.LogInformation("J2_STRUCTURED_SUMMARY present=false turns=0");
            }
            else
            {
                _log.LogInformation(
                    "J2_STRUCTURED_SUMMARY present=true turns={Turns} firstAt={First:o} lastAt={Last:o}",
                    structuredSummary.Turns.Count,
                    structuredSummary.FirstTurnAt,
                    structuredSummary.LastTurnAt);
            }
        }

        return new ConversationContextResult(
            RecentConversationSummary:      conversationSummary,
            StructuredConversationSummary:  structuredSummary,
            RecentClosedConversation:       recentClosed);
    }
}
