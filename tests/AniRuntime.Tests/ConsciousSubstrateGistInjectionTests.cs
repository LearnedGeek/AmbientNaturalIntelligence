using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Loops;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AniRuntime.Tests;

/// <summary>
/// Spec tests for the gist injection consumer
/// (<see cref="ConsciousSubstrateGistObservation.ComposeAndInjectAsync"/>).
///
/// **G.1 contract (2026-06-11):** the method takes BOTH the system and
/// user prompt text plus a <c>directiveInSystem</c> flag, and returns a
/// <see cref="PromptPair"/>. When the gist has content:
/// - <c>directiveInSystem=true</c>  → gist appends to SYSTEM, user unchanged
/// - <c>directiveInSystem=false</c> → gist prepends to USER (legacy), system unchanged
///
/// Empirical anchor: 2026-06-10 22:17 production self-echo cascade.
/// Previously, when LeanConversationPromptDirectiveInSystem=true, the
/// gist alone became a non-empty user-role turn AFTER Mark's actual
/// current turn from RecentHistory — the model lifted phrasings from
/// the gist as if it were a fresh inbound. Issue #92 §G.1.
/// </summary>
public class ConsciousSubstrateGistInjectionTests
{
    private const string OriginalSystem = "You are Ani. Be kind. Be honest.";
    private const string OriginalUser   = "Mark just texted: \"how was your day?\"";

    // F-1 Phase 4 (2026-08-18) — every injected gist body is wrapped with
    // these treatment-directive boundary markers. Kept as constants so the
    // regression tests fail loudly if the marker text is changed without
    // deliberate intent (this is the load-bearing string the model reads).
    private const string SubstrateOpen  = "[SUBSTRATE — reference only, DO NOT adopt this voice]";
    private const string SubstrateClose = "[/SUBSTRATE]";
    private static string Wrap(string body) => $"{SubstrateOpen}\n{body}\n{SubstrateClose}";

    private static AniOptions Options(bool enabled) => new()
    {
        ConsciousSubstrateGistEnabled   = enabled,
        ConsciousSubstrateGistMaxTokens = 200,
    };

    private static ContextSnapshot Snapshot() => new();

