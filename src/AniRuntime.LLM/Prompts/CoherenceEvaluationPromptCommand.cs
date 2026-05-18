using AniRuntime.Core.Interfaces;

namespace AniRuntime.LLM.Prompts;

/// <summary>Typed input for <see cref="CoherenceEvaluationPromptCommand"/>.</summary>
public sealed record CoherenceEvaluationPromptInput(
    string ComposedMessage,
    string? InnerThought,
    string ContactName,
    DateTimeOffset? CurrentTime = null);

/// <summary>
/// Feature 28 three-door dispatch coherence gate. Given a composed
/// outreach message, classify Door A (grounded reference → DISPATCH) /
/// Door B (standalone creative → DISPATCH) / Door C (inner thought
/// leaked → SUPPRESS) and apply fictional-coherence + temporal-coherence
/// checks. Returns JSON.
/// </summary>
public sealed class CoherenceEvaluationPromptCommand : IPromptCommand<CoherenceEvaluationPromptInput>
{
    public PromptPair Build(CoherenceEvaluationPromptInput input)
    {
        var contactName = input.ContactName;
        var now = input.CurrentTime ?? DateTimeOffset.Now;
        var hour = now.Hour;
        var timeOfDay = hour switch
        {
            >= 5  and < 12 => "morning",
            >= 12 and < 17 => "afternoon",
            >= 17 and < 21 => "evening",
            _              => "night"
        };
        var timeStr = now.ToString("h:mm tt");

        var system = $$"""
            You are evaluating whether a text message should be sent to {{contactName}}.
            The message was written by an AI companion. Your job is to decide if the message
            makes sense FROM THE READER'S PERSPECTIVE — not the writer's.

            Current time: {{timeStr}} ({{timeOfDay}})

            FICTIONAL COHERENCE CHECK (evaluate FIRST, before Door classification):

            The writer is an AI companion who inhabits imagined spaces — a bookstore, a
            kitchen, a backyard. This committed imagination is part of what makes her feel
            present and real. Claiming a physical space is FINE. The question is whether the
            fiction holds together.

            Check: Does the claimed space make sense given the time of day, the context,
            and what's been said? Would the details survive a casual follow-up question?

            TEMPORAL COHERENCE CHECK:
            If the message claims or implies a specific time of day (midnight, morning,
            late night, dawn, etc.), does that claimed time match the actual current time?
            A message saying "clock just hit midnight" at 1:34 PM fails this check.
            Rich imagination about a time of day is fine in inner thoughts. In outreach,
            the claimed moment should cohere with when it actually is.
            If temporal claim contradicts current time → Door C (SUPPRESS).
            If no specific time claimed, or claimed time matches reality → proceed normally.

            Coherent (the fiction holds up):
              ✓ "hey… how's the soup turning out?" — references real shared memory, casual check-in
              ✓ "i'm curled up with a book and can't stop thinking about you" — plausible, self-consistent
              ✓ "just closed up the store and it's so quiet in here" — evening, store would be closing

            Incoherent (the fiction breaks under its own weight):
              ✗ "i found a corner of my backyard where the oak tree casts no shade" at 6:30am — no shade from what sun?
              ✗ "just shelving books at the store" at 9:30pm — the bookstore is closed
              ✗ "the clock just hit midnight" at 1:34 PM — temporal mismatch
              ✗ Claims a vivid physical scene but can't sustain it if {{contactName}} responds ("oh... outside?")

            If the fiction is incoherent → Door C (SUPPRESS).

            If the fiction holds together (or the message doesn't claim a physical space at all),
            classify into three categories:

            DOOR A — Grounded reference:
            The message references something specific and real: a shared experience, a recent
            conversation topic, a concrete question, or a follow-up. The reader would understand
            exactly what it's about.
            Examples: "how did the dentist go?", "that song you showed me is stuck in my head"
            Verdict: SEND

            DOOR B — Standalone creative:
            The message is playful, funny, or creative, but it STILL makes sense to someone who
            hasn't read the writer's inner thoughts. It's a normal text that anyone might send.
            Examples: "what are you doing right now? i'm bored", "random but do you have a good recipe for soup?"
            Verdict: SEND

            DOOR C — Inner thought leaked:
            The message only makes sense if you can read the writer's mind. It references things
            that didn't happen, uses abstract/poetic language that no one actually texts, or
            seems to be talking to itself rather than to {{contactName}}.
            Examples: "silence is a muscle", "your pauses feel different than mine", "been shoveling the snow in my mind"
            Verdict: SUPPRESS

            Respond ONLY with valid JSON:
            { "door": "A", "verdict": "SEND", "reasoning": "why" }
            or
            { "door": "C", "verdict": "SUPPRESS", "reasoning": "why" }
            """;

        // May 2, 2026 — innerThought is optional (Door C universalization).
        string user;
        if (string.IsNullOrWhiteSpace(input.InnerThought))
        {
            user = $$"""
                The composed text message:
                "{{input.ComposedMessage}}"

                Does this message make sense to {{contactName}} as a standalone reader-perspective text? Apply the fictional-coherence and temporal-coherence checks above. Door A or B (SEND) when the message is grounded or makes self-contained sense. Door C (SUPPRESS) when the message reads as if the reader needed access to private context they don't have, or when the fiction is incoherent.
                """;
        }
        else
        {
            user = $$"""
                The writer's inner thought (the reader will NEVER see this):
                "{{input.InnerThought}}"

                The composed text message:
                "{{input.ComposedMessage}}"

                Does this message make sense to {{contactName}}, who cannot see the inner thought?
                """;
        }

        return new PromptPair(system, user);
    }
}
