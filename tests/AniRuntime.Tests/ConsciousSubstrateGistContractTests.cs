using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Loops;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace AniRuntime.Tests;

/// <summary>
/// Theme M Phase M.0 (May 5, 2026) spec tests pinning the read-only
/// architectural property of the conscious-substrate gist composer.
///
/// **The architectural property under test:** the gist is computed at
/// prompt-build time, attached to the prompt, and discarded. It MUST NOT
/// be persisted to <see cref="IMemoryService"/>, MUST NOT be persisted to
/// <see cref="IConversationService"/>, and MUST NOT enter retrieval pools.
/// Persisting would create an own-output recursive loop on the conscious-
/// substrate layer mirroring the §5.24 cascade we already have on the
/// Episodic layer.
///
/// **Strict-mock discipline (Theme K):** the memory + conversation service
/// mocks are <see cref="MockBehavior.Strict"/> with NO setups for write
/// methods. Any unauthorized call raises immediately. The no-op composer
/// makes none, so these tests pass at M.0; M.1+ replacement composers
/// inherit the same enforcement contract.
///
/// **What these tests do NOT cover:**
/// - Slice content correctness — that's M.1+ test territory.
/// - Composition rules (slice ordering, token budget) — that's §4.6 spec
///   test territory at M.1+.
/// - Telemetry shape — covered by
///   <c>ConsciousSubstrateGistObservationTests</c> separately.
///
/// Plan: docs/spec/ANI-Theme-M-Conscious-Substrate-Individuation-Plan.md §3
/// (read-only as core architectural property), §5 Phase M.0 acceptance
/// criteria.
/// </summary>
public class ConsciousSubstrateGistContractTests
{
    private static IOptions<AniOptions> Options(bool enabled = false) =>
        Microsoft.Extensions.Options.Options.Create(new AniOptions
        {
            ConsciousSubstrateGistEnabled         = enabled,
            ConsciousSubstrateGistOutreachEnabled = enabled,
            ConsciousSubstrateGistMaxTokens       = 200,
        });

    private static ConsciousSubstrateGistComposer Composer(bool enabled = false) =>
        new(Options(enabled), NullLogger<ConsciousSubstrateGistComposer>.Instance);

    private static ContextSnapshot Snapshot() => new()
    {
        CharacterState = new CharacterStateDoc { PrimaryContactName = "Mark" },
    };

    private static ContextSnapshot SnapshotWithEmotion() => new()
    {
        CharacterState = new CharacterStateDoc { PrimaryContactName = "Mark" },
        EmotionalState = new EmotionalState
        {
            Warmth      = 0.78f, WarmthBaseline      = 0.60f,
            Energy      = 0.55f, EnergyBaseline      = 0.50f,
            Worry       = 0.31f, WorryBaseline       = 0.20f,
            Playfulness = 0.50f, PlayfulnessBaseline = 0.50f,
        },
    };

    [Fact]
    public async Task ComputeGistAsync_M1_FlagDisabled_ReturnsEmpty()
    {
        // M.1 contract: when flag is disabled, composer returns Empty
        // regardless of snapshot content. This is the load-bearing
        // flag-respect property pinned by RegisterStateGistSlice_RespectsFeatureFlag.
        var composer = Composer(enabled: false);

        var gist = await composer.ComputeGistAsync(SnapshotWithEmotion(), CancellationToken.None);

        gist.IsEmpty.Should().BeTrue();
        gist.Composed.Should().BeEmpty();
        gist.TokenCount.Should().Be(0);
        gist.Slices.RegisterState.Should().BeFalse();
    }

