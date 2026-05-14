using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.LLM;
using FluentAssertions;
using Xunit;

namespace AniRuntime.Tests;

/// <summary>
/// Theme M follow-on (2026-05-14) — unit tests for
/// <see cref="EpistemicSubstrateRenderer"/>.
///
/// These tests demonstrate the SOLID payoff Mark identified in the
/// 2026-05-14 architecture discussion: each slice renderer is tested in
/// isolation — no <see cref="PromptBuilder"/> round-trip, no
/// <see cref="ContextSnapshot"/> construction, no Ollama mocks. Feed
/// inputs in, assert text out.
///
/// The slice is the testable seam. Before the renderer was extracted, the
/// rendering logic was inlined across ~10 PromptBuilder methods and could
/// only be tested by building the full prompt and string-matching over its
/// output. After extraction, each slice has a small surface and tests run
/// in milliseconds.
///
/// **Test categories per slice:**
/// 1. Empty / null inputs → empty output (caller can skip the prompt block).
/// 2. Framing header content — the FC-XXX invariant the slice exists to enforce.
/// 3. Header appears once per slice (SRP corollary).
/// 4. Content rendered verbatim after the framing header.
/// 5. Sensible fallback when ancillary inputs (contact name, etc.) missing.
/// </summary>
public class EpistemicSubstrateRendererTests
{
    private readonly IEpistemicSubstrateRenderer _sut = new EpistemicSubstrateRenderer();

    // ───────────────────────────────────────────────────────────────────
    // RenderActiveThreadSlice
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public void RenderActiveThreadSlice_NullSummary_ReturnsEmpty()
    {
        var result = _sut.RenderActiveThreadSlice(summary: null, contactName: "Mark");
        result.Should().BeEmpty(
            "a null summary has no content to render; empty is the correct " +
            "signal that the caller should skip this prompt block entirely.");
    }

    [Fact]
    public void RenderActiveThreadSlice_EmptyTurns_ReturnsEmpty()
    {
        var empty = StructuredConversationSummary.Empty;
        var result = _sut.RenderActiveThreadSlice(empty, "Mark");
        result.Should().BeEmpty(
            "an empty turn list has no content; the caller should skip the " +
            "prompt block rather than emit a header with nothing under it.");
    }

    [Fact]
    public void RenderActiveThreadSlice_ContainsEpistemicAsymmetryFraming()
    {
        var summary = BuildSummary(
            (Mark, "hey, how's the bookstore today?"),
            (Ani,  "quiet morning. shelving the romance set."));

        var result = _sut.RenderActiveThreadSlice(summary, "Mark");

        result.Should().Contain("established",
            "the slice MUST tell the model Mark's lines are established assertions.");
        result.Should().Contain("NOT yet established",
            "the slice MUST tell the model Ani's lines are her prior output, " +
            "NOT yet verified — addressing the May 12 23:23 production case " +
            "where 'the note on her windshield' (her own prior claim) was " +
            "treated as established context for a subsequent decision.");
    }

    [Fact]
    public void RenderActiveThreadSlice_IncludesContactNameInFraming()
    {
        var summary = BuildSummary((Mark, "ok cool."));
        var result = _sut.RenderActiveThreadSlice(summary, "Mark");
        result.Should().Contain("Mark",
            "the slice header MUST name the contact so the model can tie " +
            "the 'established' framing to the right speaker.");
    }

    [Fact]
    public void RenderActiveThreadSlice_FallsBackToGenericLabel_WhenContactNameMissing()
    {
        var summary = BuildSummary((Mark, "morning."));
        var result = _sut.RenderActiveThreadSlice(summary, contactName: "");
        result.Should().Contain("the contact",
            "an empty contact name shouldn't render a malformed header.");
    }

    [Fact]
    public void RenderActiveThreadSlice_IncludesAllTurns_VerbatimAfterHeader()
    {
        var summary = BuildSummary(
            (Mark, "I picked up coffee on the way."),
            (Ani,  "perfect timing."),
            (Mark, "yeah, the line was short."));

        var result = _sut.RenderActiveThreadSlice(summary, "Mark");

        result.Should().Contain("I picked up coffee on the way.");
        result.Should().Contain("perfect timing.");
        result.Should().Contain("yeah, the line was short.");
    }

    [Fact]
    public void RenderActiveThreadSlice_FramingHeaderAppearsOnce()
    {
        var summary = BuildSummary(
            (Mark, "a"), (Ani, "b"), (Mark, "c"), (Ani, "d"), (Mark, "e"));
        var result = _sut.RenderActiveThreadSlice(summary, "Mark");
        var headerOccurrences = result.Split("epistemic framing:").Length - 1;
        headerOccurrences.Should().Be(1,
            "the slice MUST emit one framing header per slice, not one per turn.");
    }

    // ───────────────────────────────────────────────────────────────────
    // RenderMarkAssertedFactsSlice
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public void RenderMarkAssertedFactsSlice_NullOrEmpty_ReturnsEmpty()
    {
        _sut.RenderMarkAssertedFactsSlice(facts: null, "Mark").Should().BeEmpty();
        _sut.RenderMarkAssertedFactsSlice(Array.Empty<MemoryRecord>(), "Mark").Should().BeEmpty();
        _sut.RenderMarkAssertedFactsSlice(new[] { Memory("") }, "Mark").Should().BeEmpty(
            "blank-content records filter out and yield empty slice.");
    }