    [Fact]
    public async Task ComposeAndInjectAsync_NullComposer_ReturnsBothPromptsUnchanged()
    {
        var result = await ConsciousSubstrateGistObservation.ComposeAndInjectAsync(
            composer:           null,
            snapshot:           Snapshot(),
            aniOptions:         Options(enabled: true),
            promptSystemText:   OriginalSystem,
            promptUserText:     OriginalUser,
            directiveInSystem:  true,
            log:                NullLogger.Instance,
            ct:                 CancellationToken.None);

        result.System.Should().Be(OriginalSystem);
        result.User.Should().Be(OriginalUser);
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
                Composed   = "recent-thread: bookstore chatter; warm tone.",
                Slices     = new GistSliceFlags { ClosedConversation = true },
                TokenCount = 12,
            });

        var result = await ConsciousSubstrateGistObservation.ComposeAndInjectAsync(
            composer:           mockComposer.Object,
            snapshot:           Snapshot(),
            aniOptions:         Options(enabled: false),
            promptSystemText:   OriginalSystem,
            promptUserText:     OriginalUser,
            directiveInSystem:  true,
            log:                NullLogger.Instance,
            ct:                 CancellationToken.None);

        result.System.Should().Be(OriginalSystem, "flag-off path must not inject into system");
        result.User.Should().Be(OriginalUser,     "flag-off path must not inject into user");
    }

    [Fact]
    public async Task ComposeAndInjectAsync_FlagOnEmptyGist_ReturnsBothPromptsUnchanged()
    {
        var mockComposer = new Mock<IConsciousSubstrateGist>(MockBehavior.Strict);
        mockComposer.Setup(c => c.ComputeGistAsync(It.IsAny<ContextSnapshot>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConsciousSubstrateGist.Empty);

        var result = await ConsciousSubstrateGistObservation.ComposeAndInjectAsync(
            composer:           mockComposer.Object,
            snapshot:           Snapshot(),
            aniOptions:         Options(enabled: true),
            promptSystemText:   OriginalSystem,
            promptUserText:     OriginalUser,
            directiveInSystem:  true,
            log:                NullLogger.Instance,
            ct:                 CancellationToken.None);

        result.System.Should().Be(OriginalSystem);
        result.User.Should().Be(OriginalUser);
    }

    [Fact]
    public async Task ComposeAndInjectAsync_DirectiveInSystem_AppendsGistToSystem_UserUnchanged()
    {
        // G.1 LOAD-BEARING TEST: this is the regression check for the
        // 2026-06-10 22:17 self-echo cascade. With LeanConversationPromptDirectiveInSystem=true,
        // the gist MUST attach to the system prompt, not become a separate
        // user-role turn. The user prompt must remain unchanged so that
        // Mark's actual current turn from RecentHistory stays the only
        // user-role content the model sees.
        const string gistText = "recent-thread: bookstore chatter; warm tone. world-self: clerk in town.";
        var mockComposer = new Mock<IConsciousSubstrateGist>(MockBehavior.Strict);
        mockComposer.Setup(c => c.ComputeGistAsync(It.IsAny<ContextSnapshot>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConsciousSubstrateGist
            {
                Composed   = gistText,
                Slices     = new GistSliceFlags { ClosedConversation = true, WorldSelf = true },
                TokenCount = 18,
            });

        var result = await ConsciousSubstrateGistObservation.ComposeAndInjectAsync(
            composer:           mockComposer.Object,
            snapshot:           Snapshot(),
            aniOptions:         Options(enabled: true),
            promptSystemText:   OriginalSystem,
            promptUserText:     OriginalUser,
            directiveInSystem:  true,
            log:                NullLogger.Instance,
            ct:                 CancellationToken.None);

        result.System.Should().Be(OriginalSystem + "\n\n" + Wrap(gistText),
            "gist appends to system when directive is system-side, wrapped with F-1 Phase 4 treatment-directive markers");
        result.User.Should().Be(OriginalUser,
            "user prompt must remain unchanged so Mark's actual turn from RecentHistory stays the only user-role content");
    }

    [Fact]
    public async Task ComposeAndInjectAsync_DirectiveInSystem_EmptyUserPrompt_GistDoesNotBecomeUserTurn()
    {
        // The empirical anchor case. LeanConversationPromptDirectiveInSystem=true
        // makes the user prompt empty. Pre-G.1, the gist became a non-empty
        // user-role message that landed after Mark's actual turn in RecentHistory.
        // Post-G.1, the gist routes to system and the user remains empty.
        const string gistText = "recent-thread: bookstore chatter; warm tone.";
        var mockComposer = new Mock<IConsciousSubstrateGist>(MockBehavior.Strict);
        mockComposer.Setup(c => c.ComputeGistAsync(It.IsAny<ContextSnapshot>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConsciousSubstrateGist
            {
                Composed   = gistText,
                Slices     = new GistSliceFlags { ClosedConversation = true },
                TokenCount = 12,
            });

        var result = await ConsciousSubstrateGistObservation.ComposeAndInjectAsync(
            composer:           mockComposer.Object,
            snapshot:           Snapshot(),
            aniOptions:         Options(enabled: true),
            promptSystemText:   OriginalSystem,
            promptUserText:     string.Empty,  // Lean directive folded into system → user is empty
            directiveInSystem:  true,
            log:                NullLogger.Instance,
            ct:                 CancellationToken.None);

        result.System.Should().Be(OriginalSystem + "\n\n" + Wrap(gistText),
            "gist appends to system, wrapped with F-1 Phase 4 treatment-directive markers");
        result.User.Should().BeEmpty(
            "G.1 invariant: gist MUST NOT populate the user prompt when directive is system-side — that's the second-user-role-turn cascade root cause");
    }

    [Fact]
    public async Task ComposeAndInjectAsync_DirectiveInUser_PrependsGistToUser_SystemUnchanged()
    {
        // Legacy path: when the directive is user-side (the original May-18
        // structural-role-flip OFF case), the gist prepends the user prompt
        // and the system stays untouched. This shape is documented but no
        // longer exercised in production (LeanConversationPromptDirectiveInSystem=true
        // is the default since PR #82).
        const string gistText = "recent-thread: bookstore chatter; warm tone.";
        var mockComposer = new Mock<IConsciousSubstrateGist>(MockBehavior.Strict);
        mockComposer.Setup(c => c.ComputeGistAsync(It.IsAny<ContextSnapshot>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConsciousSubstrateGist
            {
                Composed   = gistText,
                Slices     = new GistSliceFlags { ClosedConversation = true },
                TokenCount = 12,
            });

        var result = await ConsciousSubstrateGistObservation.ComposeAndInjectAsync(
            composer:           mockComposer.Object,
            snapshot:           Snapshot(),
            aniOptions:         Options(enabled: true),
            promptSystemText:   OriginalSystem,
            promptUserText:     OriginalUser,
            directiveInSystem:  false,
            log:                NullLogger.Instance,
            ct:                 CancellationToken.None);

        result.System.Should().Be(OriginalSystem,
            "system unchanged when directive is user-side");
        result.User.Should().Be(Wrap(gistText) + "\n\n" + OriginalUser,
            "gist prepends user prompt — legacy May-18 shape, wrapped with F-1 Phase 4 treatment-directive markers");
    }

    [Fact]
    public async Task ComposeAndInjectAsync_ComposerThrows_ReturnsBothPromptsUnchanged()
    {
        // Best-effort semantics: a composer failure must not break dispatch.
        // Both prompts are used as fallback; the failure logs at Warning.
        var mockComposer = new Mock<IConsciousSubstrateGist>(MockBehavior.Strict);
        mockComposer.Setup(c => c.ComputeGistAsync(It.IsAny<ContextSnapshot>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("simulated composer failure"));

        var result = await ConsciousSubstrateGistObservation.ComposeAndInjectAsync(
            composer:           mockComposer.Object,
            snapshot:           Snapshot(),
            aniOptions:         Options(enabled: true),
            promptSystemText:   OriginalSystem,
            promptUserText:     OriginalUser,
            directiveInSystem:  true,
            log:                NullLogger.Instance,
            ct:                 CancellationToken.None);

        result.System.Should().Be(OriginalSystem, "composer failures must not propagate");
        result.User.Should().Be(OriginalUser,     "composer failures must not propagate");
    }
}
