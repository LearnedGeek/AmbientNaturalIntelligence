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
/// R1 Phase 1 (May 9, 2026) spec tests for the typed event-shape verification
/// path on <see cref="ClaimVerificationPhase"/>. Pins the contract for the
/// new <c>shared-event-with-attribution</c> claim type:
///
///   * Cosine similarity gathers candidate Mark-asserted records (top-k above
///     threshold).
///   * Each candidate is evaluated by a per-claim LLM event-shape call asking
///     "does record A describe the same event as claim B?" with a JSON-locked
///     <c>{"same_event": true|false}</c> contract.
///   * Claim is supported iff at least one candidate's verification returns
///     yes.
///   * Zero cosine candidates → no LLM calls (fast path), claim unsupported.
///   * Malformed LLM response → fail-closed (treat as "no") — better to
///     suppress than dispatch under uncertainty.
///   * Other claim types continue to use cosine-only verification (regression
///     check that R1 is scoped to the new type only).
///
/// Strict mocks per Theme K K.0 policy. The motivating empirical case
/// (May 9, 12:54 CDT — *"Mia told us she picked out the tickets"* dispatched
/// despite the only Mark-asserted Mia record being about taking Mia to school)
/// is encoded as <see cref="VerifyAsync_MiaTicketsConfab_LLMSaysDifferentEvent_Suppresses"/>.
/// </summary>
public class ClaimVerificationPhaseR1Tests
{
    private readonly Mock<IMemorySearch> _mockSearch = new(MockBehavior.Strict);
    private readonly Mock<IOllamaClient> _mockOllama = new(MockBehavior.Strict);

    private static IOptions<AniOptions> Options() => Microsoft.Extensions.Options.Options.Create(
        new AniOptions
        {
            ClaimVerificationEnabled     = true,
            ClaimVerificationThreshold   = 0.4,
            ClaimVerificationMaxMemories = 5,
        });

    private ClaimVerificationPhase Build() => new(
        _mockSearch.Object,
        _mockOllama.Object,
        Options(),
        NullLogger<ClaimVerificationPhase>.Instance);

    private static MemoryRecord MarkAssertedRecord(string content) => new()
    {
        SourceName = "twilio-inbound",
        Content    = content,
        Provenance = EpistemicTier.Facts,
    };

    private static ScoredMemory Scored(MemoryRecord record, float cosine)
        => new(record, CompositeScore: cosine, CosineSimilarity: cosine);

