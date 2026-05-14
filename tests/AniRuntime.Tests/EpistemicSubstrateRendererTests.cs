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
/// <see cref="StructuredConversationSummary"/> records in, assert text out.
///
/// The slice is the testable seam. Before the renderer was extracted, the
/// rendering logic was inlined in <see cref="PromptBuilder.BuildOutreachPrompt"/>
/// and could only be tested by building the full prompt and string-matching
/// over its output. After extraction, the slice has a 2-method surface
/// (render + format) and tests run in milliseconds.
/// </summary>
public class EpistemicSubstrateRendererTests
{
    private readonly IEpistemicSubstrateRenderer _sut = new EpistemicSubstrateRenderer();

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

        // The FC-004 invariant the slice exists to enforce: the model
        // must see explicit framing distinguishing Mark-asserted content
        // (established) from Ani's prior conversational claims (her own
        // prior output — NOT yet established as fact).
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
            "an empty contact name shouldn't render a malformed header like " +
            "'with  — epistemic framing:'; the renderer falls back to a generic " +
            "label.");
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
        // SRP corollary: the slice is one block with one header, not one
        // header per turn. This pin guards against accidental per-turn
        // framing duplication if the renderer is later refactored.
        var summary = BuildSummary(
            (Mark, "a"), (Ani, "b"), (Mark, "c"), (Ani, "d"), (Mark, "e"));

        var result = _sut.RenderActiveThreadSlice(summary, "Mark");

        var headerOccurrences = result.Split("epistemic framing:").Length - 1;
        headerOccurrences.Should().Be(1,
            "the slice MUST emit one framing header per slice, not one per turn.");
    }

    // ── Helpers ─────────────────────────────────────────────────────────

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
}