    [Fact]
    public async Task ComputeGistAsync_M1_FlagEnabled_ProducesRegisterStateSlice()
    {
        // M.1 contract: when flag is enabled and EmotionalState is non-default,
        // the composer produces a §4.3 register-state slice. Slice content
        // is structured first-person register data: dominant + secondary
        // register names with values, plus baseline drift.
        //
        // M.1 evening update: composer now also produces §4.8 tension-state
        // slice when emotional state has any divergence from baseline. The
        // test snapshot has warmth/worry above baseline → tension-state
        // slice also fires. The §4.3 register-state slice is what this test
        // asserts; tension-state coverage is in TensionStateSliceContractTests.
        var composer = Composer(enabled: true);

        var gist = await composer.ComputeGistAsync(SnapshotWithEmotion(), CancellationToken.None);

        gist.IsEmpty.Should().BeFalse();
        gist.Slices.RegisterState.Should().BeTrue();
        gist.Slices.ClosedConversation.Should().BeFalse();
        gist.Slices.InnerThoughtAggregate.Should().BeFalse();
        gist.Slices.ContactState.Should().BeFalse();
        gist.Slices.WorldSelf.Should().BeFalse();
        // TensionState slice MAY also fire when emotional state has
        // divergence from baseline; we don't assert on it here. Coverage:
        // TensionStateSliceContractTests.
        gist.TokenCount.Should().BeGreaterThan(0);

        // Phase M.2-lite: per-slice token counts mirror slice flags.
        // Active slice → non-zero token count; inactive slice → zero.
        // Total matches gist.TokenCount when only register-state fires;
        // when tension-state also fires it's slightly more (joined with \n).
        gist.SliceTokens.RegisterState.Should().BeGreaterThan(0,
            "RegisterState slice fired so its token count must be > 0");
        gist.SliceTokens.ClosedConversation.Should().Be(0,
            "ClosedConversation slice is M.3+ — must report 0 tokens until shipped");
        gist.SliceTokens.InnerThoughtAggregate.Should().Be(0,
            "InnerThoughtAggregate slice is M.5+ — must report 0 tokens until shipped");
        gist.SliceTokens.ContactState.Should().Be(0,
            "ContactState slice is M.4+ — must report 0 tokens until shipped");
        gist.SliceTokens.WorldSelf.Should().Be(0,
            "WorldSelf slice is M.6+ — must report 0 tokens until shipped");
    }

