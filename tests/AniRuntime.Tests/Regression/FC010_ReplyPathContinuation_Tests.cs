using System.Reflection;
using System.Runtime.CompilerServices;
using AniRuntime.Core.Models;
using FluentAssertions;
using Xunit;

namespace AniRuntime.Tests.Regression;

/// <summary>
/// SPEC regression tests for <b>FC-010 — Reply path can't engage with prior
/// dispatched content (continuation / walkback gap)</b>.
///
/// Named 2026-05-14 during architecture discussion. This is the gating-
/// asymmetry failure Mark surfaced — *"we have a gap here because she
/// reached out, and then should be able to reply. Either we need to block
/// the initial outreach, or allow her to respond. Gating only one created
/// the issue."*
///
/// THE SPEC (architectural-existence level):
///   The reply path must expose an architectural primitive for engaging
///   with prior dispatched content. When Ani has dispatched a message and
///   Mark replies asking about its content, the reply pipeline must select
///   between:
///     (1) Natural expansion — engage with prior content as new information
///     (2) Substrate-supported callback — retrieve grounded substrate
///     (3) Honest walkback — acknowledge prior content was speculative
///   The primitive is currently absent. Self-echo blocks any verbatim
///   re-statement; no producer or strategy provides expansion or walkback.
///   SafeAck is the only available path.
///
/// THIS SPEC PROBES: does any type in the loaded AniRuntime.* assemblies
/// expose continuation/walkback machinery (an enum, strategy interface,
/// property, or named handler)? If nothing matches, FC-010 is OPEN at the
/// architectural-primitive layer.
///
/// EMPIRICAL ANCHOR (May 12 21:35–21:51 windshield case, re-classified):
///   - 21:35 Ani dispatches "I just got home... found this on my windshield"
///     (Self / factual / novel — ALLOW under reframed FC-002)
///   - 21:50 Mark asks "What did you find on your windshield?"
///   - 21:50:55 model parrots prior outreach (no substrate; no walkback path)
///   - 21:50:55 self-echo correctly catches parrot
///   - SafeAck dispatched
///   The original outreach was legitimate; the gap is downstream. FC-010 is
///   the actual link in the windshield chain (not FC-001 — misnamed).
///
/// TEST CATEGORY: SPEC. PASS = continuation primitive exists in the
/// architecture. FAIL = FC-010 OPEN at the primitive-existence layer. Fix
/// space is open (a new strategy interface, an enum on CognitiveArtifact,
/// a producer-selection step in ConversationReplyPhase, etc.); whichever
/// shape the fix takes, the SPEC stays.
/// </summary>
public class FC010_ReplyPathContinuation_Tests
{
    /// <summary>
    /// FC-010 — SPEC: a continuation/walkback architectural primitive must
    /// exist somewhere in the AniRuntime.* assemblies. We probe via type and
    /// property name conventions any acceptable fix would use.
    /// </summary>
    [Fact]
    public void FC010_ContinuationPrimitive_ExistsInArchitecture_Spec()
    {
        var aniAssemblies = AppDomain.CurrentDomain
            .GetAssemblies()
            .Where(a => a.GetName().Name?.StartsWith("AniRuntime", StringComparison.Ordinal) == true)
            .ToList();

        aniAssemblies.Should().NotBeEmpty("AniRuntime.* assemblies must be loaded for this probe.");

        var allTypes = aniAssemblies
            .SelectMany(a =>
            {
                try { return a.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null)!; }
            })
            .Where(t => t != null)
            .Cast<Type>()
            // Skip compiler-generated types (async state machines, lambda
            // closures, <>c__DisplayClass*, iterator types, etc.) — they
            // false-positive on "Continuation" via TaskContinuation infra.
            .Where(t => t.GetCustomAttribute<CompilerGeneratedAttribute>() is null)
            .Where(t => !t.Name.Contains('<') && !t.Name.Contains('>'))
            .ToList();

        // Probe 1: a NAMED user-authored type with one of the strict
        // FC-010-vocabulary names. We use exact-name set rather than
        // substring because substring-on-"Continuation" false-positives
        // on Task.Continuation-related infrastructure.
        var continuationTypeNames = new[]
        {
            "ReplyContinuationStrategy", "ContinuationStrategy", "ContinuationMode",
            "ContinuationHandler", "ContinuationProducer",
            "ReplyEngagementMode", "EngagementMode", "ReplyEngagementStrategy",
            "WalkbackStrategy", "WalkbackHandler", "WalkbackProducer", "WalkbackMode",
        };
        var continuationTypes = allTypes
            .Where(t => continuationTypeNames.Any(name =>
                string.Equals(t.Name, name, StringComparison.Ordinal)))
            .ToList();

