using AniRuntime.Core.Models;
using AniRuntime.LLM;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// Theme N Phase N.3 (May 8, 2026) — spec tests for the
/// <see cref="PromptBuilder.BuildOutreachMessagePrompt"/> frame-aware
/// signature.
///
/// When the optional <see cref="OutreachFrame"/> parameter is non-null
/// AND its <see cref="OutreachFrame.FrameType"/> is not
/// <see cref="OutreachFrameType.None"/>, the user prompt prepends:
/// <list type="number">
///   <item>A <c>[FRAME: &lt;type&gt;]</c> header line.</item>
///   <item>An <c>[ANCHOR] &lt;truncated 200-char anchor text&gt;</c> section.</item>
///   <item>A per-frame composition guidance phrase steering the model
///   toward the honest framing for that source-type.</item>
/// </list>
/// When the parameter is null OR <see cref="OutreachFrameType.None"/>,
/// the prompt is structurally identical to the pre-N.3 form (caller
/// paths that don't enable the flag are unaffected).
/// </summary>
public class PromptBuilderOutreachFrameTests
{
    private static ContextSnapshot MinimalSnapshot() => new()
    {
        CharacterState = new CharacterStateDoc
        {
            Name               = "Ani",
            PrimaryContactName = "Mark",
            CoreTraits         = ["warm", "curious"],
            Occupation         = "Bookstore",
        },
        DesireState         = new DesireState(),
        EmotionalState      = new EmotionalState(),
        RecentMemory        = [],
        RelevantMemory      = [],
        OpenLoops           = [],
        Perceptions         = [],
        RecentHistory       = [],
        SimilarRecentThoughts = [],
        BuiltAt             = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void OutreachFrame_Null_DoesNotEmitFrameOrAnchorSections()
    {
        // The N.3-default caller path passes no frame. The prompt must be
        // structurally identical to the pre-N.3 form — no [FRAME:] header,
        // no [ANCHOR] section, no per-frame guidance line.
        var snapshot = MinimalSnapshot();

        var (_, user) = PromptBuilder.BuildOutreachMessagePrompt(
            snapshot,
            recentThought: "thinking about how the sky cleared up",
            reasoning: "",
            reasoningInComposition: false,
            frame: null);

        user.Should().NotContain("[FRAME:");
        user.Should().NotContain("[ANCHOR]");
        user.Should().NotContain("Compose by referencing");
        user.Should().NotContain("Compose by framing");
        user.Should().Contain("Trigger:",
            "the prompt's existing motivation block must still be present");
    }

    [Fact]
    public void OutreachFrame_None_DoesNotEmitFrameOrAnchorSections()
    {
        // OutreachFrame.None is the canonical "suppress" sentinel. If a
        // caller still happens to pass it (defense in depth), the prompt
        // builder must NOT emit a [FRAME: None] header or any per-frame
        // composition guidance. The prompt is structurally identical to
        // the null-frame case.
        var snapshot = MinimalSnapshot();

        var (_, user) = PromptBuilder.BuildOutreachMessagePrompt(
            snapshot,
            recentThought: "thinking about how the sky cleared up",
            reasoning: "",
            reasoningInComposition: false,
            frame: OutreachFrame.None);

        user.Should().NotContain("[FRAME:");
        user.Should().NotContain("[ANCHOR]");
        user.Should().NotContain("Compose by");
    }

    [Fact]
    public void OutreachFrame_Shared_EmitsHeaderAnchorAndSharedGuidance()
    {
        var snapshot = MinimalSnapshot();
        var frame = new OutreachFrame(
            OutreachFrameType.Shared,
            "Mark said: I had a long day at work today",
            0.85f);

        var (_, user) = PromptBuilder.BuildOutreachMessagePrompt(
            snapshot,
            recentThought: "wondering how his afternoon went",
            reasoning: "",
            reasoningInComposition: false,
            frame: frame);

        user.Should().Contain("[FRAME: Shared]");
        user.Should().Contain("[ANCHOR] Mark said: I had a long day at work today");
        user.Should().Contain("remember when",
            "Shared frame guidance must steer the model toward 'remember when...' framing");
        user.Should().Contain("that thing you said about");
    }

    [Fact]
    public void OutreachFrame_AniDomain_EmitsHeaderAnchorAndAniDomainGuidance()
    {
        var snapshot = MinimalSnapshot();
        var frame = new OutreachFrame(
            OutreachFrameType.AniDomain,
            "the bookstore: late afternoon light through the front window",
            0.7f);

        var (_, user) = PromptBuilder.BuildOutreachMessagePrompt(
            snapshot,
            recentThought: "thinking about light through the window",
            reasoning: "",
            reasoningInComposition: false,
            frame: frame);

        user.Should().Contain("[FRAME: AniDomain]");
        user.Should().Contain("[ANCHOR] the bookstore: late afternoon light through the front window");
        user.Should().Contain("the bookstore",
            "AniDomain guidance must steer the model toward 'the bookstore...' framing");
        user.Should().Contain("canonical-world framing");
    }

    [Fact]
    public void OutreachFrame_AniInterior_EmitsInteriorGuidance_WithHonestSelfFraming()
    {
        // ANI_INTERIOR is the architecturally load-bearing frame from
        // Theme N's mirror-trap analysis (May 6 16:30 CDT). Interior
        // content is fine to share; the failure mode is presenting it
        // with shared-experience framing. The guidance must enforce
        // honest "i was just thinking..." framing.
        var snapshot = MinimalSnapshot();
        var frame = new OutreachFrame(
            OutreachFrameType.AniInterior,
            "i imagined the kitchen lights softer at midnight, the way they bend off the chrome",
            0.6f);

        var (_, user) = PromptBuilder.BuildOutreachMessagePrompt(
            snapshot,
            recentThought: "thinking about kitchen lights at midnight",
            reasoning: "",
            reasoningInComposition: false,
            frame: frame);

        user.Should().Contain("[FRAME: AniInterior]");
        user.Should().Contain("[ANCHOR] i imagined the kitchen lights softer at midnight");
        user.Should().Contain("i was just thinking",
            "AniInterior guidance must steer toward honest interior-self framing");
        user.Should().Contain("Don't present interior content as shared observation",
            "AniInterior guidance must explicitly forbid the kitchen-lights failure pattern");
    }

    [Fact]
    public void OutreachFrame_WorldPerception_EmitsHeaderAnchorAndWorldPerceptionGuidance()
    {
        var snapshot = MinimalSnapshot();
        var frame = new OutreachFrame(
            OutreachFrameType.WorldPerception,
            "rss: a piece on bookstore architecture in small Wisconsin towns",
            0.65f);

        var (_, user) = PromptBuilder.BuildOutreachMessagePrompt(
            snapshot,
            recentThought: "an article caught my eye",
            reasoning: "",
            reasoningInComposition: false,
            frame: frame);

        user.Should().Contain("[FRAME: WorldPerception]");
        user.Should().Contain("[ANCHOR] rss: a piece on bookstore architecture in small Wisconsin towns");
        user.Should().Contain("i saw",
            "WorldPerception guidance must steer toward 'i saw...' / external-content framing");
        user.Should().Contain("external-content framing");
    }

    [Fact]
    public void OutreachFrame_AnchorLongerThan200Chars_TruncatedInPrompt()
    {
        // Defensive: even if a substrate item slipped through with very
        // long content, the prompt block stays bounded at ~200 chars so
        // the [ANCHOR] section doesn't dominate the prompt budget.
        var snapshot = MinimalSnapshot();
        var longAnchor = new string('x', 350);
        var frame = new OutreachFrame(
            OutreachFrameType.Shared,
            longAnchor,
            0.7f);

        var (_, user) = PromptBuilder.BuildOutreachMessagePrompt(
            snapshot,
            recentThought: "anchor truncation test",
            reasoning: "",
            reasoningInComposition: false,
            frame: frame);

        user.Should().Contain("[FRAME: Shared]");
        // The full 350-char anchor must NOT appear verbatim — truncation kicked in.
        user.Should().NotContain(longAnchor);
        // The first 200 chars of the anchor (200 'x's) must be present.
        user.Should().Contain(new string('x', 200));
    }
}
