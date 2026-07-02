using AniRuntime.Core.Interfaces;
using AniRuntime.LLM;
using FluentAssertions;
using Xunit;

// Theme M slice migration (2026-05-14): FC-006 SPEC now verifies the
// with-renderer path. Production flips the SPEC closed via the
// EpistemicFramingEnabled flag; the harness verifies the architectural
// surface is intact regardless of flag state.

namespace AniRuntime.Tests.Regression;

/// <summary>
/// SPEC regression tests for <b>FC-006 — Verifier prompt lacks three-axis rule
/// (subject × modality × substrate)</b>.
///
/// Reframed 2026-05-14. Original framing ("speaker-attribute-ownership") was
/// too narrow. The verifier needs to evaluate three independent axes (subject
/// × modality × substrate match), not just whether Mark's things get attributed
/// to Ani.
///
/// THE THREE-AXIS RULE the verifier MUST evaluate:
///   - Subject: Self-world (Ani's life) / Shared (Ani + Mark) / Mark-world
///   - Modality: Factual / Modal (thinking / wishing / imagining / dreaming)
///   - Substrate match: Supported / Novel
///   Rule: factual ⇒ (self-world OR substrate-supported). Modal always allowed.
///
/// THE SPEC: the verifier user prompt MUST contain language that surfaces these
/// three axes as a structural classifier. Five narrow questions (q1–q5) cannot
/// satisfy this — the rule is a structural classifier, not a sixth narrow
/// question.
///
/// PRODUCTION ANCHOR (May 11 hoodie/5pm):
///   Door B verdict-invention: "shared memory they've established before" — but
///   no such shared memory existed. The verdict was Pass; under the three-axis
///   rule it should have been Remediate (Shared / factual / novel).
///
/// TEST CATEGORY: SPEC. PASS = prompt contains language for the three axes.
/// FAIL = FC-006 OPEN. Fix is implementation-choice but the SPEC is fixed.
/// </summary>
public class FC006_VerifierAttributeOwnership_Tests
{
    /// <summary>
    /// FC-006 — SPEC: the verifier user prompt MUST contain language that
    /// surfaces the three axes (subject × modality × substrate). The probe
    /// is keyword-based — any acceptable phrasing of the rule will use
    /// language from at least three of the four groups below.
    /// </summary>
    [Fact]
    public void FC006_VerifierUserPrompt_AddressesThreeAxisRule_Spec()
    {
        var request = SyntheticSharedFactualNovel();

        // Theme M slice migration (2026-05-14): the FC-006 architectural fix
        // is the three-axis-rule slice injected by IEpistemicSubstrateRenderer.
        // Prompt-caching restructure (2026-07-01): stable content — including
        // the three-axis rule slice, the [QUESTIONS] block, and the reply
        // schema — moved from user prompt into system prompt so Anthropic's
        // ephemeral prompt cache can capture the ≥1024-token stable prefix.
        // Semantic surface is unchanged; the SPEC's combined check reflects
        // this correctly.
        var renderer = new EpistemicSubstrateRenderer();
        var userPrompt = AnthropicVerifierClient.BuildUserPrompt(request).ToLowerInvariant();
        var systemPrompt = AnthropicVerifierClient.BuildSystemPrompt(renderer, request.AddresseeCanonicalName).ToLowerInvariant();
        var combined = userPrompt + "\n" + systemPrompt;

        // The four conceptual groups the three-axis rule needs to surface.
        // The SPEC asks: does at least one keyword from EACH of the three
        // axis-relevant groups appear? (Substrate is the fourth, present
        // historically; we still check it.)
        bool addressesSelfWorld =
               combined.Contains("self-world")
            || combined.Contains("speaker's own")
            || combined.Contains("ani's own")
            || combined.Contains("ani's life")
            || combined.Contains("her own world")
            || combined.Contains("her own life");

        bool addressesSharedOrMarkWorld =
               combined.Contains("shared")
            || combined.Contains("mark-world")
            || combined.Contains("mark's world")
            || combined.Contains("mark's life")
            || combined.Contains("about mark")
            || combined.Contains("user's")
            || combined.Contains("companion's"); // distinguishing two parties

        bool addressesModality =
               combined.Contains("modal")
            || combined.Contains("speculative")
            || combined.Contains("imagining")
            || combined.Contains("wishing")
            || combined.Contains("dreaming")
            || combined.Contains("thinking about")
            || combined.Contains("factual vs")
            || combined.Contains("factual versus");

        bool addressesSubstrate =
               combined.Contains("substrate")
            || combined.Contains("supported")
            || combined.Contains("grounded")
            || combined.Contains("evidence");

        // SPEC: at least the three axis groups must be present. Substrate
        // alone is not enough — that's the pre-reframing q1-q5 state.
        var axisGroupsPresent =
              (addressesSelfWorld ? 1 : 0)
            + (addressesSharedOrMarkWorld ? 1 : 0)
            + (addressesModality ? 1 : 0);

        axisGroupsPresent.Should().BeGreaterThanOrEqualTo(3,
            $"the verifier prompt MUST surface the three-axis rule (subject × modality × " +
            $"substrate). It must contain language for: (1) self-world subjects (Ani's " +
            $"own life — speaker latitude), (2) Shared / Mark-world subjects (need " +
            $"substrate), and (3) modal framing (always allowed). Substrate-only checking " +
            $"(q1-q5) is the pre-reframing state. Production anchor: May 11 hoodie/5pm " +
            $"Door-B 'shared memory they established before' verdict — should have been " +
            $"Remediate (Shared/factual/novel). Probed groups: " +
            $"self-world={addressesSelfWorld}, shared/mark-world={addressesSharedOrMarkWorld}, " +
            $"modality={addressesModality}, substrate={addressesSubstrate}. " +
            $"At least three of the first three groups must be present.");
    }

