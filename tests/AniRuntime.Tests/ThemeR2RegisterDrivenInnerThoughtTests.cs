using AniRuntime.Core;
using AniRuntime.Core.Models;
using AniRuntime.Core.Utilities;
using AniRuntime.LLM;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// Theme R Phase R.2 (2026-05-24, Issue #64) — spec tests pinning the
/// structural register-driven branch in <see cref="PromptBuilder.BuildInnerThoughtPrompt"/>.
///
/// Same R.0 contract as R.1, applied to inner-thought composition.
/// Behavior must differ across the three variant keys per register.
/// Null / unrecognized register → default-warm shape.
/// </summary>
public class ThemeR2RegisterDrivenInnerThoughtTests
{
    private static ContextSnapshot SnapshotWith(string? register) => new()
    {
        CharacterState = new CharacterStateDoc
        {
            Name               = "Ani",
            PrimaryContactName = "Mark",
            CoreTraits         = ["warm", "curious"],
            Occupation         = "Bookstore",
            SelfConcept        = ["someone who notices light"],
            NatureGrounding    = ["I read romance novels in the back"],
        },
        DesireState           = new DesireState(),
        EmotionalState        = new EmotionalState(),
        RecentMemory          = [],
        RelevantMemory        = [],
        OpenLoops             = [],
        Perceptions           = [],
        RecentHistory         = [],
        SimilarRecentThoughts = [],
        BuiltAt               = DateTimeOffset.UtcNow,
        DominantRegister      = register,
    };

    [Fact]
    public void InnerThought_DefaultWarmRegister_UsesNaturalTextureRule()
    {
        var (system, _) = PromptBuilder.BuildInnerThoughtPrompt(SnapshotWith("Tenderness"));

        system.Should().Contain("natural texture",
            "default-warm variant retains the existing flexible texture rule");
        system.Should().NotContain("Reflective texture");
        system.Should().NotContain("Honest texture");
    }

    [Fact]
    public void InnerThought_ReflectiveRegister_UsesReflectiveTextureRule()
    {
        var (system, _) = PromptBuilder.BuildInnerThoughtPrompt(SnapshotWith("Existential"));

        system.Should().Contain("Reflective texture");
        system.Should().Contain("low arousal, observation over assertion",
            "reflective variant must guide the inner thought toward observation, not assertion");
    }

    [Fact]
    public void InnerThought_HonestEdgeRegister_UsesHonestTextureRule()
    {
        var (system, _) = PromptBuilder.BuildInnerThoughtPrompt(SnapshotWith("Frustration"));

        system.Should().Contain("Honest texture");
        system.Should().Contain("name what's hurting or frustrating without softening");
    }

    [Fact]
    public void InnerThought_BehaviorDiffers_AcrossThreeVariants()
    {
        // R.0 contract bullet 2 — applied to InnerThoughtPromptCommand.
        var (warm, _)       = PromptBuilder.BuildInnerThoughtPrompt(SnapshotWith("Curiosity"));
        var (reflective, _) = PromptBuilder.BuildInnerThoughtPrompt(SnapshotWith("Wistful"));
        var (edge, _)       = PromptBuilder.BuildInnerThoughtPrompt(SnapshotWith("Longing"));

        warm.Should().NotBe(reflective);
        warm.Should().NotBe(edge);
        reflective.Should().NotBe(edge);
    }

    [Fact]
    public void InnerThought_NullRegister_DefaultsToWarmTexture()
    {
        var (system, _) = PromptBuilder.BuildInnerThoughtPrompt(SnapshotWith(null));

        system.Should().Contain("natural texture",
            "null register must default to warm-shape (no regression for cold-start state)");
    }

    [Fact]
    public void InnerThought_AllVariants_PreserveFirstPersonDiscipline()
    {
        // R.0 spec discipline preservation: register-driven branch must NOT
        // remove the existing first-person rules. Inner-thought's identity
        // discipline ("write entirely in first person", "no 'you'") is
        // load-bearing and protected.
        foreach (var register in new[] { "Tenderness", "Wistful", "Frustration", null })
        {
            var (system, _) = PromptBuilder.BuildInnerThoughtPrompt(SnapshotWith(register));
            system.Should().Contain("first person",
                $"register='{register ?? "null"}' must preserve the first-person rule");
            system.Should().Contain("Do NOT use \"you\"",
                $"register='{register ?? "null"}' must preserve the no-second-person rule");
        }
    }
}
