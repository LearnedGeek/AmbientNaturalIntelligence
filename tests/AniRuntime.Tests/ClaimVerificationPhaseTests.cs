using AniRuntime.Loops;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// Tests for the Feature 14 v2 claim-extraction JSON parser and the shape of
/// the ClaimVerificationResult record.
///
/// The integration behavior of ClaimVerificationPhase.VerifyAsync — the full
/// extract → corroborate → suppress flow — is not covered here because it
/// requires a real IOllamaClient and IMemorySearch, both of which need a live
/// Ollama instance plus seeded memory. Integration-level verification happens
/// in deployed-system observation (research log) rather than in unit tests.
///
/// What these tests cover:
///   - JSON parser: well-formed input, malformed input, missing fields,
///     wrong types, empty arrays, non-array claims. Parser must never throw.
///   - Result record: Pass / Suppress factory correctness.
/// </summary>
public class ClaimVerificationPhaseTests
{
    // ── Parser: well-formed input ─────────────────────────────────────────────

    [Fact]
    public void ParseClaims_ReturnsAllFields_FromWellFormedJson()
    {
        const string json = """
        {
          "claims": [
            {
              "text": "you brought them over",
              "type": "mark-action",
              "key_terms": ["brought", "flowers"]
            },
            {
              "text": "we decided on purple",
              "type": "shared-decision",
              "key_terms": ["decided", "purple"]
            }
          ]
        }
        """;

        var claims = ClaimVerificationPhase.ParseClaims(json);

        claims.Should().HaveCount(2);
        claims[0].Text.Should().Be("you brought them over");
        claims[0].Type.Should().Be("mark-action");
        claims[0].KeyTerms.Should().BeEquivalentTo(new[] { "brought", "flowers" });
        claims[1].Text.Should().Be("we decided on purple");
        claims[1].Type.Should().Be("shared-decision");
    }

    [Fact]
    public void ParseClaims_ReturnsEmpty_WhenClaimsArrayIsEmpty()
    {
        const string json = """{ "claims": [] }""";
        var claims = ClaimVerificationPhase.ParseClaims(json);
        claims.Should().BeEmpty();
    }

    // ── Parser: tolerance of malformed input ─────────────────────────────────

    [Fact]
    public void ParseClaims_ReturnsEmpty_WhenJsonIsNull()
    {
        var claims = ClaimVerificationPhase.ParseClaims(null);
        claims.Should().BeEmpty();
    }

    [Fact]
    public void ParseClaims_ReturnsEmpty_WhenJsonIsWhitespace()
    {
        var claims = ClaimVerificationPhase.ParseClaims("   ");
        claims.Should().BeEmpty();
    }

    [Fact]
    public void ParseClaims_DoesNotThrow_OnMalformedJson()
    {
        // Gate bugs must not silence Ani: parsing errors are caught and return empty,
        // which the VerifyAsync path treats as "no claims" → PASS (dispatch allowed).
        var act = () => ClaimVerificationPhase.ParseClaims("{ not valid json");
        act.Should().NotThrow();
        ClaimVerificationPhase.ParseClaims("{ not valid json").Should().BeEmpty();
    }

    [Fact]
    public void ParseClaims_ReturnsEmpty_WhenClaimsPropertyMissing()
    {
        const string json = """{ "some_other_field": "value" }""";
        var claims = ClaimVerificationPhase.ParseClaims(json);
        claims.Should().BeEmpty();
    }

    [Fact]
    public void ParseClaims_ReturnsEmpty_WhenClaimsIsNotAnArray()
    {
        const string json = """{ "claims": "not an array" }""";
        var claims = ClaimVerificationPhase.ParseClaims(json);
        claims.Should().BeEmpty();
    }

    // ── Parser: default / missing fields within a claim ──────────────────────

    [Fact]
    public void ParseClaims_SkipsClaim_WhenTextIsMissing()
    {
        const string json = """
        {
          "claims": [
            { "type": "mark-action", "key_terms": ["did"] },
            { "text": "real claim", "type": "mark-action", "key_terms": [] }
          ]
        }
        """;
        var claims = ClaimVerificationPhase.ParseClaims(json);
        claims.Should().HaveCount(1);
        claims[0].Text.Should().Be("real claim");
    }

    [Fact]
    public void ParseClaims_TreatsMissingType_AsEmptyString()
    {
        // Type is informational; a claim without type is still processed. The
        // downstream verification does not dispatch on type alone.
        const string json = """
        {
          "claims": [
            { "text": "some claim", "key_terms": ["x"] }
          ]
        }
        """;
        var claims = ClaimVerificationPhase.ParseClaims(json);
        claims.Should().HaveCount(1);
        claims[0].Type.Should().Be("");
    }

    [Fact]
    public void ParseClaims_TreatsMissingKeyTerms_AsEmptyList()
    {
        const string json = """
        {
          "claims": [
            { "text": "some claim", "type": "mark-action" }
          ]
        }
        """;
        var claims = ClaimVerificationPhase.ParseClaims(json);
        claims.Should().HaveCount(1);
        claims[0].KeyTerms.Should().BeEmpty();
    }

    [Fact]
    public void ParseClaims_FiltersBlankKeyTerms()
    {
        const string json = """
        {
          "claims": [
            {
              "text": "some claim",
              "type": "mark-action",
              "key_terms": ["valid", "", "   ", "also-valid"]
            }
          ]
        }
        """;
        var claims = ClaimVerificationPhase.ParseClaims(json);
        claims.Should().HaveCount(1);
        claims[0].KeyTerms.Should().BeEquivalentTo(new[] { "valid", "also-valid" });
    }

    [Fact]
    public void ParseClaims_TreatsKeyTermsAsEmpty_WhenNotAnArray()
    {
        const string json = """
        {
          "claims": [
            { "text": "some claim", "type": "mark-action", "key_terms": "not-an-array" }
          ]
        }
        """;
        var claims = ClaimVerificationPhase.ParseClaims(json);
        claims.Should().HaveCount(1);
        claims[0].KeyTerms.Should().BeEmpty();
    }

    // ── Result record: factory correctness ───────────────────────────────────

    [Fact]
    public void ClaimVerificationResult_Pass_SetsPassedTrue_AndNoUnverified()
    {
        var claim = new ExtractedClaim("x", "mark-action", new[] { "y" });
        var result = ClaimVerificationResult.Pass("disabled", new[] { claim });

        result.Passed.Should().BeTrue();
        result.Reason.Should().Be("disabled");
        result.Claims.Should().HaveCount(1);
        result.Unverified.Should().BeEmpty();
    }

    [Fact]
    public void ClaimVerificationResult_Suppress_SetsPassedFalse_AndPopulatesUnverified()
    {
        var claim = new ExtractedClaim("x", "mark-action", new[] { "y" });
        var result = ClaimVerificationResult.Suppress(
            "unsupported",
            new[] { claim },
            new[] { claim });

        result.Passed.Should().BeFalse();
        result.Reason.Should().Be("unsupported");
        result.Claims.Should().HaveCount(1);
        result.Unverified.Should().HaveCount(1);
    }
}
