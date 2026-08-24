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
/// 6. Substrate retrieval (P.3.2, May 12 2026) — handler uses
///    <see cref="IMemorySearch.SearchByTierAsync"/> keyed to the composed
///    reply text. Snapshot's <c>GroundedFacts</c> / <c>AnchoredMemories</c>
///    pools are NOT read. <c>ContactRecentMessages</c> is NOT read.
///    Anchored records (<see cref="DecayTier.Anchored"/>) split into the
///    request's <c>CanonicalSubstrate</c> field; non-anchored Facts split
///    into <c>MarkAssertedSubstrate</c>.
///
/// Strict mock for <see cref="IFrontierVerifierClient"/> + strict mock
/// for <see cref="IMemorySearch"/> — every test declares its exact
/// expected interaction (or asserts the call is never made).
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

    /// <summary>
    /// Build a strict <see cref="IMemorySearch"/> mock for the P.3.2
    /// retrieval surface. <paramref name="hits"/> is the ranked list the
    /// handler will receive (each as a <see cref="ScoredMemory"/>). The
    /// mock asserts <see cref="EpistemicTier.Facts"/> is the requested
    /// tier — handler must NOT call any other tier or any non-tier
    /// search method (strict mode catches that).
    /// </summary>
    private static Mock<IMemorySearch> SearchMock(IEnumerable<ScoredMemory> hits)
    {
        var mock = new Mock<IMemorySearch>(MockBehavior.Strict);
        mock.Setup(s => s.SearchByTierAsync(
                It.IsAny<string>(),
                EpistemicTier.Facts,
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<float>()))
            .ReturnsAsync(hits.ToList());
        return mock;
    }

    /// <summary>Default: empty retrieval pool.</summary>
    private static Mock<IMemorySearch> EmptySearchMock() =>
        SearchMock(Array.Empty<ScoredMemory>());

    /// <summary>
    /// Strict mock with no <c>SearchByTierAsync</c> setup — if HandleAsync
    /// reaches retrieval the call fails the test with <c>MockException</c>.
    /// Use for tests that gate out before retrieval (flag-off, wrong
    /// sink/producer at AppliesTo, empty content, cancellation pre-call).
    /// </summary>
    private static Mock<IMemorySearch> NeverCalledSearchMock() =>
        new(MockBehavior.Strict);

    private static ScoredMemory Hit(MemoryRecord record, float composite = 0.7f, float cosine = 0.7f) =>
        new(record, composite, cosine);

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
        var search = NeverCalledSearchMock();
        var handler = new FrontierVerifierHandler(
            client.Object, search.Object, Options(flagOn: false), new CapturingLogger());

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
        var search = NeverCalledSearchMock();
        var handler = new FrontierVerifierHandler(client.Object, search.Object, Options(), new CapturingLogger());

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
        var search = NeverCalledSearchMock();
        var handler = new FrontierVerifierHandler(client.Object, search.Object, Options(), new CapturingLogger());

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
        var search = NeverCalledSearchMock();
        var handler = new FrontierVerifierHandler(client.Object, search.Object, Options(), new CapturingLogger());

        handler.AppliesTo(ArtifactFor(producer: producer)).Should().BeTrue();
    }

    // ─── 4. No violations → Continue + Pass telemetry ────────────────────

    [Fact]
    public async Task HandleAsync_NoViolations_ReturnsContinue_AndLogsPassVerdict()
    {
        var client = new Mock<IFrontierVerifierClient>(MockBehavior.Strict);
        client.Setup(c => c.VerifyAsync(It.IsAny<FrontierVerifierRequest>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(NoViolations());
        var search = EmptySearchMock();
        var log = new CapturingLogger();
        var handler = new FrontierVerifierHandler(client.Object, search.Object, Options(), log);

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
        var search = EmptySearchMock();
        var log = new CapturingLogger();
        var handler = new FrontierVerifierHandler(client.Object, search.Object, Options(), log);

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
        var search = EmptySearchMock();
        var log = new CapturingLogger();
        var handler = new FrontierVerifierHandler(client.Object, search.Object, Options(), log);

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
        var search = EmptySearchMock();
        var log = new CapturingLogger();
        var handler = new FrontierVerifierHandler(client.Object, search.Object, Options(), log);

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
        var search = EmptySearchMock();
        var log = new CapturingLogger();
        var handler = new FrontierVerifierHandler(client.Object, search.Object, Options(), log);

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
        var search = EmptySearchMock();
        var log = new CapturingLogger();
        var handler = new FrontierVerifierHandler(client.Object, search.Object, Options(), log);

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
        // Strict mocks — no setup on either. If HandleAsync reaches
        // retrieval or the verifier, the test fails with MockException.
        var client = new Mock<IFrontierVerifierClient>(MockBehavior.Strict);
        var search = NeverCalledSearchMock();
        var handler = new FrontierVerifierHandler(client.Object, search.Object, Options(), new CapturingLogger());

        var result = await handler.HandleAsync(CtxFor(ArtifactFor(content: "")), CancellationToken.None);

        result.ShortCircuit.Should().BeFalse();
        client.Verify(c => c.VerifyAsync(It.IsAny<FrontierVerifierRequest>(), It.IsAny<CancellationToken>()),
            Times.Never, "empty content has nothing to verify; verifier must not be called");
        search.Verify(s => s.SearchByTierAsync(
                It.IsAny<string>(), It.IsAny<EpistemicTier>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>(),
                It.IsAny<float>()),
            Times.Never, "empty content short-circuits before retrieval too");
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
        // Retrieval succeeds (empty) so the OCE comes from the verifier
        // call — that's the path the test pins (orchestrator handles OCE
        // tied to the caller's ct).
        var search = EmptySearchMock();
        var handler = new FrontierVerifierHandler(client.Object, search.Object, Options(), new CapturingLogger());

        var act = async () => await handler.HandleAsync(CtxFor(), cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>(
            "the pipeline orchestrator handles cancellation; the handler must not swallow it as a fallback");
    }

    // ─── 12. Substrate — post-hoc retrieval keyed to composed reply ──────

    [Fact]
    public async Task HandleAsync_BuildsRequestViaPostHocRetrievalKeyedToComposedReply()
    {
        FrontierVerifierRequest? captured = null;
        var client = new Mock<IFrontierVerifierClient>(MockBehavior.Strict);
        client.Setup(c => c.VerifyAsync(It.IsAny<FrontierVerifierRequest>(), It.IsAny<CancellationToken>()))
              .Callback<FrontierVerifierRequest, CancellationToken>((req, _) => captured = req)
              .ReturnsAsync(NoViolations());

        // Mixed hit list: two regular Facts records + two anchored records.
        // P.3.2 simplification — anchored character seeds carry
        // provenance=Facts (so they surface in the single Facts-tier search)
        // AND tier=Anchored (so the handler splits them into the
        // CanonicalSubstrate field). Order in this list is the ranked
        // composite-score order the handler will receive.
        var hits = new List<ScoredMemory>
        {
            Hit(MemoryFact("Mark mentioned a tough day yesterday")),
            Hit(AnchoredMemory("Ani is a bookstore clerk in a small Wisconsin town")),
            Hit(MemoryFact("Mark teaches Spanish on Tuesdays")),
            Hit(AnchoredMemory("Mark lives in Wisconsin")),
        };

        string? capturedQuery = null;
        int     capturedTopK  = -1;
        float   capturedMinCosine = -1f;
        var search = new Mock<IMemorySearch>(MockBehavior.Strict);
        search.Setup(s => s.SearchByTierAsync(
                It.IsAny<string>(), EpistemicTier.Facts,
                It.IsAny<int>(), It.IsAny<CancellationToken>(),
                It.IsAny<float>()))
              .Callback<string, EpistemicTier, int, CancellationToken, float>(
                  (q, _, k, _, m) => { capturedQuery = q; capturedTopK = k; capturedMinCosine = m; })
              .ReturnsAsync(hits);

        var handler = new FrontierVerifierHandler(
            client.Object, search.Object, Options(), new CapturingLogger());

        var ctx = CtxFor(
            artifact: ArtifactFor(content: "morning! just thinking about you."),
            // Snapshot pools populated to verify the handler does NOT read
            // them — P.3.2 routes through IMemorySearch, not the snapshot
            // pool slices.
            groundedFacts: new List<MemoryRecord>
            {
                MemoryFact("SNAPSHOT_FACT_should_not_appear"),
            },
            anchoredMemories: new List<MemoryRecord>
            {
                AnchoredMemory("SNAPSHOT_ANCHOR_should_not_appear"),
            },
            canonicalContacts: new List<string> { "Sarah", "Kevin" });
        await handler.HandleAsync(ctx, CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.ComposedMessage.Should().Be("morning! just thinking about you.");
        captured.AddresseeCanonicalName.Should().Be("Mark");

        // Retrieval was keyed to the composed reply text — not perceptions,
        // not the cycle's snapshot query. This is the P.3.2 invariant.
        capturedQuery.Should().Be("morning! just thinking about you.",
            "the verifier substrate is retrieved against the composed reply (P.3.2), not snapshot-keyed");
        capturedTopK.Should().BeGreaterOrEqualTo(10,
            "P.3.2 starts at top-N=20; >=10 is the floor sanity check");
        capturedMinCosine.Should().BeApproximately(0.75f, 0.001f,
            "P.4 (May 12, 2026): verifier retrieval must pass MinCosineThresholdVerifier (0.75) so only meaningful substrate reaches q1–q5 evaluation");

        // MarkAssertedSubstrate gets the non-anchored Facts records.
        captured.MarkAssertedSubstrate.Should().Contain("tough day yesterday");
        captured.MarkAssertedSubstrate.Should().Contain("Spanish on Tuesdays");
        captured.MarkAssertedSubstrate.Should().NotContain("bookstore clerk",
            "anchored records split into CanonicalSubstrate, not MarkAssertedSubstrate");
        captured.MarkAssertedSubstrate.Should().NotContain("Mark lives in Wisconsin",
            "anchored records split into CanonicalSubstrate, not MarkAssertedSubstrate");

        // CanonicalSubstrate gets the anchored (DecayTier.Anchored) records.
        captured.CanonicalSubstrate.Should().Contain("Wisconsin");
        captured.CanonicalSubstrate.Should().Contain("bookstore clerk");

        // Snapshot-pool reads are forbidden — verify those records never
        // reached the verifier (P.3.2 architectural correction).
        captured.MarkAssertedSubstrate.Should().NotContain("SNAPSHOT_FACT_should_not_appear",
            "handler must NOT read Snapshot.GroundedFacts — retrieval is post-hoc semantic only");
        captured.CanonicalSubstrate.Should().NotContain("SNAPSHOT_ANCHOR_should_not_appear",
            "handler must NOT read Snapshot.AnchoredMemories — retrieval is post-hoc semantic only");

        // Known contacts still flow from CharacterStateDoc.CanonicalContacts
        // (identity scaffolding, not claim-grounding substrate — unchanged).
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
        // Retrieval returns empty so we can detect any leak unambiguously.
        var search = EmptySearchMock();
        var handler = new FrontierVerifierHandler(
            client.Object, search.Object, Options(), new CapturingLogger());

        // Stuff the artifact's ContactRecentMessages with content that, if
        // the handler were reading it, would leak into MarkAssertedSubstrate.
        // Plan-doc §9.1 forbids this — the handler reads only canonical
        // retrieval substrate.
        var artifact = ArtifactFor(
            content:    "hey, how's the morning going?",
            markRecent: new[] { "SHOULD_NOT_APPEAR_IN_SUBSTRATE_alpha", "SHOULD_NOT_APPEAR_IN_SUBSTRATE_beta" });
        var ctx = CtxFor(artifact: artifact);

        await handler.HandleAsync(ctx, CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.MarkAssertedSubstrate.Should().NotContain("SHOULD_NOT_APPEAR_IN_SUBSTRATE",
            "the handler must not consult artifact.ContactRecentMessages — canonical retrieval only (plan-doc §9.1)");
        captured.CanonicalSubstrate.Should().NotContain("SHOULD_NOT_APPEAR_IN_SUBSTRATE",
            "ContactRecentMessages must not leak through CanonicalSubstrate either");
    }

    // ─── 14. Substrate — empty retrieval renders as empty strings ────────

    [Fact]
    public async Task HandleAsync_EmptyRetrieval_RendersSubstrateAsEmptyStrings()
    {
        FrontierVerifierRequest? captured = null;
        var client = new Mock<IFrontierVerifierClient>(MockBehavior.Strict);
        client.Setup(c => c.VerifyAsync(It.IsAny<FrontierVerifierRequest>(), It.IsAny<CancellationToken>()))
              .Callback<FrontierVerifierRequest, CancellationToken>((req, _) => captured = req)
              .ReturnsAsync(NoViolations());
        // No retrieval hits — and the snapshot pools (which the handler
        // must NOT read) are stuffed with content to catch any regression.
        var search = EmptySearchMock();
        var handler = new FrontierVerifierHandler(
            client.Object, search.Object, Options(), new CapturingLogger());

        var ctx = CtxFor(
            groundedFacts: new List<MemoryRecord>
            {
                MemoryFact("LEAK_FACT_should_not_appear"),
            },
            anchoredMemories: new List<MemoryRecord>
            {
                AnchoredMemory("LEAK_ANCHOR_should_not_appear"),
            },
            recentExchanges: new List<MemoryRecord>
            {
                MemoryFact("LEAK_RECENT_should_not_appear"),
            },
            canonicalContacts: new List<string>());
        await handler.HandleAsync(ctx, CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.MarkAssertedSubstrate.Should().BeEmpty(
            "no retrieval hits → empty Mark-asserted substrate; no fallback to snapshot pools");
        captured.CanonicalSubstrate.Should().BeEmpty(
            "no anchored retrieval hits → empty canonical substrate; no fallback to Snapshot.AnchoredMemories");
        captured.KnownContacts.Should().BeEmpty(
            "no CanonicalContacts seeded → empty known-contacts; no derivation from runtime");
        captured.MarkAssertedSubstrate.Should().NotContain("LEAK_",
            "snapshot pools / RecentExchanges must not leak into the verifier prompt");
        captured.CanonicalSubstrate.Should().NotContain("LEAK_",
            "snapshot pools must not leak into the verifier prompt");
    }

    // ─── 15. Substrate — retrieval failure degrades to empty, not throw ──

    [Fact]
    public async Task HandleAsync_RetrievalFailure_RendersEmptySubstrate_AndVerifierStillRuns()
    {
        FrontierVerifierRequest? captured = null;
        var client = new Mock<IFrontierVerifierClient>(MockBehavior.Strict);
        client.Setup(c => c.VerifyAsync(It.IsAny<FrontierVerifierRequest>(), It.IsAny<CancellationToken>()))
              .Callback<FrontierVerifierRequest, CancellationToken>((req, _) => captured = req)
              .ReturnsAsync(NoViolations());

        // Retrieval throws — handler must continue with empty substrate
        // rather than propagate. The verifier call still happens (the
        // graceful-degradation contract isolates retrieval failure from
        // verifier failure).
        var search = new Mock<IMemorySearch>(MockBehavior.Strict);
        search.Setup(s => s.SearchByTierAsync(
                It.IsAny<string>(), EpistemicTier.Facts,
                It.IsAny<int>(), It.IsAny<CancellationToken>(),
                It.IsAny<float>()))
              .ThrowsAsync(new InvalidOperationException("simulated retrieval failure"));

        var handler = new FrontierVerifierHandler(
            client.Object, search.Object, Options(), new CapturingLogger());

        var result = await handler.HandleAsync(CtxFor(), CancellationToken.None);

        result.ShortCircuit.Should().BeFalse(
            "retrieval failure must not short-circuit; the verifier still gets the composed reply");
        captured.Should().NotBeNull(
            "verifier is invoked even when retrieval fails — substrate just renders empty");
        captured!.MarkAssertedSubstrate.Should().BeEmpty();
        captured.CanonicalSubstrate.Should().BeEmpty();
    }

    // ─── 16. Theme P P.4 — minCosine=0.75 passed to retrieval ────────────

    [Fact]
    public async Task HandleAsync_RetrievalCall_PassesVerifierCosineThreshold()
    {
        // P.4 (May 12, 2026) pin: every retrieval call from the verifier
        // handler must pass the verifier-tier cosine floor (default 0.75)
        // so only meaningful substrate reaches q1–q5 evaluation. The floor
        // is sourced from AniOptions.MinCosineThresholdVerifier, not a
        // hard-coded literal — toggling the option must change the call.
        float capturedMinCosine = -1f;
        var search = new Mock<IMemorySearch>(MockBehavior.Strict);
        search.Setup(s => s.SearchByTierAsync(
                It.IsAny<string>(), EpistemicTier.Facts,
                It.IsAny<int>(), It.IsAny<CancellationToken>(),
                It.IsAny<float>()))
              .Callback<string, EpistemicTier, int, CancellationToken, float>(
                  (_, _, _, _, m) => capturedMinCosine = m)
              .ReturnsAsync(Array.Empty<ScoredMemory>());

        var client = new Mock<IFrontierVerifierClient>(MockBehavior.Strict);
        client.Setup(c => c.VerifyAsync(It.IsAny<FrontierVerifierRequest>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(NoViolations());

        var handler = new FrontierVerifierHandler(
            client.Object, search.Object, Options(), new CapturingLogger());

        await handler.HandleAsync(CtxFor(), CancellationToken.None);

        capturedMinCosine.Should().BeApproximately(0.75f, 0.001f,
            "verifier retrieval is floored at MinCosineThresholdVerifier (0.75) so q1–q5 evaluates only meaningful substrate");
    }

    // ─── 17. Theme P P.4 — threshold-filtered-empty produces empty substrate fields ──

    [Fact]
    public async Task HandleAsync_RetrievalFilteredEmpty_ProducesEmptySubstrateFields()
    {
        // P.4 pin: when the threshold floor filters every candidate (i.e.,
        // SearchByTierAsync returns an empty enumerable because nothing
        // crossed the cosine floor), BuildRequestAsync must produce a
        // FrontierVerifierRequest whose MarkAssertedSubstrate AND
        // CanonicalSubstrate are both empty strings. This is the shape
        // the AnthropicVerifierClient empty-substrate clause is calibrated
        // to interpret as "insufficient evidence to evaluate" rather than
        // "no support = fabricated."
        FrontierVerifierRequest? captured = null;
        var client = new Mock<IFrontierVerifierClient>(MockBehavior.Strict);
        client.Setup(c => c.VerifyAsync(It.IsAny<FrontierVerifierRequest>(), It.IsAny<CancellationToken>()))
              .Callback<FrontierVerifierRequest, CancellationToken>((req, _) => captured = req)
              .ReturnsAsync(NoViolations());

        // Threshold filtered everything → empty enumerable. Same return
        // shape as "no candidates exist at all" — the handler cannot
        // distinguish, and per the contract it should not try.
        var search = EmptySearchMock();

        var handler = new FrontierVerifierHandler(
            client.Object, search.Object, Options(), new CapturingLogger());

        await handler.HandleAsync(CtxFor(), CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.MarkAssertedSubstrate.Should().BeEmpty(
            "threshold-filtered-empty Facts pool → empty MarkAssertedSubstrate; the verifier prompt reads this as insufficient evidence, not as fabrication confirmation");
        captured.CanonicalSubstrate.Should().BeEmpty(
            "threshold-filtered-empty pool → empty CanonicalSubstrate (anchored records share the same Facts-tier retrieval surface)");
    }

    // ─── 17. F-2 Phase 2 W1 — verifier substrate carries P4 attribution tags ─

    /// <summary>
    /// F-2 Phase 2 W1 (2026-08-23) — pre-W1, <c>RenderRecords</c> emitted raw
    /// <c>- {content}</c> bullets. Sonnet's three-axis-rule prompt discipline
    /// couldn't distinguish Mark-asserted from Ani-prior lines because the
    /// substrate carried no author signal.
    ///
    /// W1 threads the F-2 Phase 1 P4 attribution-tag shape through
    /// <see cref="FrontierVerifierHandler"/>'s substrate rendering via
    /// <see cref="AniRuntime.LLM.PromptBuilder.FormatMemoryWithTime"/>.
    /// This test pins the tag shape on both substrate fields
    /// (<c>MarkAssertedSubstrate</c> for regular Facts + <c>CanonicalSubstrate</c>
    /// for anchored) so the verifier's existing discipline actually has
    /// author signal to weight.
    /// </summary>
    [Fact]
    public async Task HandleAsync_SubstrateCarriesP4AttributionTags()
    {
        FrontierVerifierRequest? captured = null;
        var client = new Mock<IFrontierVerifierClient>(MockBehavior.Strict);
        client.Setup(c => c.VerifyAsync(It.IsAny<FrontierVerifierRequest>(), It.IsAny<CancellationToken>()))
              .Callback<FrontierVerifierRequest, CancellationToken>((req, _) => captured = req)
              .ReturnsAsync(NoViolations());

        var markInboundAttr = AttributionTriple.MarkAt(
            DateTimeOffset.UtcNow.AddMinutes(-30), "twilio-inbound:SM_test");
        var markAssertedRecord = new MemoryRecord
        {
            Content                    = "W1-FIXTURE: tough day yesterday",
            Provenance                 = EpistemicTier.Facts,
            SourceName                 = "twilio-inbound",
            OccurredAt                 = DateTimeOffset.UtcNow.AddMinutes(-30),
            AttributedTo               = markInboundAttr.AttributedTo,
            AttributedAt               = markInboundAttr.AttributedAt,
            AttributedSourceRecordId   = markInboundAttr.SourceRecordId,
            AttributedSourceDescriptor = markInboundAttr.SourceDescriptor,
            AttributionTrust           = markInboundAttr.Trust,
        };

        var canonicalAttr = AttributionTriple.MarkCanonical("character-seed:mark.profile");
        var anchoredRecord = new MemoryRecord
        {
            Content                    = "W1-FIXTURE: Mark lives in Wisconsin",
            Provenance                 = EpistemicTier.Facts,
            SourceName                 = "character-seed",
            DecayTier                  = DecayTier.Anchored,
            OccurredAt                 = DateTimeOffset.UtcNow.AddDays(-30),
            AttributedTo               = canonicalAttr.AttributedTo,
            AttributedAt               = canonicalAttr.AttributedAt,
            AttributedSourceRecordId   = canonicalAttr.SourceRecordId,
            AttributedSourceDescriptor = canonicalAttr.SourceDescriptor,
            AttributionTrust           = canonicalAttr.Trust,
        };

        var search = SearchMock(new[]
        {
            Hit(markAssertedRecord),
            Hit(anchoredRecord),
        });

        var handler = new FrontierVerifierHandler(
            client.Object, search.Object, Options(), new CapturingLogger());

        await handler.HandleAsync(CtxFor(), CancellationToken.None);

        captured.Should().NotBeNull();

        // Non-anchored Mark-asserted record must render with the F-2 P4
        // attribution-tag shape. Verified trust → TRUST segment omitted.
        captured!.MarkAssertedSubstrate.Should().Contain("AUTHORED: Mark",
            "verifier substrate must expose per-line author signal — the whole point of W1");
        captured.MarkAssertedSubstrate.Should().Contain("W1-FIXTURE: tough day yesterday",
            "content must still be present (P4 tag prepended, not replaced)");
        captured.MarkAssertedSubstrate.Should().Contain("[FROM:",
            "P4 boundary tag must open the line so Sonnet parses the frame");

        // Anchored (canonical) record likewise carries the tag on the
        // CanonicalSubstrate side.
        captured.CanonicalSubstrate.Should().Contain("AUTHORED: Mark");
        captured.CanonicalSubstrate.Should().Contain("W1-FIXTURE: Mark lives in Wisconsin");
        captured.CanonicalSubstrate.Should().Contain("[FROM:");
    }

    /// <summary>
    /// F-2 Phase 2 W1 CONTROL — an Ani-authored record retrieved into the
    /// verifier substrate must render with <c>AUTHORED: Ani</c>, not Mark.
    /// This is the specific defense the pre-W1 verifier could not deliver
    /// against: a "you said X" claim in the composed reply that actually
    /// traces back to an Ani-prior record would previously have been
    /// invisible to the three-axis-rule check.
    /// </summary>
    [Fact]
    public async Task HandleAsync_SubstrateAniAuthoredRecord_RendersAuthoredAni()
    {
        FrontierVerifierRequest? captured = null;
        var client = new Mock<IFrontierVerifierClient>(MockBehavior.Strict);
        client.Setup(c => c.VerifyAsync(It.IsAny<FrontierVerifierRequest>(), It.IsAny<CancellationToken>()))
              .Callback<FrontierVerifierRequest, CancellationToken>((req, _) => captured = req)
              .ReturnsAsync(NoViolations());

        // Ani-authored Facts-tier record (e.g. reflection synthesized about
        // Mark's world that landed in the Facts pool — rare but possible).
        var aniAttr = AttributionTriple.AniAt(DateTimeOffset.UtcNow.AddMinutes(-10));
        var aniAuthored = new MemoryRecord
        {
            Content                    = "W1-FIXTURE: Ani-composed observation",
            Provenance                 = EpistemicTier.Facts,
            SourceName                 = "reflection",
            OccurredAt                 = DateTimeOffset.UtcNow.AddMinutes(-10),
            AttributedTo               = aniAttr.AttributedTo,
            AttributedAt               = aniAttr.AttributedAt,
            AttributedSourceRecordId   = aniAttr.SourceRecordId,
            AttributedSourceDescriptor = aniAttr.SourceDescriptor,
            AttributionTrust           = aniAttr.Trust,
        };

        var search = SearchMock(new[] { Hit(aniAuthored) });
        var handler = new FrontierVerifierHandler(
            client.Object, search.Object, Options(), new CapturingLogger());

        await handler.HandleAsync(CtxFor(), CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.MarkAssertedSubstrate.Should().Contain("AUTHORED: Ani",
            "the pre-W1 verifier could not see this signal; the three-axis-rule needs it to weight the line correctly");
        captured.MarkAssertedSubstrate.Should().NotContain("AUTHORED: Mark",
            "misattribution here would be the exact 12:04-shape failure at the verifier surface");
    }
}