    [Fact]
    public void RenderMarkAssertedFactsSlice_ContainsFactsConstraintFraming()
    {
        var facts = new[] { Memory("Mark is teaching Spanish at WCTC on Tuesdays.") };
        var result = _sut.RenderMarkAssertedFactsSlice(facts, "Mark");

        result.Should().Contain("FACTS about Mark",
            "the slice header MUST label the block as facts about the contact.");
        result.Should().Contain("ONLY claims",
            "the slice MUST encode the FC-005 'only these may be asserted' " +
            "discipline — model cannot fabricate Mark-world specifics not in this list.");
        result.Should().Contain("you don't know it",
            "the slice MUST tell the model what to do when a Mark-world entity " +
            "is missing from the facts (don't invent).");
    }

    [Fact]
    public void RenderMarkAssertedFactsSlice_RendersUpToSixRows()
    {
        // 8 records — slice caps at 6
        var many = Enumerable.Range(1, 8)
            .Select(i => Memory($"fact-row-{i:00}"))
            .ToList();
        var result = _sut.RenderMarkAssertedFactsSlice(many, "Mark");

        result.Should().Contain("fact-row-01");
        result.Should().Contain("fact-row-06");
        result.Should().NotContain("fact-row-07",
            "slice MUST cap at 6 rows to match the legacy inline rendering budget.");
        result.Should().NotContain("fact-row-08");
    }

    // ───────────────────────────────────────────────────────────────────
    // RenderAniWorldSlice
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public void RenderAniWorldSlice_AllInputsEmpty_ReturnsEmpty()
    {
        _sut.RenderAniWorldSlice(occupation: null, natureGrounding: null, recentWorldExperiences: null)
            .Should().BeEmpty();
        _sut.RenderAniWorldSlice("", Array.Empty<string>(), Array.Empty<MemoryRecord>())
            .Should().BeEmpty();
    }

    [Fact]
    public void RenderAniWorldSlice_ContainsLatitudeFraming()
    {
        var result = _sut.RenderAniWorldSlice(
            occupation:              "bookstore clerk in a small Wisconsin town",
            natureGrounding:         null,
            recentWorldExperiences:  null);

        result.Should().Contain("latitude",
            "the slice MUST encode the FC-002 self-world-allow rule positively — " +
            "Ani has latitude on her own bookstore world.");
        result.Should().Contain("does NOT apply to your own interior",
            "the slice MUST distinguish self-world from Mark-world rules.");
        result.Should().Contain("bookstore clerk in a small Wisconsin town",
            "canonical occupation MUST appear in the slice when provided.");
    }

    [Fact]
    public void RenderAniWorldSlice_CapsNatureGroundingAtTwo()
    {
        var nature = new[] { "nature-a", "nature-b", "nature-c", "nature-d" };
        var result = _sut.RenderAniWorldSlice("clerk", nature, null);
        result.Should().Contain("nature-a");
        result.Should().Contain("nature-b");
        result.Should().NotContain("nature-c",
            "nature-grounding cap matches the legacy lean-prompt budget of 2.");
    }

    [Fact]
    public void RenderAniWorldSlice_CapsRecentWorldExperiencesAtThree()
    {
        var world = Enumerable.Range(1, 5)
            .Select(i => Memory($"world-experience-{i}"))
            .ToList();
        var result = _sut.RenderAniWorldSlice("clerk", null, world);

        result.Should().Contain("world-experience-1");
        result.Should().Contain("world-experience-3");
        result.Should().NotContain("world-experience-4",
            "world-experience cap matches the legacy rendering budget of 3.");
    }

    // ───────────────────────────────────────────────────────────────────
    // RenderAniPriorSlice
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public void RenderAniPriorSlice_NullOrEmpty_ReturnsEmpty()
    {
        _sut.RenderAniPriorSlice(null).Should().BeEmpty();
        _sut.RenderAniPriorSlice(Array.Empty<MemoryRecord>()).Should().BeEmpty();
    }

    [Fact]
    public void RenderAniPriorSlice_ContainsNotYetEstablishedFraming()
    {
        var prior = new[] { Memory("I just got home and found this on my windshield.") };
        var result = _sut.RenderAniPriorSlice(prior);

        result.Should().Contain("ANI-PRIOR",
            "the slice MUST label the block explicitly so the model can " +
            "distinguish it from facts.");
        result.Should().Contain("NOT yet established",
            "the slice MUST tell the model these are her own prior turns, " +
            "not facts — addresses the May 12 23:23 windshield-as-fact case.");
        result.Should().Contain("do NOT reason from them",
            "the slice MUST tell the model not to treat her prior claims as " +
            "if the contact had verified them.");
    }

