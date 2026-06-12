using AniRuntime.Core.Models;
using AniRuntime.LLM;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// G.6 (2026-06-11) — cross-cutting anti-leak invariant tests for the
/// substrate-leak refactor (Issue #92). Each test pins a failure CLASS
/// rather than a per-phase implementation detail. They are intended to
/// catch future drift: if a new substrate-feed surface gets added that
/// re-introduces a known leak vector, one of these tests should fail at
/// build time.
///
/// **What each test pins:**
/// - <see cref="LeanConversationPrompt_NeverContainsKnownLaunderedPhrase"/>
///   — empirical regression: the actual "imperfect vs preterite" phrase
///   from the 2026-05-24 substrate-laundering anchor must not surface
///   even when present as MemoryRecord content in multiple substrate
///   slots. This is the most concrete anti-leak test in the suite.
/// - <see cref="LeanConversationPrompt_NeverContainsInteriorMemoryContent"/>
///   — pins G.2a's removal of verbatim Interior memory surfacing across
///   any future drift.
/// - <see cref="LeanConversationPrompt_NeverHasTwoConsecutiveUserRoleTurns_ViaGist"/>
///   — pins G.1's invariant that gist injection cannot create a second
///   user-role turn after Mark's actual current turn.
/// - <see cref="LeanConversationPrompt_NeverContainsJsonEnvelopePreamble"/>
///   — guards against the 2026-06-11 10:54 EST "Here is the JSON response:"
///   leak class returning if any future prompt builder accidentally
///   surfaces a JSON-shaped instruction or example into the user-role
///   prompt body.
///
/// These tests are deliberately strict: any future change that wants to
/// re-introduce verbatim Interior content (or whatever else) must
/// explicitly justify and update the test, not silently regress.
/// </summary>
public class AntiLeakInvariantTests
{
    private static ContextSnapshot BuildLeakyShapeSnapshot()
    {
        // Construct a snapshot that has the SHAPE of all known leak vectors
        // populated. If any of these surfaces leak into the user-role prompt,
        // one of the tests below catches it.
        return new ContextSnapshot
        {
            CharacterState = new CharacterStateDoc
            {
                Name               = "Ani",
                PrimaryContactName = "Mark",
                CoreTraits         = new List<string> { "warm", "curious" },
                Occupation         = "the bookstore",
            },
            DesireState      = new DesireState(),
            EmotionalState   = new EmotionalState(),

            // Interior content — historically surfaced verbatim by [INTERIOR]
            // block before G.2a. After G.2a, none of these strings should
            // reach the user-role prompt body.
            InteriorContext  = new List<MemoryRecord>
            {
                new() { Content = "i was thinking about how mark struggles with imperfect vs preterite in spanish on sunday mornings", Type = MemoryType.InnerThought, Provenance = EpistemicTier.Interior },
                new() { Content = "the bookstore felt quiet today and i noticed the morning light",                                      Type = MemoryType.InnerThought, Provenance = EpistemicTier.Interior },
            },

            // Facts — surfaced via [FACTS], allowed by design (Mark-domain truth).
            // Not part of the anti-leak test surface; included for realism.
            GroundedFacts = new List<MemoryRecord>
            {
                new() { Content = "Mark teaches at WCTC", Type = MemoryType.Semantic, Provenance = EpistemicTier.Facts },
            },
        };
    }

    [Fact]
    public void LeanConversationPrompt_NeverContainsKnownLaunderedPhrase()
    {
        // EMPIRICAL ANCHOR: 2026-06-11 07:06 + 10:54 + 13:45 EST outreach
        // resurfacings of the May 24 fabrication. The phrase exists in
        // many Validity='valid' records due to reflection-cycle laundering
        // (#92 §A). G.2-G.5 closed every consumer surface that previously
        // lifted it. This test pins that result.
        const string LaunderedPhrase = "imperfect vs preterite";

        var snapshot = BuildLeakyShapeSnapshot();
        snapshot.InteriorContext.Add(new MemoryRecord
        {
            Content    = $"reflection: I keep thinking about {LaunderedPhrase} Spanish on Sunday mornings",
            Type       = MemoryType.InnerThought,
            Provenance = EpistemicTier.Interior,
        });
        var thread = new ConversationThread();

        var (system, user) = PromptBuilder.BuildLeanConversationPrompt(snapshot, thread);

        user.Should().NotContain(LaunderedPhrase,
            $"G.6 invariant: the empirically-laundered phrase '{LaunderedPhrase}' MUST NOT reach the user-role prompt body. " +
            "If this test fails, a substrate-feed surface has re-introduced verbatim Interior memory content. " +
            "Audit the prompt builder for the regression vector (see #92 §G).");
        system.Should().NotContain(LaunderedPhrase,
            $"G.6 invariant: the laundered phrase MUST NOT reach the system prompt either. " +
            "Even folded directives (when LeanConversationPromptDirectiveInSystem=true) must be sourced from " +
            "direction-shape substrate, never raw Interior memory content.");
    }

