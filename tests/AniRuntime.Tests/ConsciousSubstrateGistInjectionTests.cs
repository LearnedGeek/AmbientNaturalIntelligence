using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Loops;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AniRuntime.Tests;

/// <summary>
/// Theme M Phase M.1 (May 5, 2026 evening) — spec tests for the gist
/// injection consumer (<see cref="ConsciousSubstrateGistObservation.ComposeAndInjectAsync"/>).
///
/// **Behavior matrix under test:**
/// - Composer null → original prompt unchanged.
/// - Flag off → original prompt unchanged (defensive — even if composer
///   produces content, injection is gated on the consumer's flag check).
/// - Flag on + composer returns Empty → original prompt unchanged.
/// - Flag on + composer returns content → gist prepended as substrate
///   block followed by blank line + original prompt.
/// - Composer throws → original prompt unchanged (best-effort semantics).
///
/// Strict-mock <see cref="IConsciousSubstrateGist"/> with explicit setups
/// for each test path.
/// </summary>
public class ConsciousSubstrateGistInjectionTests
{
    private const string OriginalPrompt = "Mark just texted: \"how was your day?\"";

    private static AniOptions Options(bool enabled) => new()
    {
        ConsciousSubstrateGistEnabled   = enabled,
        ConsciousSubstrateGistMaxTokens = 200,
    };

    private static ContextSnapshot Snapshot() => new();

    [Fact]
    public async Task ComposeAndInjectAsync_NullComposer_ReturnsOriginalPrompt()
    {
        var result = await ConsciousSubstrateGistObservation.ComposeAndInjectAsync(
            composer:       null,
            snapshot:       Snapshot(),
            aniOptions:     Options(enabled: true),
            promptUserText: OriginalPrompt,
            log:            NullLogger.Instance,
            ct:             CancellationToken.None);

        result.Should().Be(OriginalPrompt);
    }

    [Fact]
    public async Task ComposeAndInjectAsync_FlagOff_DoesNotInjectEvenWhenComposerProducesContent()
    {
        // Defensive flag check at the consumer surface — a future composer
        // that ignores the flag must not cause injection into a flag-off
        // deployment.
        var mockComposer = new Mock<IConsciousSubstrateGist>(MockBehavior.Strict);
        mockComposer.Setup(c => c.ComputeGistAsync(It.IsAny<ContextSnapshot>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConsciousSubstrateGist
            {
                Composed   = "your register state right now: warmth high (0.78)",
                Slices     = new GistSliceFlags { RegisterState = true },
                TokenCount = 12,
            });

        var result = await ConsciousSubstrateGistObservation.ComposeAndInjectAsync(
            composer:       mockComposer.Object,
            snapshot:       Snapshot(),
            aniOptions:     Options(enabled: false),
            promptUserText: OriginalPrompt,
            log:            NullLogger.Instance,
            ct:             CancellationToken.None);

        result.Should().Be(OriginalPrompt, "flag-off path must not inject even when composer produces content");
    }

    [Fact]
    public async Task ComposeAndInjectAsync_FlagOnEmptyGist_ReturnsOriginalPrompt()
    {
        var mockComposer = new Mock<IConsciousSubstrateGist>(MockBehavior.Strict);
        mockComposer.Setup(c => c.ComputeGistAsync(It.IsAny<ContextSnapshot>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConsciousSubstrateGist.Empty);

        var result = await ConsciousSubstrateGistObservation.ComposeAndInjectAsync(
            composer:       mockComposer.Object,
            snapshot:       Snapshot(),
            aniOptions:     Options(enabled: true),
            promptUserText: OriginalPrompt,
            log:            NullLogger.Instance,
            ct:             CancellationToken.None);

        result.Should().Be(OriginalPrompt);
    }

    [Fact]
    public async Task ComposeAndInjectAsync_FlagOnWithContent_PrependsGistAsSubstrateBlock()
    {
        const string gistText = "your register state right now: warmth high (0.78), energy mid (0.55).";
        var mockComposer = new Mock<IConsciousSubstrateGist>(MockBehavior.Strict);
        mockComposer.Setup(c => c.ComputeGistAsync(It.IsAny<ContextSnapshot>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConsciousSubstrateGist
            {
                Composed   = gistText,
                Slices     = new GistSliceFlags { RegisterState = true },
                TokenCount = 18,
            });

        var result = await ConsciousSubstrateGistObservation.ComposeAndInjectAsync(
            composer:       mockComposer.Object,
            snapshot:       Snapshot(),
            aniOptions:     Options(enabled: true),
            promptUserText: OriginalPrompt,
            log:            NullLogger.Instance,
            ct:             CancellationToken.None);

        result.Should().StartWith(gistText, "gist must be prepended at the top of the prompt");
        result.Should().Contain(OriginalPrompt, "original prompt content must be preserved verbatim");
        result.Should().Be(gistText + "\n\n" + OriginalPrompt,
            "format is: gist + blank line + original prompt");
    }

    [Fact]
    public async Task ComposeAndInjectAsync_ComposerThrows_ReturnsOriginalPromptUnchanged()
    {
        // Best-effort semantics: a composer failure must not break dispatch.
        // The original prompt is used as a fallback; the failure logs at Warning.
        var mockComposer = new Mock<IConsciousSubstrateGist>(MockBehavior.Strict);
        mockComposer.Setup(c => c.ComputeGistAsync(It.IsAny<ContextSnapshot>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("simulated composer failure"));

        var result = await ConsciousSubstrateGistObservation.ComposeAndInjectAsync(
            composer:       mockComposer.Object,
            snapshot:       Snapshot(),
            aniOptions:     Options(enabled: true),
            promptUserText: OriginalPrompt,
            log:            NullLogger.Instance,
            ct:             CancellationToken.None);

        result.Should().Be(OriginalPrompt, "composer failures must not propagate; original prompt is the fallback");
    }
}