        // Probe 2: a property on a user-authored type (with the strict
        // vocabulary) exposing engagement-mode metadata.
        var continuationPropertyNames = new[]
        {
            "ContinuationMode", "ContinuationStrategy",
            "EngagementMode", "ReplyEngagementMode",
            "WalkbackMode", "WalkbackStrategy",
            "ReplyStrategy",
        };
        var continuationProperties = allTypes
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(p => continuationPropertyNames.Any(name =>
                string.Equals(p.Name, name, StringComparison.Ordinal)))
            .ToList();

        // Probe 3: an interface with the strict vocabulary.
        var continuationInterfaceNames = new[]
        {
            "IReplyContinuationStrategy", "IContinuationStrategy",
            "IContinuationHandler", "IWalkbackHandler",
            "IReplyEngagementStrategy",
        };
        var continuationInterfaces = allTypes
            .Where(t => t.IsInterface)
            .Where(t => continuationInterfaceNames.Any(name =>
                string.Equals(t.Name, name, StringComparison.Ordinal)))
            .ToList();

        var primitiveExists =
               continuationTypes.Count > 0
            || continuationProperties.Count > 0
            || continuationInterfaces.Count > 0;

        primitiveExists.Should().BeTrue(
            "the reply path MUST expose an architectural primitive for engaging with " +
            "prior dispatched content (natural expansion / substrate-supported callback / " +
            "honest walkback). The May 12 windshield case (21:35 outreach → 21:50 Mark " +
            "follow-up → 21:50:55 parrot caught by self-echo → SafeAck) is the empirical " +
            "anchor: without this primitive, the system has no path forward when the front " +
            "door legitimately passes content the back door cannot engage with. Fix space " +
            "is open (named type, property on CognitiveArtifact, strategy interface — any " +
            "acceptable shape). Probed strict vocabulary: " +
            $"types {{{string.Join(", ", continuationTypeNames)}}} → {continuationTypes.Count} found; " +
            $"properties {{{string.Join(", ", continuationPropertyNames)}}} → {continuationProperties.Count} found; " +
            $"interfaces {{{string.Join(", ", continuationInterfaceNames)}}} → {continuationInterfaces.Count} found. " +
            "None of these exist today; FC-010 is OPEN at the architectural-primitive layer.");
    }

    /// <summary>
    /// FC-010 — companion PIN (inverted 2026-05-14 when the FC-010 primitive
    /// landed): documents that CognitiveArtifact NOW carries a
    /// <see cref="ReplyContinuationMode"/> nullable property. The original
    /// pin asserted no such property existed; with the architectural fix
    /// shipped, the pin asserts the specific shape so future refactors
    /// can't silently drop or rename it.
    /// </summary>
    [Fact]
    public void FC010_CognitiveArtifact_CarriesContinuationModeProperty_Pin()
    {
        var prop = typeof(CognitiveArtifact)
            .GetProperty("ContinuationMode", BindingFlags.Public | BindingFlags.Instance);

        prop.Should().NotBeNull(
            "PIN: CognitiveArtifact MUST carry the FC-010 continuation-mode " +
            "primitive. If this pin fails, the property was renamed or removed " +
            "— that's a conversation, not a bug fix.");

        prop!.PropertyType.Should().Be(typeof(ReplyContinuationMode?),
            "PIN: the property MUST be nullable ReplyContinuationMode so " +
            "producers can opt-in by setting it; null = standard reply path.");

        // Verify init-only setter — the modreq token IsExternalInit on the
        // setter's return-type modifiers marks the property as init-only
        // in C# 9+. (CanWrite returns true for init-only properties from
        // reflection's POV; the init/set distinction is at the language
        // layer, so we check the IL-level marker directly.)
        var setter = prop.GetSetMethod();
        setter.Should().NotBeNull("init-only properties still expose a set method via reflection");
        var isInitOnly = setter!.ReturnParameter.GetRequiredCustomModifiers()
            .Any(t => t.FullName == "System.Runtime.CompilerServices.IsExternalInit");
        isInitOnly.Should().BeTrue(
            "PIN: CognitiveArtifact properties are init-only; ContinuationMode " +
            "follows the same convention. Verifies immutability after construction.");
    }
}