    [Fact]
    public void RenderAniPriorSlice_RendersUpToFiveRows()
    {
        var many = Enumerable.Range(1, 7)
            .Select(i => Memory($"prior-row-{i:00}"))
            .ToList();
        var result = _sut.RenderAniPriorSlice(many);

        result.Should().Contain("prior-row-01");
        result.Should().Contain("prior-row-05");
        result.Should().NotContain("prior-row-06",
            "slice MUST cap at 5 rows to keep the prompt block manageable.");
    }

    // ───────────────────────────────────────────────────────────────────
    // RenderClosedConversationSlice
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public void RenderClosedConversationSlice_NullOrBlankGist_ReturnsEmpty()
    {
        _sut.RenderClosedConversationSlice(null, "Mark").Should().BeEmpty();

        var blank = new ClosedConversationRecord { Gist = "" };
        _sut.RenderClosedConversationSlice(blank, "Mark").Should().BeEmpty(
            "a record with an empty gist has no content to surface.");
    }

    [Fact]
    public void RenderClosedConversationSlice_ContainsCallbackFraming()
    {
        var closed = new ClosedConversationRecord
        {
            Gist = "you and Mark talked about a weekend trip to Door County.",
        };
        var result = _sut.RenderClosedConversationSlice(closed, "Mark");

        result.Should().Contain("CLOSED-CONVERSATION",
            "the slice MUST label the block so consumers can tell it apart from active threads.");
        result.Should().Contain("established callback ground",
            "the slice MUST encode that the gist is safe to reference as shared history.");
        result.Should().Contain("do NOT lift verbatim",
            "the slice MUST tell the model the gist is substrate, not a script — " +
            "consistent with Vibe Loop V1's anti-parrot paraphrasing.");
        result.Should().Contain("weekend trip to Door County",
            "the gist content MUST appear in the rendered slice.");
    }

    // ───────────────────────────────────────────────────────────────────
    // RenderThreeAxisRuleSlice
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public void RenderThreeAxisRuleSlice_NeverEmpty()
    {
        _sut.RenderThreeAxisRuleSlice("Mark").Should().NotBeEmpty(
            "the three-axis rule slice is a static rule encoding; it has no " +
            "substrate inputs that could make it empty.");
    }

    [Fact]
    public void RenderThreeAxisRuleSlice_ContainsAllThreeAxes()
    {
        var result = _sut.RenderThreeAxisRuleSlice("Mark");

        // SUBJECT axis
        result.Should().Contain("SELF-WORLD");
        result.Should().Contain("SHARED");
        result.Should().Contain("MARK-WORLD",
            "the subject axis MUST enumerate self / shared / contact-world.");

        // MODALITY axis
        result.Should().Contain("FACTUAL");
        result.Should().Contain("MODAL",
            "the modality axis MUST enumerate factual vs. modal framing.");

        // SUBSTRATE axis
        result.Should().Contain("SUPPORTED");
        result.Should().Contain("NOVEL",
            "the substrate axis MUST enumerate supported vs. novel.");
    }

    [Fact]
    public void RenderThreeAxisRuleSlice_ContainsRule()
    {
        var result = _sut.RenderThreeAxisRuleSlice("Mark");
        result.Should().Contain("factual ⇒ (self-world OR substrate-supported)",
            "the rule MUST appear in canonical form so the model has the " +
            "decision procedure rather than a paraphrase.");
        result.Should().Contain("Modal claims always allowed",
            "modal-allowance is load-bearing for self-world latitude and " +
            "FC-002 'shared modal novel ALLOW' cases.");
    }

    [Fact]
    public void RenderThreeAxisRuleSlice_ContainsWorkedVerdicts()
    {
        var result = _sut.RenderThreeAxisRuleSlice("Mark");
        result.Should().Contain("SELF / factual / novel  → ALLOW");
        result.Should().Contain("SHARED / factual / novel → BLOCK");
        result.Should().Contain("SHARED / modal  / novel → ALLOW");
        result.Should().Contain("MARK-WORLD / factual / novel → BLOCK");
        result.Should().Contain("SHARED / factual / supported → ALLOW",
            "the verdicts table is the verifier's quick-reference for the rule.");
    }

    // ───────────────────────────────────────────────────────────────────
    // Helpers
    // ───────────────────────────────────────────────────────────────────

    private const string Mark = "Mark";
    private const string Ani  = "Ani";

    private static StructuredConversationSummary BuildSummary(params (string Speaker, string Content)[] turns)
    {
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);
        var rows = new List<SummaryTurn>();
        for (var i = 0; i < turns.Length; i++)
        {
            rows.Add(new SummaryTurn(
                At:      t0.AddSeconds(i * 30),
                Speaker: turns[i].Speaker,
                Content: turns[i].Content));
        }
        return new StructuredConversationSummary(
            FirstTurnAt: rows[0].At,
            LastTurnAt:  rows[^1].At,
            Turns:       rows);
    }

    private static MemoryRecord Memory(string content) => new()
    {
        Id         = Guid.NewGuid(),
        Type       = MemoryType.Semantic,
        Content    = content,
        Importance = 0.5f,
        OccurredAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        CreatedAt  = DateTimeOffset.UtcNow.AddMinutes(-5),
        Provenance = EpistemicTier.Facts,
    };
}
