using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Loops.Pipeline;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using DispatchVerdict = AniRuntime.Core.Models.OutputGateVerdict;

namespace AniRuntime.Tests;

/// <summary>
/// Theme P Phase P.1 (May 11, 2026) — spec tests for
/// <see cref="FrontierVerifierHandler"/>. Pins the additive-defense
/// architecture per plan-doc §9.1 (May 11 21:36 CDT correction):
///
/// 1. <see cref="FrontierVerifierHandler.AppliesTo"/> consults ONLY
///    <see cref="AniOptions.FrontierVerifierEnabled"/>. It does not
///    reach into any other handler's behavior. No tests here exercise
///    flag-gating of other invariants — those invariants are unchanged
///    by Theme P.
/// 2. Continue on no-violation verdict + <c>P_VERIFIER_VERDICT verdict=Pass</c>
///    telemetry shape.
/// 3. ShortCircuit(Remediate) on single-question violation + correct flag
///    row in telemetry.
/// 4. ShortCircuit(Remediate) with aggregated reason on multi-question
///    violations.
/// 5. Graceful degradation on API timeout / API exception / parse failure
///    — Continue + <c>P_VERIFIER_FALLBACK</c> telemetry with
///    "local judgment gates remain active" callout (additive framing).
/// 6. Substrate forwarding — <c>GroundedFacts</c> + <c>AnchoredMemories</c>
///    only. <c>ContactRecentMessages</c> is NOT used (plan-doc §9.1).
///
/// Strict mock for <see cref="IFrontierVerifierClient"/> — every test
/// declares its exact expected interaction with the verifier (or asserts
/// the verifier is never called, when AppliesTo is supposed to gate it
/// out).
/// </summary>
public class FrontierVerifierHandlerTests
{
    // ─── Fakes / helpers ─────────────────────────────────────────────────

