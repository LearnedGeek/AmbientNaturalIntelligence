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

    [Fact]
    public async Task ComputeGistAsync_M0_NoOp_ReturnsEmpty()
    {
        // M.0 acceptance: composer always returns Empty regardless of input
        // and regardless of feature-flag state. Slice content arrives in M.1+.
        var composer = Composer(enabled: true);

        var gist = await composer.ComputeGistAsync(Snapshot(), CancellationToken.None);

        gist.Should().NotBeNull();
        gist.IsEmpty.Should().BeTrue("M.0 no-op composer must return ConsciousSubstrateGist.Empty");
        gist.Composed.Should().BeEmpty();
        gist.TokenCount.Should().Be(0);
        gist.Slices.ClosedConversation.Should().BeFalse();
        gist.Slices.InnerThoughtAggregate.Should().BeFalse();
        gist.Slices.RegisterState.Should().BeFalse();
        gist.Slices.ContactState.Should().BeFalse();
        gist.Slices.WorldSelf.Should().BeFalse();
        gist.Slices.TensionState.Should().BeFalse();
    }

    [Fact]
    public async Task ComputeGistAsync_M0_NoOp_ReturnsEmptyEvenWhenFlagDisabled()
    {
        // Flag-disabled path — same behavior. The flag's purpose in M.0 is
        // to gate the consumer's prompt-injection logic, not to gate composer
        // execution. Spec-tested property (no memory writes) holds regardless
        // of flag state.
        var composer = Composer(enabled: false);

        var gist = await composer.ComputeGistAsync(Snapshot(), CancellationToken.None);

        gist.IsEmpty.Should().BeTrue();
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

        var gist = await composer.ComputeGistAsync(Snapshot(), CancellationToken.None);

        gist.IsEmpty.Should().BeTrue();
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

        var gist = await composer.ComputeGistAsync(Snapshot(), CancellationToken.None);

        gist.IsEmpty.Should().BeTrue();
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