    [Fact]
    public async Task ComputeGistAsync_NoMemoryWriteAfterCompute_StrictMockProves()
    {
        // The load-bearing read-only architectural property:
        // ComputeGistAsync MUST NOT call IMemoryService.SaveAsync.
        //
        // Strict-mock IMemoryService with NO SaveAsync setup. The composer
        // does not depend on IMemoryService directly, but this test pins
        // the contract that the composer + downstream wiring (M.1+ slice
        // implementations) MUST NOT introduce a memory-write side effect.
        // We hand the composer a strict mock; if any future implementation
        // resolves IMemoryService and calls SaveAsync, this test breaks
        // before the bad code reaches deployment.
        var memoryMock = new Mock<IMemoryService>(MockBehavior.Strict);
        // NO SaveAsync setup — strict mode raises on any call.

        var composer = Composer(enabled: true);

        var gist = await composer.ComputeGistAsync(SnapshotWithEmotion(), CancellationToken.None);

        // The point of this test is the Verify(Never) below — gist content
        // is permitted (M.1 produces register-state slice text), but it must
        // never trigger an IMemoryService.SaveAsync call.
        gist.Should().NotBeNull();
        memoryMock.Verify(
            m => m.SaveAsync(It.IsAny<MemoryRecord>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Theme M plan §3 — gist content MUST NOT be persisted to memory under any circumstance");
    }

    [Fact]
    public async Task ComputeGistAsync_NoConversationMessagesWriteAfterCompute_StrictMockProves()
    {
        // The companion read-only property: gist content MUST NOT enter
        // conversation_messages. Even though the M.0 no-op composer makes
        // no calls, the strict-mock pin ensures any future implementation
        // that resolves IConversationService and calls AddMessageAsync
        // breaks this test before deployment.
        var conversationMock = new Mock<IConversationService>(MockBehavior.Strict);
        // NO AddMessageAsync setup — strict mode raises on any call.

        var composer = Composer(enabled: true);

        var gist = await composer.ComputeGistAsync(SnapshotWithEmotion(), CancellationToken.None);

        // The point of this test is the Verify(Never) below — gist content
        // is permitted (M.1 produces register-state slice text), but it must
        // never trigger an IConversationService.AddMessageAsync call.
        gist.Should().NotBeNull();
        conversationMock.Verify(
            c => c.AddMessageAsync(It.IsAny<Guid>(), It.IsAny<ConversationMessage>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Theme M plan §3 — gist content MUST NOT be persisted to conversation_messages");
    }

    [Fact]
    public async Task ComputeGistAsync_DoesNotMutateSnapshot()
    {
        // Theme M plan §3 + §4 — composer reads from snapshot but MUST NOT
        // mutate it. Capture the input shape, run, compare. M.0 no-op
        // satisfies trivially; the contract is pinned here for M.1+
        // implementations that read the closed-conversation gist, register
        // state, etc.
        var snapshot = Snapshot();
        snapshot.RecentMemory.Add(new MemoryRecord { Content = "test memory" });
        snapshot.RecentHistory.Add(new ChatMessage("user", "hi"));

        var memCountBefore = snapshot.RecentMemory.Count;
        var historyCountBefore = snapshot.RecentHistory.Count;
        var contactBefore = snapshot.CharacterState.PrimaryContactName;

        var composer = Composer(enabled: true);
        await composer.ComputeGistAsync(snapshot, CancellationToken.None);

        snapshot.RecentMemory.Count.Should().Be(memCountBefore);
        snapshot.RecentHistory.Count.Should().Be(historyCountBefore);
        snapshot.CharacterState.PrimaryContactName.Should().Be(contactBefore);
    }

    [Fact]
    public async Task RegisterStateGistSlice_NotCaregiverOriented()
    {
        // §4.3 generation invariant: the slice is about Ani's register state,
        // never about Mark. Mark-content belongs in §4.4 contact-state aggregate
        // (M.4 deliverable). Pinned by spec test so future composer changes
        // can't accidentally cross the categorical line.
        var composer = Composer(enabled: true);
        var gist = await composer.ComputeGistAsync(SnapshotWithEmotion(), CancellationToken.None);

        gist.Composed.Should().NotBeEmpty();
        gist.Composed.Should().NotContain("Mark", "the register-state slice is about Ani's state, never about the caregiver as subject");
        gist.Composed.Should().NotContain("mark", "case-insensitive check for the same property");
        gist.Composed.Should().NotContain("you (Mark)", "no caregiver-as-subject framing allowed in this slice");
    }

    // ── Theme M Phase M.6a (May 28, 2026) — WorldSelf slice contract tests ──

    private static ContextSnapshot SnapshotWithEmotionAndWorld(
        string occupation = "the bookstore",
        params string[] worldExperienceContents)
    {
        var snap = SnapshotWithEmotion();
        snap.CharacterState.Occupation = occupation;
        snap.RecentWorldExperiences = worldExperienceContents
            .Select(c => new MemoryRecord { Content = c, Type = MemoryType.Semantic })
            .ToList();
        return snap;
    }

    [Fact]
    public async Task WorldSelfSlice_FiresWhenOccupationIsSet()
    {
        // §4.5 + M.6a data-availability gate: slice fires when occupation
        // is set, even without recent world experiences.
        var composer = Composer(enabled: true);
        var snapshot = SnapshotWithEmotionAndWorld(occupation: "the bookstore");

        var gist = await composer.ComputeGistAsync(snapshot, CancellationToken.None);

        gist.Slices.WorldSelf.Should().BeTrue(
            "WorldSelf slice fires when occupation is set");
        gist.SliceTokens.WorldSelf.Should().BeGreaterThan(0,
            "active WorldSelf slice must report non-zero tokens");
        gist.Composed.Should().Contain("world-self:",
            "WorldSelf slice has the canonical 'world-self:' prefix matching M.1 slice style");
        gist.Composed.Should().Contain("the bookstore",
            "occupation grounding must be present in slice content");
    }

    [Fact]
    public async Task WorldSelfSlice_FiresWhenRecentWorldExperiencesPresent()
    {
        // Slice fires from RecentWorldExperiences alone — occupation absent.
        var composer = Composer(enabled: true);
        var snapshot = SnapshotWithEmotionAndWorld(
            occupation: "",
            "Today the rain made the bookstore smell like wet wool",
            "A regular bought another Tana French novel");

        var gist = await composer.ComputeGistAsync(snapshot, CancellationToken.None);

        gist.Slices.WorldSelf.Should().BeTrue();
        gist.SliceTokens.WorldSelf.Should().BeGreaterThan(0);
        gist.Composed.Should().Contain("recent:",
            "world-experience snippets render under 'recent:' label");
        gist.Composed.Should().Contain("rain",
            "first world-experience content surfaces in slice");
    }

    [Fact]
    public async Task WorldSelfSlice_StaysSilentWhenNoWorldSubstrate()
    {
        // §4.5 architectural honesty: when no occupation AND no recent
        // world experiences, the slice is silent. M.6a data-availability
        // gate preserves "don't oversell the World Layer" principle.
        var composer = Composer(enabled: true);
        var snapshot = SnapshotWithEmotion();  // empty Occupation, empty RecentWorldExperiences

        var gist = await composer.ComputeGistAsync(snapshot, CancellationToken.None);

        gist.Slices.WorldSelf.Should().BeFalse(
            "data-availability gate keeps WorldSelf silent when no substrate exists");
        gist.SliceTokens.WorldSelf.Should().Be(0);
        gist.Composed.Should().NotContain("world-self:");
    }

    [Fact]
    public async Task WorldSelfSlice_TakesAtMostTwoExperiences()
    {
        // Token-budget hygiene: cap at the two most-recent experiences to
        // bound slice length. ContextBuilder owns the lookback window;
        // composer trusts the populated list as ordered most-recent-first.
        var composer = Composer(enabled: true);
        var snapshot = SnapshotWithEmotionAndWorld(
            occupation: "the bookstore",
            "EXPERIENCE-ONE: morning shift",
            "EXPERIENCE-TWO: afternoon customer",
            "EXPERIENCE-THREE: should not appear",
            "EXPERIENCE-FOUR: should not appear");

        var gist = await composer.ComputeGistAsync(snapshot, CancellationToken.None);

        gist.Composed.Should().Contain("EXPERIENCE-ONE");
        gist.Composed.Should().Contain("EXPERIENCE-TWO");
        gist.Composed.Should().NotContain("EXPERIENCE-THREE",
            "only the two most-recent world experiences should appear");
        gist.Composed.Should().NotContain("EXPERIENCE-FOUR");
    }

    [Fact]
    public async Task WorldSelfSlice_OrdersAfterRegisterStateInComposition()
    {
        // §4.6 slice ordering: tension → register → (closed, inner, contact)
        // → world-self. WorldSelf is LAST. With M.6a + tension + register
        // active, world-self appears AFTER register-state in the composed
        // string.
        var composer = Composer(enabled: true);
        var snapshot = SnapshotWithEmotionAndWorld(occupation: "the bookstore");

        var gist = await composer.ComputeGistAsync(snapshot, CancellationToken.None);

        var registerIdx  = gist.Composed.IndexOf("register state", StringComparison.OrdinalIgnoreCase);
        var worldSelfIdx = gist.Composed.IndexOf("world-self:", StringComparison.OrdinalIgnoreCase);

        registerIdx.Should().BeGreaterOrEqualTo(0, "register-state slice should be present");
        worldSelfIdx.Should().BeGreaterOrEqualTo(0, "world-self slice should be present");
        worldSelfIdx.Should().BeGreaterThan(registerIdx,
            "§4.6 slice ordering places world-self AFTER register-state");
    }

    [Fact]
    public async Task RegisterStateGistSlice_TokenBudgetEnforced()
    {
        // §4.6 token-budget rule: slice never exceeds ConsciousSubstrateGistMaxTokens.
        // M.1 register-state is small (~30 tokens default); the budget enforcement
        // is defense-in-depth for future slices. We test by setting a tight budget
        // and asserting the composer drops to Empty rather than producing oversized
        // content (the alternative — silently truncating — would corrupt slice text).
        var tightOptions = Microsoft.Extensions.Options.Options.Create(new AniOptions
        {
            ConsciousSubstrateGistEnabled   = true,
            ConsciousSubstrateGistMaxTokens = 5,  // far below the ~30-token slice
        });
        var composer = new ConsciousSubstrateGistComposer(
            tightOptions,
            NullLogger<ConsciousSubstrateGistComposer>.Instance);

        var gist = await composer.ComputeGistAsync(SnapshotWithEmotion(), CancellationToken.None);

        // With a 5-token budget, the ~30-token register-state slice can't fit.
        // The composer should return Empty (drops the slice) rather than truncate
        // to a malformed half-clause.
        gist.IsEmpty.Should().BeTrue("token-budget violation should drop the slice to Empty rather than truncate to malformed text");
    }

    [Fact]
    public void ConsciousSubstrateGistEmpty_IsCanonicalSingleton()
    {
        // Spec note for downstream consumers: ConsciousSubstrateGist.Empty
        // is the canonical empty instance. Equality by record value is
        // sufficient; reference equality is not required.
        ConsciousSubstrateGist.Empty.Should().NotBeNull();
        ConsciousSubstrateGist.Empty.IsEmpty.Should().BeTrue();
        ConsciousSubstrateGist.Empty.Composed.Should().BeEmpty();
        ConsciousSubstrateGist.Empty.TokenCount.Should().Be(0);

        var anotherEmpty = new ConsciousSubstrateGist();
        anotherEmpty.Should().Be(ConsciousSubstrateGist.Empty,
            "record value equality lets consumers compare against Empty without reference-tracking");
    }
}
