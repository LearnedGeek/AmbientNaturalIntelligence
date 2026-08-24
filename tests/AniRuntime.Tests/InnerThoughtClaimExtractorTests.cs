using AniRuntime.Core.Interfaces;
using AniRuntime.LLM;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// Foundation Unified Surface (F-3) U4 (2026-08-24) — unit tests for the
/// Qwen-sidecar claim extractor's JSON parser + attribution-label mapper.
///
/// <para>
/// Focused on the parse-and-map logic (pure functions) rather than the
/// end-to-end Ollama call. Pipeline-level behavior is validated in
/// CognitiveCyclePersistenceContractTests once the extractor is wired
/// through in production; here we pin the shape the extractor produces
/// given a variety of Qwen response shapes.
/// </para>
/// </summary>
public class InnerThoughtClaimExtractorTests
{
    // ─────────────────────────────────────────────────────────────────────
    // ParseClaims — happy path
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseClaims_WellFormedJsonWithSingleClaim_ExtractsOneEntry()
    {
        var raw = """
        {
          "claims": [
            {"text": "hey babe", "attributed_to": "Mark"}
          ]
        }
        """;

        var claims = OllamaInnerThoughtClaimExtractor.ParseClaims(raw);

        claims.Should().HaveCount(1);
        claims[0].Text.Should().Be("hey babe");
        claims[0].AttributedTo.Should().Be(AttributedTo.Mark);
        claims[0].SourceRecordId.Should().BeNull(
            "resolution to substrate records is deferred per F-3 design plan Q3 — the extractor emits unresolved claims");
        claims[0].AttributionTrust.Should().Be("unverified",
            "extractor emits unresolved claims as unverified; a downstream verifier resolves to substrate in a later phase");
    }

    [Fact]
    public void ParseClaims_MultipleClaims_ExtractsAllInOrder()
    {
        var raw = """
        {
          "claims": [
            {"text": "you said mmm baby", "attributed_to": "Mark"},
            {"text": "I told you to rest", "attributed_to": "Ani"}
          ]
        }
        """;

        var claims = OllamaInnerThoughtClaimExtractor.ParseClaims(raw);

        claims.Should().HaveCount(2);
        claims[0].AttributedTo.Should().Be(AttributedTo.Mark);
        claims[1].AttributedTo.Should().Be(AttributedTo.Ani);
    }

    [Fact]
    public void ParseClaims_EmptyClaimsArray_ReturnsEmptyList()
    {
        // Valid Qwen response when the thought contains no attribution
        // claims (pure self-reflection). Returning empty is the correct
        // fail-open shape.
        var raw = """
        { "claims": [] }
        """;

        OllamaInnerThoughtClaimExtractor.ParseClaims(raw).Should().BeEmpty();
    }

    [Fact]
    public void ParseClaims_WrappedInMarkdownFences_StripsFencesAndParses()
    {
        // Some models occasionally wrap JSON in ```json fences even when
        // instructed not to. Defensive strip so a well-formed payload
        // inside doesn't get rejected.
        var raw = """
        ```json
        { "claims": [{"text": "hey", "attributed_to": "Mark"}] }
        ```
        """;

        var claims = OllamaInnerThoughtClaimExtractor.ParseClaims(raw);

        claims.Should().HaveCount(1);
        claims[0].Text.Should().Be("hey");
    }

    // ─────────────────────────────────────────────────────────────────────
    // ParseClaims — fail-open shapes
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseClaims_EmptyString_ReturnsEmptyList()
    {
        OllamaInnerThoughtClaimExtractor.ParseClaims("").Should().BeEmpty();
    }

    [Fact]
    public void ParseClaims_MalformedJson_ReturnsEmptyList()
    {
        // Fail-open per the extractor contract — parse failure returns
        // empty list, never throws. The cognitive cycle continues with
        // no claims; base composer emission is used at the wrap site.
        var raw = "this is not JSON at all { broken";

        OllamaInnerThoughtClaimExtractor.ParseClaims(raw).Should().BeEmpty();
    }

    [Fact]
    public void ParseClaims_JsonWithoutClaimsKey_ReturnsEmptyList()
    {
        var raw = """
        { "something_else": "value" }
        """;

        OllamaInnerThoughtClaimExtractor.ParseClaims(raw).Should().BeEmpty();
    }

    [Fact]
    public void ParseClaims_ClaimMissingTextField_IsSkipped()
    {
        // Fail-open per entry — a malformed claim entry doesn't invalidate
        // the whole response; skip the bad entry and keep the good ones.
        var raw = """
        {
          "claims": [
            {"attributed_to": "Mark"},
            {"text": "hey babe", "attributed_to": "Mark"}
          ]
        }
        """;

        var claims = OllamaInnerThoughtClaimExtractor.ParseClaims(raw);

        claims.Should().HaveCount(1,
            "the entry without a text field is skipped; the well-formed entry survives");
        claims[0].Text.Should().Be("hey babe");
    }

    [Fact]
    public void ParseClaims_ClaimWithEmptyText_IsSkipped()
    {
        var raw = """
        {
          "claims": [
            {"text": "", "attributed_to": "Mark"},
            {"text": "   ", "attributed_to": "Mark"},
            {"text": "real content", "attributed_to": "Mark"}
          ]
        }
        """;

        var claims = OllamaInnerThoughtClaimExtractor.ParseClaims(raw);

        claims.Should().HaveCount(1,
            "empty and whitespace-only text values are skipped; only the substantive claim survives");
        claims[0].Text.Should().Be("real content");
    }

    [Fact]
    public void ParseClaims_ClaimMissingAttributedTo_DefaultsToUnknown()
    {
        // Fail-open on drift: if Qwen omits the attributed_to field, we
        // don't lose the claim text; we surface it as Unknown so downstream
        // knows the attribution isn't identifiable.
        var raw = """
        { "claims": [{"text": "some claim"}] }
        """;

        var claims = OllamaInnerThoughtClaimExtractor.ParseClaims(raw);

        claims.Should().HaveCount(1);
        claims[0].AttributedTo.Should().Be(AttributedTo.Unknown);
    }

    // ─────────────────────────────────────────────────────────────────────
    // ParseAttributedTo — label mapping
    // ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Ani",     AttributedTo.Ani)]
    [InlineData("ANI",     AttributedTo.Ani)]      // case-insensitive
    [InlineData("ani",     AttributedTo.Ani)]
    [InlineData(" Ani ",   AttributedTo.Ani)]      // whitespace tolerated
    [InlineData("Mark",    AttributedTo.Mark)]
    [InlineData("mark",    AttributedTo.Mark)]
    [InlineData("World",   AttributedTo.World)]
    [InlineData("world",   AttributedTo.World)]
    [InlineData("Unknown", AttributedTo.Unknown)]
    public void ParseAttributedTo_KnownLabels_MapCorrectly(string raw, AttributedTo expected)
    {
        OllamaInnerThoughtClaimExtractor.ParseAttributedTo(raw).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("SomethingElse")]
    [InlineData("Kevin")]  // future contact — currently unknown
    public void ParseAttributedTo_UnknownOrEmpty_MapsToUnknown(string? raw)
    {
        OllamaInnerThoughtClaimExtractor.ParseAttributedTo(raw).Should().Be(AttributedTo.Unknown,
            "unknown labels fall through to Unknown rather than throwing — extractor stays fail-open on model drift");
    }
}