    private sealed class CapturingLogger : ILogger<FrontierVerifierHandler>
    {
        public List<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(
            LogLevel level, EventId id, TState state,
            Exception? ex, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, ex));
    }

    private static IOptions<AniOptions> Options(bool flagOn = true) =>
        Microsoft.Extensions.Options.Options.Create(new AniOptions
        {
            FrontierVerifierEnabled = flagOn,
        });

    private static CognitiveArtifact ArtifactFor(
        string                content      = "hey, just thinking about you tonight.",
        CognitiveProducerKind producer     = CognitiveProducerKind.Outreach,
        CognitiveOutputSink   sink         = CognitiveOutputSink.Dispatch,
        IReadOnlyList<string>? markRecent  = null) => new()
    {
        Content                 = content,
        ProducerKind            = producer,
        IntendedSink            = sink,
        ContactName             = "Mark",
        // Populated to verify it is NOT consulted by the handler — plan-doc
        // §9.1 requires canonical-Mark-only substrate sources, never the
        // artifact's runtime-collected ContactRecentMessages.
        ContactRecentMessages   = markRecent ?? new[] { "shouldn't be in the substrate" },
        GeneratedAt             = new DateTimeOffset(2026, 5, 11, 19, 30, 0, TimeSpan.FromHours(-5)),
    };

    private static CognitivePipelineContext CtxFor(
        CognitiveArtifact? artifact   = null,
        List<MemoryRecord>? groundedFacts = null,
        List<MemoryRecord>? anchoredMemories = null,
        List<MemoryRecord>? recentExchanges = null,
        string?            primaryContact = "Mark",
        List<string>?      canonicalContacts = null) => new()
    {
        Artifact = artifact ?? ArtifactFor(),
        Snapshot = new ContextSnapshot
        {
            CharacterState = new CharacterStateDoc
            {
                Name               = "Ani",
                PrimaryContactName = primaryContact ?? string.Empty,
                CanonicalContacts  = canonicalContacts ?? new List<string>(),
            },
            GroundedFacts     = groundedFacts     ?? new List<MemoryRecord>(),
            AnchoredMemories  = anchoredMemories  ?? new List<MemoryRecord>(),
            RecentExchanges   = recentExchanges   ?? new List<MemoryRecord>(),
        },
    };

    private static MemoryRecord MemoryFact(string content, DateTimeOffset? createdAt = null) => new()
    {
        Content    = content,
        Provenance = EpistemicTier.Facts,
        CreatedAt  = createdAt ?? DateTimeOffset.UtcNow,
    };

    private static MemoryRecord AnchoredMemory(string content) => new()
    {
        Content    = content,
        Provenance = EpistemicTier.Facts,
        DecayTier  = DecayTier.Anchored,
        CreatedAt  = DateTimeOffset.UtcNow.AddDays(-30),
    };

    /// <summary>Per-question response: q1..q5 all clean (no violations).</summary>
    private static FrontierVerifierResult NoViolations() => new(
        AnyViolation: false,
        Questions: new List<QuestionVerdict>
        {
            new(1, "shared-event",          false, null, null),
            new(2, "present-tense-state",   false, null, null),
            new(3, "third-party-reference", false, null, null),
            new(4, "temporal-claim",        false, null, null),
            new(5, "inner-thought-bleed",   false, null, null),
        },
        SummaryVerdict: "pass",
        AggregatedReason: null);

    /// <summary>Per-question response: only question <paramref name="qN"/> violates.</summary>
    private static FrontierVerifierResult SingleViolation(int qN, string quote, string reason)
    {
        var labels = new[]
        {
            "shared-event", "present-tense-state", "third-party-reference",
            "temporal-claim", "inner-thought-bleed",
        };
        var qs = new List<QuestionVerdict>();
        for (var i = 1; i <= 5; i++)
        {
            qs.Add(i == qN
                ? new QuestionVerdict(i, labels[i - 1], true,  quote, reason)
                : new QuestionVerdict(i, labels[i - 1], false, null,  null));
        }
        return new(true, qs, "remediate", $"q{qN}[{labels[qN - 1]}]: {reason}");
    }

    /// <summary>Per-question response: q1 + q4 both violate (multi-violation case).</summary>
    private static FrontierVerifierResult MultiViolation() => new(
        AnyViolation: true,
        Questions: new List<QuestionVerdict>
        {
            new(1, "shared-event",          true,  "we walked yesterday",  "no shared-walk substrate"),
            new(2, "present-tense-state",   false, null,                   null),
            new(3, "third-party-reference", false, null,                   null),
            new(4, "temporal-claim",        true,  "happy Saturday",       "today is Monday"),
            new(5, "inner-thought-bleed",   false, null,                   null),
        },
        SummaryVerdict: "remediate",
        AggregatedReason: "q1[shared-event]: no shared-walk substrate; q4[temporal-claim]: today is Monday");

    // ─── 1. AppliesTo — flag off ─────────────────────────────────────────

    [Fact]
    public void AppliesTo_FlagOff_ReturnsFalse()
    {
        // Strict mock with no expectations — if AppliesTo passes through,
        // HandleAsync would fail (mock unconfigured). Flag off must gate
        // the cloud handler out cleanly. Critically: local judgment gates
        // are still running in parallel (plan-doc §9.1) — they have zero
        // knowledge of this flag.
        var client = new Mock<IFrontierVerifierClient>(MockBehavior.Strict);
        var handler = new FrontierVerifierHandler(
            client.Object, Options(flagOn: false), new CapturingLogger());

        handler.AppliesTo(ArtifactFor()).Should().BeFalse(
            "FrontierVerifierEnabled=false must gate the cloud handler out; local gates run independently");
    }

    // ─── 2. AppliesTo — wrong sink ───────────────────────────────────────

    [Theory]
    [InlineData(CognitiveOutputSink.PersistedMemory)]
    [InlineData(CognitiveOutputSink.PersistedSummary)]
    [InlineData(CognitiveOutputSink.ContextOnly)]
    public void AppliesTo_NonDispatchSink_ReturnsFalse(CognitiveOutputSink sink)
    {
        var client = new Mock<IFrontierVerifierClient>(MockBehavior.Strict);
        var handler = new FrontierVerifierHandler(client.Object, Options(), new CapturingLogger());

        handler.AppliesTo(ArtifactFor(sink: sink)).Should().BeFalse(
            "frontier verifier only applies to dispatch-bound artifacts (the actual SMS/voice surface)");
    }

    // ─── 3. AppliesTo — wrong producer kind ──────────────────────────────

    [Theory]
    [InlineData(CognitiveProducerKind.InnerThought)]
    [InlineData(CognitiveProducerKind.WorldExperience)]
    [InlineData(CognitiveProducerKind.Reflection)]
    [InlineData(CognitiveProducerKind.MemoryMerge)]
    [InlineData(CognitiveProducerKind.ClosedThreadSummary)]
    public void AppliesTo_WrongProducerKind_ReturnsFalse(CognitiveProducerKind producer)
    {
        var client = new Mock<IFrontierVerifierClient>(MockBehavior.Strict);
        var handler = new FrontierVerifierHandler(client.Object, Options(), new CapturingLogger());

        handler.AppliesTo(ArtifactFor(producer: producer)).Should().BeFalse(
            "frontier verifier only applies to contact-facing producers (Reply / Outreach / Voice)");
    }

    [Theory]
    [InlineData(CognitiveProducerKind.ConversationReply)]
    [InlineData(CognitiveProducerKind.Outreach)]
    [InlineData(CognitiveProducerKind.Voice)]
    public void AppliesTo_DispatchProducers_ReturnsTrue(CognitiveProducerKind producer)
    {
        var client = new Mock<IFrontierVerifierClient>(MockBehavior.Strict);
        var handler = new FrontierVerifierHandler(client.Object, Options(), new CapturingLogger());

        handler.AppliesTo(ArtifactFor(producer: producer)).Should().BeTrue();
    }

    // ─── 4. No violations → Continue + Pass telemetry ────────────────────

    [Fact]
    public async Task HandleAsync_NoViolations_ReturnsContinue_AndLogsPassVerdict()
    {
        var client = new Mock<IFrontierVerifierClient>(MockBehavior.Strict);
        client.Setup(c => c.VerifyAsync(It.IsAny<FrontierVerifierRequest>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(NoViolations());
        var log = new CapturingLogger();
        var handler = new FrontierVerifierHandler(client.Object, Options(), log);

        var result = await handler.HandleAsync(CtxFor(), CancellationToken.None);

        result.ShortCircuit.Should().BeFalse("no violations → pipeline continues");
        result.Verdict.Should().BeNull();
        log.Messages.Should().ContainSingle(m =>
            m.Contains("P_VERIFIER_VERDICT") && m.Contains("verdict=Pass"));
        log.Messages.Should().ContainSingle(m =>
            m.Contains("q1=0") && m.Contains("q2=0") && m.Contains("q3=0")
         && m.Contains("q4=0") && m.Contains("q5=0"));
        log.Messages.Should().ContainSingle(m => m.Contains("provider=Sonnet"));
    }

    // ─── 5. Single-question violation → ShortCircuit Remediate ───────────

    [Fact]
    public async Task HandleAsync_SingleViolationQ1_ShortCircuitsWithRemediate()
    {
        var verdict = SingleViolation(qN: 1, quote: "we both wanted that", reason: "no shared-want substrate");
        var client = new Mock<IFrontierVerifierClient>(MockBehavior.Strict);
        client.Setup(c => c.VerifyAsync(It.IsAny<FrontierVerifierRequest>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(verdict);
        var log = new CapturingLogger();
        var handler = new FrontierVerifierHandler(client.Object, Options(), log);

        var result = await handler.HandleAsync(CtxFor(), CancellationToken.None);

        result.ShortCircuit.Should().BeTrue();
        result.Verdict.Should().Be(DispatchVerdict.Remediate,
            "any violation → Remediate per plan-doc §3 aggregation rule");
        result.Reason.Should().Contain("q1");
        result.Reason.Should().Contain("no shared-want substrate");

        log.Messages.Should().ContainSingle(m =>
            m.Contains("P_VERIFIER_VERDICT") && m.Contains("verdict=Remediate")
         && m.Contains("q1=V") && m.Contains("q2=0") && m.Contains("q5=0"));
    }

    // ─── 6. Multi-question violation → aggregated reason ─────────────────

    [Fact]
    public async Task HandleAsync_MultiViolation_ReasonMentionsAllViolations()
    {
        var client = new Mock<IFrontierVerifierClient>(MockBehavior.Strict);
        client.Setup(c => c.VerifyAsync(It.IsAny<FrontierVerifierRequest>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(MultiViolation());
        var log = new CapturingLogger();
        var handler = new FrontierVerifierHandler(client.Object, Options(), log);

        var result = await handler.HandleAsync(CtxFor(), CancellationToken.None);

        result.ShortCircuit.Should().BeTrue();
        result.Verdict.Should().Be(DispatchVerdict.Remediate);
        result.Reason.Should().Contain("q1");
        result.Reason.Should().Contain("q4");
        result.Reason.Should().Contain("shared-event");
        result.Reason.Should().Contain("temporal-claim");

        log.Messages.Should().ContainSingle(m =>
            m.Contains("verdict=Remediate")
         && m.Contains("q1=V") && m.Contains("q2=0") && m.Contains("q3=0")
         && m.Contains("q4=V") && m.Contains("q5=0"));
    }

    // ─── 7. API timeout → fallback (Continue) ────────────────────────────

    [Fact]
    public async Task HandleAsync_ClientTimesOut_ReturnsContinue_AndLogsFallback()
    {
        var client = new Mock<IFrontierVerifierClient>(MockBehavior.Strict);
        client.Setup(c => c.VerifyAsync(It.IsAny<FrontierVerifierRequest>(), It.IsAny<CancellationToken>()))
              .ThrowsAsync(new TimeoutException("Anthropic /v1/messages timed out after 30000ms"));
        var log = new CapturingLogger();
        var handler = new FrontierVerifierHandler(client.Object, Options(), log);

        var result = await handler.HandleAsync(CtxFor(), CancellationToken.None);

        result.ShortCircuit.Should().BeFalse(
            "graceful degradation: cloud timeout reduces defense-in-depth by one layer; local gates remain active");
        log.Messages.Should().ContainSingle(m =>
            m.Contains("P_VERIFIER_FALLBACK")
         && m.Contains("timed out")
         && m.Contains("local judgment gates remain active"));
    }

    // ─── 8. API exception → fallback (Continue) ──────────────────────────

    [Fact]
    public async Task HandleAsync_ClientThrowsHttpError_ReturnsContinue_AndLogsFallback()
    {
        var client = new Mock<IFrontierVerifierClient>(MockBehavior.Strict);
        client.Setup(c => c.VerifyAsync(It.IsAny<FrontierVerifierRequest>(), It.IsAny<CancellationToken>()))
              .ThrowsAsync(new HttpRequestException("Anthropic returned 503"));
        var log = new CapturingLogger();
        var handler = new FrontierVerifierHandler(client.Object, Options(), log);

        var result = await handler.HandleAsync(CtxFor(), CancellationToken.None);

        result.ShortCircuit.Should().BeFalse();
        log.Messages.Should().ContainSingle(m =>
            m.Contains("P_VERIFIER_FALLBACK")
         && m.Contains("503")
         && m.Contains("local judgment gates remain active"));
    }

    // ─── 9. Parse failure → fallback (Continue) ──────────────────────────

    [Fact]
    public async Task HandleAsync_ClientThrowsParseError_ReturnsContinue_AndLogsFallback()
    {
        var client = new Mock<IFrontierVerifierClient>(MockBehavior.Strict);
        client.Setup(c => c.VerifyAsync(It.IsAny<FrontierVerifierRequest>(), It.IsAny<CancellationToken>()))
              .ThrowsAsync(new InvalidOperationException(
                  "Verifier response missing field 'q3'."));
        var log = new CapturingLogger();
        var handler = new FrontierVerifierHandler(client.Object, Options(), log);

        var result = await handler.HandleAsync(CtxFor(), CancellationToken.None);

        result.ShortCircuit.Should().BeFalse(
            "parse failure is one of the plan-doc §4 lock 4 graceful-degradation triggers");
        log.Messages.Should().ContainSingle(m =>
            m.Contains("P_VERIFIER_FALLBACK")
         && m.Contains("missing field")
         && m.Contains("local judgment gates remain active"));
    }

    // ─── 10. Empty content → no verifier call, Continue ──────────────────

    [Fact]
    public async Task HandleAsync_EmptyContent_SkipsVerifierAndReturnsContinue()
    {
        // Strict mock — no setup. If HandleAsync calls VerifyAsync the
        // test fails with MockException.
        var client = new Mock<IFrontierVerifierClient>(MockBehavior.Strict);
        var handler = new FrontierVerifierHandler(client.Object, Options(), new CapturingLogger());

        var result = await handler.HandleAsync(CtxFor(ArtifactFor(content: "")), CancellationToken.None);

        result.ShortCircuit.Should().BeFalse();
        client.Verify(c => c.VerifyAsync(It.IsAny<FrontierVerifierRequest>(), It.IsAny<CancellationToken>()),
            Times.Never, "empty content has nothing to verify; verifier must not be called");
    }

    // ─── 11. Cancellation propagates ─────────────────────────────────────

    [Fact]
    public async Task HandleAsync_Cancellation_PropagatesOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var client = new Mock<IFrontierVerifierClient>(MockBehavior.Strict);
        client.Setup(c => c.VerifyAsync(It.IsAny<FrontierVerifierRequest>(), It.IsAny<CancellationToken>()))
              .ThrowsAsync(new OperationCanceledException(cts.Token));
        var handler = new FrontierVerifierHandler(client.Object, Options(), new CapturingLogger());

        var act = async () => await handler.HandleAsync(CtxFor(), cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>(
            "the pipeline orchestrator handles cancellation; the handler must not swallow it as a fallback");
    }

    // ─── 12. Substrate — GroundedFacts + AnchoredMemories surface ────────

    [Fact]
    public async Task HandleAsync_BuildsRequestFromCanonicalSnapshotPools()
    {
        FrontierVerifierRequest? captured = null;
        var client = new Mock<IFrontierVerifierClient>(MockBehavior.Strict);
        client.Setup(c => c.VerifyAsync(It.IsAny<FrontierVerifierRequest>(), It.IsAny<CancellationToken>()))
              .Callback<FrontierVerifierRequest, CancellationToken>((req, _) => captured = req)
              .ReturnsAsync(NoViolations());
        var handler = new FrontierVerifierHandler(client.Object, Options(), new CapturingLogger());

        var groundedFacts = new List<MemoryRecord>
        {
            // Order matters: handler should recency-sort and take top 10.
            MemoryFact("Mark mentioned a tough day yesterday", DateTimeOffset.UtcNow.AddHours(-2)),
            MemoryFact("Mark teaches Spanish on Tuesdays",     DateTimeOffset.UtcNow.AddDays(-3)),
        };
        var anchoredMemories = new List<MemoryRecord>
        {
            AnchoredMemory("Mark lives in Wisconsin"),
            AnchoredMemory("Ani is a bookstore clerk in a small Wisconsin town"),
        };

        var ctx = CtxFor(
            artifact:          ArtifactFor(content: "morning! just thinking about you."),
            groundedFacts:     groundedFacts,
            anchoredMemories:  anchoredMemories,
            canonicalContacts: new List<string> { "Sarah", "Kevin" });
        await handler.HandleAsync(ctx, CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.ComposedMessage.Should().Be("morning! just thinking about you.");
        captured.AddresseeCanonicalName.Should().Be("Mark");

        // Mark-asserted block reflects GroundedFacts (canonical Mark-only),
        // not ContactRecentMessages.
        captured.MarkAssertedSubstrate.Should().Contain("tough day yesterday");
        captured.MarkAssertedSubstrate.Should().Contain("Spanish on Tuesdays");

        // Canonical block reflects AnchoredMemories.
        captured.CanonicalSubstrate.Should().Contain("Wisconsin");
        captured.CanonicalSubstrate.Should().Contain("bookstore clerk");

        // Known contacts reflect CharacterStateDoc.CanonicalContacts.
        captured.KnownContacts.Should().Contain("Sarah");
        captured.KnownContacts.Should().Contain("Kevin");

        // Day-of-week derived from the artifact's local time (2026-05-11 Monday CDT).
        captured.CurrentDayOfWeek.Should().Be("Monday",
            "handler derives day-of-week from artifact's local-time projection");
    }

    // ─── 13. Substrate — ContactRecentMessages is NOT consulted ──────────

    [Fact]
    public async Task HandleAsync_DoesNotReadContactRecentMessagesFromArtifact()
    {
        FrontierVerifierRequest? captured = null;
        var client = new Mock<IFrontierVerifierClient>(MockBehavior.Strict);
        client.Setup(c => c.VerifyAsync(It.IsAny<FrontierVerifierRequest>(), It.IsAny<CancellationToken>()))
              .Callback<FrontierVerifierRequest, CancellationToken>((req, _) => captured = req)
              .ReturnsAsync(NoViolations());
        var handler = new FrontierVerifierHandler(client.Object, Options(), new CapturingLogger());

        // Stuff the artifact's ContactRecentMessages with content that, if
        // the handler were reading it, would leak into MarkAssertedSubstrate.
        // Plan-doc §9.1 forbids this — the handler reads only canonical
        // snapshot pools.
        var artifact = ArtifactFor(
            content:    "hey, how's the morning going?",
            markRecent: new[] { "SHOULD_NOT_APPEAR_IN_SUBSTRATE_alpha", "SHOULD_NOT_APPEAR_IN_SUBSTRATE_beta" });
        var ctx = CtxFor(artifact: artifact);

        await handler.HandleAsync(ctx, CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.MarkAssertedSubstrate.Should().NotContain("SHOULD_NOT_APPEAR_IN_SUBSTRATE",
            "the handler must not consult artifact.ContactRecentMessages — canonical snapshot pools only (plan-doc §9.1)");
    }

    // ─── 14. Substrate — empty pools render as empty strings ─────────────

    [Fact]
    public async Task HandleAsync_EmptyCanonicalPools_RenderAsEmptyStrings()
    {
        FrontierVerifierRequest? captured = null;
        var client = new Mock<IFrontierVerifierClient>(MockBehavior.Strict);
        client.Setup(c => c.VerifyAsync(It.IsAny<FrontierVerifierRequest>(), It.IsAny<CancellationToken>()))
              .Callback<FrontierVerifierRequest, CancellationToken>((req, _) => captured = req)
              .ReturnsAsync(NoViolations());
        var handler = new FrontierVerifierHandler(client.Object, Options(), new CapturingLogger());

        // Snapshot with no canonical substrate at all. Plan-doc §9.1: empty
        // is the correct rendering — no fallback to other sources.
        var ctx = CtxFor(
            groundedFacts:     new List<MemoryRecord>(),
            anchoredMemories:  new List<MemoryRecord>(),
            recentExchanges:   new List<MemoryRecord>
            {
                // Even if RecentExchanges contains content, the handler
                // must NOT fall back to it.
                MemoryFact("FALLBACK_LEAK_should_not_appear"),
            },
            canonicalContacts: new List<string>());
        await handler.HandleAsync(ctx, CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.MarkAssertedSubstrate.Should().BeEmpty(
            "no Facts-tier records → empty substrate; no fallback to RecentExchanges");
        captured.CanonicalSubstrate.Should().BeEmpty(
            "no Anchored records → empty canonical substrate");
        captured.KnownContacts.Should().BeEmpty(
            "no CanonicalContacts seeded → empty known-contacts; no derivation from runtime");
        captured.MarkAssertedSubstrate.Should().NotContain("FALLBACK_LEAK",
            "RecentExchanges must not leak into the verifier prompt (plan-doc §9.1)");
    }
}