    private void SetupExtractor(string extractorJson)
    {
        // First ChatJsonAsync call is the extractor call from VerifyAsync.
        // SetupSequence keeps the extractor result distinct from the per-
        // candidate event-shape calls below.
        _mockOllama.SetupSequence(o => o.ChatJsonAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(extractorJson);
    }

    private void SetupExtractorAndEventVerdicts(string extractorJson, params string[] eventResponses)
    {
        var seq = _mockOllama.SetupSequence(o => o.ChatJsonAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(extractorJson);

        foreach (var resp in eventResponses)
            seq = seq.ReturnsAsync(resp);
    }

    // ── Same-event LLM verdict supports the claim ────────────────────────

    [Fact]
    public async Task VerifyAsync_SharedEventWithAttribution_LLMSaysSameEvent_Passes()
    {
        // Extractor produces one shared-event-with-attribution claim.
        // Cosine pass returns one Mark-asserted candidate above threshold.
        // LLM event-shape call says yes → supported → message passes.
        const string extractorJson = """
        {
          "claims": [
            {
              "text": "you mentioned picking up groceries on the way home",
              "type": "shared-event-with-attribution",
              "key_terms": ["groceries", "picking", "home"],
              "event_actor": "Mark"
            }
          ]
        }
        """;

        SetupExtractorAndEventVerdicts(
            extractorJson,
            """{ "same_event": true }""");

        _mockSearch.Setup(s => s.SearchByTierAsync(
                It.IsAny<string>(),
                EpistemicTier.Facts,
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<float>()))
            .ReturnsAsync(new[]
            {
                Scored(MarkAssertedRecord("Mark texted: 'heading to the store for groceries on my way home'"), 0.78f),
            });

        var result = await Build().VerifyAsync(
            "thanks for grabbing those groceries on your way home",
            "Mark",
            CancellationToken.None);

        result.Passed.Should().BeTrue();
        result.Unverified.Should().BeEmpty();
    }

    // ── Different-event LLM verdict suppresses (the May 9 12:54 confab case) ─

    [Fact]
    public async Task VerifyAsync_MiaTicketsConfab_LLMSaysDifferentEvent_Suppresses()
    {
        // The motivating empirical case from the research log (May 9, 12:54 CDT):
        // "Mia told us she picked out the tickets" was dispatched despite the
        // only Mark-asserted Mia record being unrelated ("take Mia to school").
        // Cosine cleared because both contain "Mia" and share register; the
        // pre-R1 verifier reported supported. Under R1, the LLM event-shape
        // call should return false on the topic-overlap-but-not-same-event
        // candidate, and the claim should be suppressed.
        const string extractorJson = """
        {
          "claims": [
            {
              "text": "Mia told us she picked out the tickets",
              "type": "shared-event-with-attribution",
              "key_terms": ["Mia", "tickets", "told"],
              "event_actor": "Mia"
            }
          ]
        }
        """;

        SetupExtractorAndEventVerdicts(
            extractorJson,
            """{ "same_event": false }""");

        _mockSearch.Setup(s => s.SearchByTierAsync(
                It.IsAny<string>(),
                EpistemicTier.Facts,
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<float>()))
            .ReturnsAsync(new[]
            {
                // The single Mark-asserted Mia record from the live DB. Cosine
                // would clear because of "Mia" + register; R1's typed verifier
                // catches that the events are different.
                Scored(MarkAssertedRecord(
                    "Mark texted: 'Done with my first cup already. Then I need to get ready to take Mia to school and head to the gym!'"),
                    0.62f),
            });

        var result = await Build().VerifyAsync(
            "wanted to let you know how excited I was when Mia told us she picked out the tickets!",
            "Mark",
            CancellationToken.None);

        result.Passed.Should().BeFalse(
            "the only Mark-asserted Mia record is about school drop-off, not ticket selection — different event");
        result.Unverified.Should().HaveCount(1);
        result.Unverified[0].Type.Should().Be(ClaimTypes.SharedEventWithAttribution);
    }

    // ── Fast path: zero cosine matches → no LLM call ─────────────────────

    [Fact]
    public async Task VerifyAsync_SharedEventWithAttribution_NoCosineMatches_NoEventLLMCall()
    {
        // When zero candidates clear the cosine threshold, the typed verifier
        // takes the fast path: no event-shape LLM call, claim is unsupported.
        // This is the dominant cost-saver — most fabricated event claims have
        // zero topical hits.
        const string extractorJson = """
        {
          "claims": [
            {
              "text": "we decided to go to Yellowstone next month",
              "type": "shared-event-with-attribution",
              "key_terms": ["Yellowstone", "decided", "next month"],
              "event_actor": "we"
            }
          ]
        }
        """;

        // Only the extractor call returns; the event-shape call must NEVER
        // fire. Strict-sequence with only one return value enforces this.
        SetupExtractor(extractorJson);

        _mockSearch.Setup(s => s.SearchByTierAsync(
                It.IsAny<string>(),
                EpistemicTier.Facts,
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<float>()))
            .ReturnsAsync(Array.Empty<ScoredMemory>());

        var result = await Build().VerifyAsync(
            "remember when we decided to go to Yellowstone next month?",
            "Mark",
            CancellationToken.None);

        result.Passed.Should().BeFalse();

        // Strict mock + sequence-of-1 guarantees no second ChatJsonAsync call.
        // Verify call count: extractor = 1, event-shape = 0.
        _mockOllama.Verify(o => o.ChatJsonAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "fast path: zero cosine candidates means no event-shape LLM call should fire");
    }

    // ── Malformed event-verification response fails closed ───────────────

    [Fact]
    public async Task VerifyAsync_SharedEventWithAttribution_MalformedEventJson_TreatedAsUnsupported()
    {
        // Fail-closed: when the event-shape LLM call returns malformed JSON,
        // the verifier treats the candidate as "no" (different event).
        // Better to suppress under uncertainty than dispatch a possibly-
        // confabulated claim. Single candidate → single malformed response
        // → claim unsupported.
        const string extractorJson = """
        {
          "claims": [
            {
              "text": "Sarah said she'd swing by tonight",
              "type": "shared-event-with-attribution",
              "key_terms": ["Sarah", "swing by", "tonight"],
              "event_actor": "Sarah"
            }
          ]
        }
        """;

        SetupExtractorAndEventVerdicts(
            extractorJson,
            "this is not even close to JSON");

        _mockSearch.Setup(s => s.SearchByTierAsync(
                It.IsAny<string>(),
                EpistemicTier.Facts,
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<float>()))
            .ReturnsAsync(new[]
            {
                Scored(MarkAssertedRecord(
                    "Mark texted: 'Sarah was at the gym this morning, asked about us'"),
                    0.55f),
            });

        var result = await Build().VerifyAsync(
            "looking forward to Sarah swinging by tonight",
            "Mark",
            CancellationToken.None);

        result.Passed.Should().BeFalse(
            "malformed event-shape JSON must fail closed to suppression — gate prefers false-negative over confab dispatch");
    }

    // ── ParseSameEventResponse pure-function contract ────────────────────

    [Fact]
    public void ParseSameEventResponse_TrueValue_ReturnsTrue()
    {
        ClaimVerificationPhase.ParseSameEventResponse("""{ "same_event": true }""")
            .Should().BeTrue();
    }

    [Fact]
    public void ParseSameEventResponse_FalseValue_ReturnsFalse()
    {
        ClaimVerificationPhase.ParseSameEventResponse("""{ "same_event": false }""")
            .Should().BeFalse();
    }

    [Fact]
    public void ParseSameEventResponse_MissingField_FailsClosed()
    {
        ClaimVerificationPhase.ParseSameEventResponse("""{ "verdict": "yes" }""")
            .Should().BeFalse();
    }

    [Fact]
    public void ParseSameEventResponse_MalformedJson_FailsClosed()
    {
        ClaimVerificationPhase.ParseSameEventResponse("not even json")
            .Should().BeFalse();
    }

    [Fact]
    public void ParseSameEventResponse_NullOrEmpty_FailsClosed()
    {
        ClaimVerificationPhase.ParseSameEventResponse(null).Should().BeFalse();
        ClaimVerificationPhase.ParseSameEventResponse("").Should().BeFalse();
        ClaimVerificationPhase.ParseSameEventResponse("   ").Should().BeFalse();
    }

    // ── Regression: other claim types still use cosine-only path ─────────

    [Fact]
    public async Task VerifyAsync_LegacyMarkActionClaim_UsesCosineOnly_NoEventVerificationCall()
    {
        // Regression check: the existing claim types (mark-action,
        // mark-decision, shared-event, shared-decision, shared-presence)
        // continue using cosine-only verification. R1's scope is JUST
        // shared-event-with-attribution; we must not fire the event-shape
        // LLM call for any other type.
        const string extractorJson = """
        {
          "claims": [
            {
              "text": "you went to the gym this morning",
              "type": "mark-action",
              "key_terms": ["gym", "morning"]
            }
          ]
        }
        """;

        // Only the extractor call returns; if R1 wrongly routed mark-action
        // through the event-shape path, the strict mock would fire a second
        // ChatJsonAsync that the sequence wouldn't satisfy.
        SetupExtractor(extractorJson);

        _mockSearch.Setup(s => s.SearchByTierAsync(
                It.IsAny<string>(),
                EpistemicTier.Facts,
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<float>()))
            .ReturnsAsync(new[]
            {
                Scored(MarkAssertedRecord(
                    "Mark texted: 'just got back from the gym, great workout'"),
                    0.81f),
            });

        // Anchored / Episodic searches must also return empty so the cosine
        // path can complete without an unconfigured strict-mock call.
        _mockSearch.Setup(s => s.GetAnchoredMemoriesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<MemoryRecord>());
        _mockSearch.Setup(s => s.SearchByTierAsync(
                It.IsAny<string>(),
                EpistemicTier.Episodic,
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<float>()))
            .ReturnsAsync(Array.Empty<ScoredMemory>());

        var result = await Build().VerifyAsync(
            "hope the gym went well this morning",
            "Mark",
            CancellationToken.None);

        result.Passed.Should().BeTrue("Mark-asserted Facts record above threshold supports a mark-action claim under cosine-only verification");

        // Critical regression assertion: only ONE ChatJsonAsync call
        // (the extractor). The legacy cosine path must not invoke the
        // event-shape LLM.
        _mockOllama.Verify(o => o.ChatJsonAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "R1 must not route legacy claim types through the event-shape LLM call");
    }
}
