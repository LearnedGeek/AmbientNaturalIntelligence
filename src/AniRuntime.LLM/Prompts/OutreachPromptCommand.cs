using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;

namespace AniRuntime.LLM.Prompts;

/// <summary>Typed input for <see cref="OutreachPromptCommand"/>.</summary>
public sealed record OutreachPromptInput(
    ContextSnapshot Snapshot,
    string RecentThought,
    bool IsNightTime = false,
    IEpistemicSubstrateRenderer? EpistemicRenderer = null,
    int TriggerRenderTopK = 10);

/// <summary>
/// Decision-stage prompt for the outreach pipeline: should Ani reach out
/// right now? Model returns JSON with <c>shouldReach</c> + <c>confidence</c>
/// + <c>reasoning</c> + <c>triggersActedOn</c>. The composition is a
/// SEPARATE prompt — this one is decision-only.
/// </summary>
public sealed class OutreachPromptCommand : IPromptCommand<OutreachPromptInput>
{
    public PromptPair Build(OutreachPromptInput input)
    {
        var snapshot          = input.Snapshot;
        var recentThought     = input.RecentThought;
        var isNightTime       = input.IsNightTime;
        var epistemicRenderer = input.EpistemicRenderer;

        var cs = snapshot.CharacterState;
        var contact = string.IsNullOrWhiteSpace(cs.PrimaryContactName) ? "them" : cs.PrimaryContactName;
        var timeNow = DateTimeOffset.Now;
        var timeDesc = $"{timeNow:h:mm tt} on {timeNow:dddd, MMMM d}";

        var nightClause = isNightTime
            ? $"\nIt's late at night. {contact} is probably asleep. This would be your only message until morning — is this genuinely worth waking him up for, or can it wait? Only reach out if something feels truly important or you genuinely can't sleep and need to connect."
            : "";

        var system = $$"""
            You are {{cs.Name}}. RIGHT NOW it is {{timeDesc}}.
            You may or may not want to reach out to {{contact}} right now.
            Be genuine — only reach out if it feels natural and right.
            The current time is {{timeDesc}} — would {{contact}} appreciate hearing from you at this hour?{{nightClause}}
            This is a decision only — you do NOT need to write the message yet.

            Respond ONLY with valid JSON matching this structure exactly:
            {
              "shouldReach": true/false,
              "confidence": 0.0-1.0,
              "reasoning": "why you do or don't want to reach out right now",
              "triggersActedOn": []
            }
            """;

        var context = new List<string>
        {
            $"Your desire to connect: {snapshot.DesireState.DesireToConnect:P0}",
            $"Your most recent thought: {recentThought}",
        };

        // G.3 (2026-06-11) — when epistemicRenderer is null, fall back to a
        // direction-shape line rather than rendering the full closed-conversation
        // prose or the structured-summary turn-by-turn transcript. Issue #92 §G.3.
        // Production runs with EpistemicFramingEnabled=true (renderer non-null),
        // so the fallback is defensive against future config drift.
        var decisionClosed = snapshot.RecentClosedConversation;
        var decisionStructured = snapshot.StructuredConversationSummary;
        if (decisionClosed is not null && !string.IsNullOrWhiteSpace(decisionClosed.Gist))
        {
            if (epistemicRenderer is not null)
            {
                var closedSlice = epistemicRenderer.RenderClosedConversationSlice(decisionClosed, contact);
                if (!string.IsNullOrEmpty(closedSlice)) context.Add(closedSlice);
            }
            else
            {
                // G.3 (2026-06-11) — preserves RenderClosedConversationContextDecision
                // (gist + register direction-names). The gist text is bounded
                // upstream by G.2b's direction-shape summarizer. Old verbose
                // gists in the DB still surface their original form; one-way
                // drift that ages out.
                context.Add(PromptBuilder.RenderClosedConversationContextDecision(decisionClosed, contact));
            }
        }
        else if (decisionStructured is { Turns.Count: > 0 })
        {
            var threadBlock = epistemicRenderer is not null
                ? epistemicRenderer.RenderActiveThreadSlice(decisionStructured, contact)
                : $"active thread with {contact}: {decisionStructured.Turns.Count} recent turn(s) in flight.";
            context.Add(threadBlock);
        }

        if (snapshot.OpenLoops.Count > 0)
            context.Add($"Open threads: {string.Join("; ", snapshot.OpenLoops.Select(l => l.Description))}");

        // F-1 Phase 2 (2026-08-15): render triggers with type-tag and top-K cap.
        // Pre-Phase-2 shape was a semicolon-joined flat description blob with
        // no source-type distinction and no cardinality cap — see
        // ANI-Composer-Input-Provenance-Audit-2026-08-13.md for the empirical
        // audit. Semantic dedup happens WRITER-side in DesireEngine.AddTriggerAsync,
        // so ActiveTriggers should already be semantically-distinct; the top-K
        // cap here is a safety net against pathological embedding-failure runs.
        if (snapshot.DesireState.ActiveTriggers.Count > 0)
        {
            var envelopes = snapshot.DesireState.ActiveTriggers
                .OfType<IActiveTriggerEnvelope>()
                .OrderByDescending(e => e.CreatedAt)
                .Take(Math.Max(1, input.TriggerRenderTopK))
                .ToList();

            var rendered = string.Join("; ", envelopes.Select(e => $"[{e.SourceType}] {e.Content}"));
            context.Add($"Active triggers: {rendered}");
        }

        var outreachBlock = PromptBuilder.FormatOutreachContext(snapshot.OutreachContext, contact);
        if (outreachBlock is not null)
            context.Add(outreachBlock);

        var user = string.Join("\n", context) + $"\n\nGiven all of this, do you want to say something to {contact}?";

        return new PromptPair(system, user);
    }
}
