using AniRuntime.Core.Utilities;
using AniRuntime.Loops.Invariants;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// Theme J Phase J.5e (May 1, 2026) — single-source-of-truth check:
/// V1.2's producer-side prompt fragment and the gate's invariant must
/// reference the same threshold + same rule text. If they diverge,
/// future Claude / future contributors silently break the
/// "consolidation" thesis and the bug-class returns under a different
/// disguise.
/// </summary>
public class AntiParrotPromptFragmentsTests
{
    [Fact]
    public void Threshold_MatchesGateInvariant()
    {
        AntiParrotPromptFragments.VerbatimNGramThreshold
            .Should().Be(AntiParrotInvariant.VerbatimNGramThreshold,
                "J.5e consolidation: V1.2's producer-side rule MUST use the same " +
                "threshold as the gate's invariant. Divergence means the producer " +
                "instructs the LLM with one threshold while the gate enforces another — " +
                "the bug class returns under a different disguise.");
    }

    [Fact]
    public void NoVerbatimContactTurnsRule_ContainsThresholdAndContactName()
    {
        var rule = AntiParrotPromptFragments.NoVerbatimContactTurnsRule("Mark");

        rule.Should().Contain("Mark");
        rule.Should().Contain(AntiParrotPromptFragments.VerbatimNGramThreshold.ToString());
        rule.Should().Contain("verbatim");
    }

    [Fact]
    public void NoVerbatimContactTurnsRule_ParameterizedByContactName()
    {
        var sarah = AntiParrotPromptFragments.NoVerbatimContactTurnsRule("Sarah");
        sarah.Should().Contain("Sarah");
        sarah.Should().NotContain("Mark");
    }

    [Fact]
    public void V12_GistPrompt_UsesTheSharedFragment()
    {
        // Smoke check: BuildGistPrompt's system text must contain the
        // canonical no-verbatim rule from AntiParrotPromptFragments. If
        // someone reverts BuildGistPrompt to inline the rule again, this
        // test fails and signals the consolidation broke.
        var thread = new AniRuntime.Core.Models.ConversationThread
        {
            Messages = new List<AniRuntime.Core.Models.ConversationMessage>
            {
                new() { Role = AniRuntime.Core.Roles.Mark, Content = "hi" },
            },
        };

        var (system, _) = AniRuntime.LLM.ClosedConversationSummarizer.BuildGistPrompt(thread);

        var canonicalRule = AntiParrotPromptFragments.NoVerbatimContactTurnsRule("Mark");
        system.Should().Contain(canonicalRule,
            "V1.2's gist prompt must reference the shared anti-parrot fragment, not an inlined copy.");
    }
}
