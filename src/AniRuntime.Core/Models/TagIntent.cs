namespace AniRuntime.Core.Models;

/// <summary>
/// Issue #93 Phase 2 (2026-07-06) — Mark's <c>///tag</c> intent as classified
/// by <see cref="Interfaces.ITagIntentClassifier"/>. Replaces the regex
/// keyword sniff at <c>TagCommand.cs:109</c> that only matched
/// <c>confab / fabricat / walk back / made up / hallucination</c> and inert
/// against Mark's actual tag vocabulary ("broken output" / "weather
/// confusion" / "json response?").
///
/// Both directions of the substrate-correction loop route through this enum:
/// negative (invalidate) — walk back a confabulated reply, sets Validity;
/// positive (confirm) — Mark endorses the reply, sets ConfirmedAt so the
/// referenced Interior record joins the retrieval-biased pool.
/// </summary>
public enum TagIntent
{
    /// <summary>
    /// Classifier failed / timed out / returned unparseable output. Caller
    /// treats as fail-open: preserve the flag (audit) but make no substrate
    /// change. Matches the routing-classifier / verifier fail-open contract.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Mark confirms the Ani reply's content is correct. The matching
    /// Episodic conversation record (and, if surfaced from Interior, the
    /// Interior source record) receives <c>ConfirmedAt</c> = tag time,
    /// <c>ConfirmedBy</c> = <c>"mark-tag"</c>. Retrieval bias picks it up
    /// on subsequent semantic search.
    /// </summary>
    Confirm = 1,

    /// <summary>
    /// Mark calls out the Ani reply as fabricated / wrong / confused. The
    /// matching Episodic conversation record's <c>Validity</c> is set to
    /// <c>"invalid_confabulation"</c> so retrieval stops laundering it.
    /// Same shape as the pre-Issue-93 regex behavior — preserved verbatim
    /// through the classifier so the negative half of the loop is
    /// unchanged from Mark's perspective.
    /// </summary>
    Invalidate = 2,

    /// <summary>
    /// Mark's tag is a clarification question or ambiguous reaction
    /// ("why?", "hmm", "interesting") — not a verdict on truth. Flag is
    /// saved for audit but no substrate mutation happens.
    /// </summary>
    Clarify = 3,

    /// <summary>
    /// Mark's tag content is unrelated to the Ani reply (a note-to-self,
    /// off-topic aside, or accidental tag). Flag is saved for audit but
    /// no substrate mutation happens. Distinct from
    /// <see cref="Clarify"/>: the tag isn't ABOUT the reply at all.
    /// </summary>
    Unrelated = 4,
}

/// <summary>
/// Structured verdict returned by <see cref="Interfaces.ITagIntentClassifier"/>.
/// Shape mirrors the frontier-verifier response (JSON-parseable, confidence
/// scalar, human-readable reason).
/// </summary>
/// <param name="Intent">Classified intent bucket.</param>
/// <param name="Confidence">Model-reported confidence in [0.0, 1.0]. Low
/// confidence should route to <see cref="TagIntent.Clarify"/> at the caller
/// boundary rather than committing a substrate mutation on shaky evidence.</param>
/// <param name="Reason">One-line justification the classifier emitted, kept
/// for audit + eval-harness inspection.</param>
public sealed record TagIntentVerdict(
    TagIntent Intent,
    float     Confidence,
    string?   Reason);
