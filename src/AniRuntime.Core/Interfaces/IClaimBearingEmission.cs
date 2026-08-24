using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Foundation Unified Surface (F-3) U1 (2026-08-24) — extension of
/// <see cref="IComposerEmission{T}"/> for composers that emit structured
/// output including per-claim attribution.
///
/// <para>
/// <b>Why this exists.</b> The base <see cref="IComposerEmission{T}"/>
/// envelope carries who WROTE the content (record-author attribution) —
/// trivially Ani for every current composer. It does NOT track per-claim
/// attribution WITHIN the content. A composer can emit prose that quotes
/// or paraphrases something ("you said X", "Mark told me Y") and the
/// base envelope has no field for who the composer is attributing that
/// embedded claim to. The 12:04 misattribution shape lives exactly here:
/// the record's own author is Ani (correct), but the embedded quote
/// claim attributes to Mark (fabricated), and nothing in the current
/// surface distinguishes the two levels.
/// </para>
///
/// <para>
/// <b>Structured emission is the fix.</b> For composers that produce
/// claim-bearing prose — inner-thought and reflection specifically —
/// the composer's LLM call returns structured JSON of shape
/// <c>{content, claims: [{text, attributed_to, source_record_id,
/// attribution_trust}]}</c>. The composer parses that into an
/// <see cref="IClaimBearingEmission{T}"/> where <see cref="Claims"/>
/// carries the structured list. Downstream verification becomes trivial:
/// each claim's <c>SourceRecordId</c> either resolves to a substrate
/// record whose author matches the claim's <c>AttributedTo</c>, or it
/// doesn't (fabrication detected structurally, no free-text extractor
/// needed).
/// </para>
///
/// <para>
/// <b>Scoped to internal composers.</b> User-facing composers
/// (conversation reply, outreach composition, voice reply, reactive
/// share) keep prose emission because structured wrapping would leak
/// into the delivered SMS/voice text. See the F-3 plan doc's Option B
/// scope decision.
/// </para>
///
/// <para>
/// <b>Empty <see cref="Claims"/> is valid.</b> A composer that emits
/// self-content only (no embedded quoting of other speakers) declares
/// an empty claims list. The interface is opt-in per composer: composers
/// that don't emit claims implement only the base
/// <see cref="IComposerEmission{T}"/> and don't need to expose the
/// <see cref="Claims"/> accessor at all.
/// </para>
/// </summary>
/// <typeparam name="T">The wrapped payload type — same as
/// <see cref="IComposerEmission{T}"/>.</typeparam>
public interface IClaimBearingEmission<out T> : IComposerEmission<T>
{
    /// <summary>
    /// Per-claim attribution list. Each entry declares an embedded claim
    /// in <see cref="IComposerEmission{T}.Content"/> alongside who the
    /// composer attributes it to and (optionally) the substrate record
    /// grounding the attribution.
    /// </summary>
    IReadOnlyList<ContentClaim> Claims { get; }
}

/// <summary>
/// Per-claim attribution record used by <see cref="IClaimBearingEmission{T}"/>.
/// One entry declares one embedded claim within the composer's output.
/// </summary>
/// <param name="Text">
/// The exact quoted-or-paraphrased span inside the emission's content
/// that this claim covers. Should be a substring or clear paraphrase of
/// <see cref="IComposerEmission{T}.Content"/>. Verification convention
/// (design-phase Q2) — trust the composer to declare accurately for U4;
/// add substring/fuzzy-match verification later if empirical evidence
/// shows drift.
/// </param>
/// <param name="AttributedTo">
/// Who the composer attributes this specific embedded claim to. May
/// differ from the enveloping <see cref="IComposerEmission{T}.AttributedTo"/>
/// — e.g., an Ani-authored inner thought whose embedded claim quotes
/// something Mark said would carry
/// <see cref="IComposerEmission{T}.AttributedTo"/> = Ani and this
/// <see cref="AttributedTo"/> = Mark.
/// </param>
/// <param name="SourceRecordId">
/// Optional pointer to the substrate <c>MemoryRecord</c> that grounds
/// this claim. Null when the composer can't or doesn't link (e.g.,
/// paraphrasing a general shared context rather than quoting a specific
/// past turn). Design-phase Q3 — resolution timing (composer-emission
/// vs persist-time) deferred; recommended default is verify at persist
/// time on a background thread.
/// </param>
/// <param name="AttributionTrust">
/// Trust marker for this specific claim. <c>"verified"</c> when
/// <see cref="SourceRecordId"/> resolves to a real substrate record
/// whose author matches <see cref="AttributedTo"/>. <c>"unverified"</c>
/// otherwise (ungrounded claim — potentially fabricated).
/// </param>
public sealed record ContentClaim(
    string        Text,
    AttributedTo  AttributedTo,
    Guid?         SourceRecordId,
    string        AttributionTrust);