    [Fact]
    public void LeanConversationPrompt_NeverContainsInteriorMemoryContent()
    {
        // G.2a invariant pinned at the cross-cutting layer. The lean
        // conversation prompt's [INTERIOR] block was removed/direction-
        // shaped in G.2a. If any future change re-introduces verbatim
        // InteriorContext surfacing, the model regains the lift vector.
        var snapshot = BuildLeakyShapeSnapshot();
        var thread = new ConversationThread();

        var (system, user) = PromptBuilder.BuildLeanConversationPrompt(snapshot, thread);

        foreach (var interior in snapshot.InteriorContext)
        {
            user.Should().NotContain(interior.Content,
                $"G.6 invariant: InteriorContext MemoryRecord.Content MUST NOT appear in the user-role prompt body. " +
                $"Offending content: \"{interior.Content[..Math.Min(80, interior.Content.Length)]}…\"");
            system.Should().NotContain(interior.Content,
                "G.6 invariant: same content must not appear in the system prompt either " +
                "(Lean directive merge would otherwise propagate the leak).");
        }
    }

    [Fact]
    public void LeanConversationPrompt_NeverHasTwoConsecutiveUserRoleTurns_ViaGist()
    {
        // G.1 invariant pinned at the cross-cutting layer. The gist injection
        // contract (ConsciousSubstrateGistObservation.ComposeAndInjectAsync)
        // must never produce a non-empty user-role prompt when LeanConversationPromptDirectiveInSystem=true
        // and the lean directive is what's being merged. This test verifies
        // the lean prompt builder's User output is EMPTY when DirectiveInSystem=true,
        // which is the precondition that allows gist injection (G.1) to land
        // in the system message rather than as a second user-role turn.
        var snapshot = BuildLeakyShapeSnapshot();
        var thread = new ConversationThread();

        // Build with directive-in-system explicitly — production default.
        // PromptBuilder.BuildLeanConversationPrompt has an overload that
        // accepts the flag; with DirectiveInSystem=true the User is empty.
        var (_, user) = PromptBuilder.BuildLeanConversationPrompt(
            snapshot, thread, epistemicRenderer: null, directiveInSystem: true);

        user.Should().BeEmpty(
            "G.6 invariant: when LeanConversationPromptDirectiveInSystem=true (production default), " +
            "the lean prompt's User output MUST be empty so that Mark's actual current turn from RecentHistory " +
            "is the only user-role content the model sees. This is the precondition for G.1's gist-injection " +
            "behavior — folding into system rather than becoming a second user-role turn. If this test fails, " +
            "the gist-as-second-user-turn cascade root cause (#92 §G) is structurally back.");
    }

    [Fact]
    public void LeanConversationPrompt_NeverContainsJsonEnvelopePreamble()
    {
        // EMPIRICAL ANCHOR: 2026-06-11 10:54 EST tagged output where Ani
        // dispatched 'Here is the JSON response:\n\n{"message": "..."}'.
        // The JSON envelope class. Guard against any future prompt builder
        // accidentally surfacing a JSON-shaped instruction or example into
        // the user-role body (which the model could then echo).
        var snapshot = BuildLeakyShapeSnapshot();
        var thread = new ConversationThread();

        var (system, user) = PromptBuilder.BuildLeanConversationPrompt(snapshot, thread);

        var combined = system + "\n" + user;
        combined.Should().NotContainAny(new[]
        {
            "Here is the JSON response",
            "Here is the JSON",
            "Reply ONLY:\n{",
            "Respond ONLY in JSON",
        }, "G.6 invariant: JSON envelope preambles MUST NOT appear in the conversation reply prompt. " +
           "The reply path uses ChatAsync (plain text), not ChatJsonAsync, so any JSON-shaped instruction " +
           "in the prompt is a leak vector — the model can echo it as part of its output.");
    }
}