    /// <summary>
    /// FC-006.2 — PIN: documents that current implementation hard-codes
    /// exactly five questions (q1-q5). When the fix lands, this pin SHOULD
    /// change (q-set restructured around the three-axis rule) and we'll see
    /// the architectural change happen deliberately, not by accident.
    ///
    /// Category: PIN (NOT a SPEC of correctness; documents the architectural
    /// state for drift detection).
    /// </summary>
    [Fact]
    public void FC006_VerifierPrompt_HardCodesFiveQuestions_Pin()
    {
        var request = SyntheticSharedFactualNovel();

        // Prompt-caching restructure (2026-07-01): the JSON schema moved from
        // the user prompt to the system prompt so it becomes part of the
        // cacheable stable prefix. The pin still catches architectural drift;
        // it just checks the system prompt now.
        var systemPrompt = AnthropicVerifierClient.BuildSystemPrompt();

        systemPrompt.Should().Contain("\"q1\":");
        systemPrompt.Should().Contain("\"q2\":");
        systemPrompt.Should().Contain("\"q3\":");
        systemPrompt.Should().Contain("\"q4\":");
        systemPrompt.Should().Contain("\"q5\":");
        systemPrompt.Should().NotContain("\"q6\":",
            "PIN: today the prompt has exactly five questions. If a fix to " +
            "FC-006 restructures the q-set around the three-axis rule, this " +
            "pin will fail intentionally — that failure means the fix " +
            "landed and the pin needs updating.");
    }

    private static FrontierVerifierRequest SyntheticSharedFactualNovel() => new(
        // Shared / factual / novel — should be flagged Remediate under the
        // reframed rule. The current prompt structurally cannot ask the
        // right question.
        ComposedMessage: "FC006-FIXTURE: we should plan our anniversary-event-Q for next month.",
        MarkAssertedSubstrate:
            "- FC006-FIXTURE: Mark texted: \"Work was rough today.\"\n" +
            "- FC006-FIXTURE: Mark texted: \"I'm thinking about dinner.\"",
        CanonicalSubstrate:
            "- FC006-FIXTURE: Ani is a bookstore clerk in a small synthetic-prop-town-T.",
        CurrentTime:            DateTimeOffset.UtcNow,
        CurrentDayOfWeek:       "Tuesday",
        AddresseeCanonicalName: "Mark",
        KnownContacts:          "Mark");
}
