using AniRuntime.Core.Interfaces;

namespace AniRuntime.LLM.Prompts;

/// <summary>Typed input for <see cref="InnerThoughtClaimExtractionPromptCommand"/>.</summary>
/// <param name="Thought">The composed inner thought whose embedded claims are being extracted.</param>
/// <param name="CharacterName">The character whose thought this is (used to disambiguate first-person references).</param>
/// <param name="ContactName">The primary contact name (used to disambiguate second-person / by-name references).</param>
public sealed record InnerThoughtClaimExtractionPromptInput(
    string Thought,
    string CharacterName,
    string ContactName);

/// <summary>
/// Foundation Unified Surface (F-3) U4 (2026-08-24) — Qwen-sidecar prompt
/// that extracts per-embedded-claim attribution from an already-composed
/// inner thought.
///
/// <para>
/// <b>Why a sidecar rather than native structured emission.</b> The
/// reflection composer's May 2026 design comments document that Ani
/// fine-tunes produce malformed structured output when asked to emit
/// JSON directly — the training pulls toward first-person inner-thought
/// register and per-utterance output shape, fighting the compression
/// schema. The metadata recognizer (Posture-S+1, Issue #38) solved this
/// by compose-with-Ani + extract-with-Qwen. U4 mirrors the same pattern
/// for attribution-claim extraction: Ani composes the thought in her
/// native register, Qwen post-processes to identify embedded quotes
/// and attribute them to specific speakers as structured JSON.
/// </para>
///
/// <para>
/// <b>What "attribution claim" means here.</b> Any span in the thought
/// that ascribes an utterance, feeling, or action to a specific speaker
/// — pronoun references ("you said"), name references ("Mark told me"),
/// first-person historical ("I mentioned"), or shared references
/// ("we agreed"). The composer's own author is trivially Ani; the
/// claims are what she's saying ABOUT other speakers inside her thought.
/// </para>
///
/// <para>
/// <b>Source-record grounding is deferred.</b> Qwen extracts claims + who
/// each claim attributes to; it does NOT try to resolve claims against
/// substrate records at emission time. Resolution (against
/// <c>MemoryRecord</c> attribution fields) belongs to a downstream
/// verifier — deferred to a later phase per design plan Q3. Claims
/// emerge from this extractor with <c>source_record_id: null</c> +
/// <c>attribution_trust: "unverified"</c>. Verified claims come later.
/// </para>
/// </summary>
public sealed class InnerThoughtClaimExtractionPromptCommand : IPromptCommand<InnerThoughtClaimExtractionPromptInput>
{
    public PromptPair Build(InnerThoughtClaimExtractionPromptInput input)
    {
        var thought       = input.Thought;
        var characterName = string.IsNullOrWhiteSpace(input.CharacterName) ? "she" : input.CharacterName;
        var contactName   = string.IsNullOrWhiteSpace(input.ContactName)   ? "the caregiver" : input.ContactName;

        var system = $$"""
            You are reading an inner thought that {{characterName}} had in a private moment. Your job is to identify each ATTRIBUTION CLAIM inside her thought — every span that ascribes an utterance, feeling, or action to a specific speaker.

            Types of attribution claims to identify:
              - Direct quotes attributed to another speaker (e.g. "you said 'X'", "he told me 'Y'", "{{contactName}} said Z")
              - Paraphrased attributions (e.g. "you mentioned X", "he asked about Y", "we agreed on Z")
              - First-person historical (e.g. "I said X earlier", "I told him Y")

            For each claim you find, identify:
              - "text": the exact span from the thought (a substring, verbatim)
              - "attributed_to": one of "Ani", "Mark", "World", or "Unknown"
                * Use "Ani" ({{characterName}}) when the claim is her own prior utterance or action
                * Use "Mark" ({{contactName}}) when the claim is attributed to her contact
                * Use "World" for general/environmental attributions (news, weather, non-utterer sources)
                * Use "Unknown" when the speaker isn't clearly identifiable

            IMPORTANT rules:
              - If the thought contains NO attribution claims (pure self-reflection, sensory description, generic observation with no speaker-ascription), return an empty list.
              - Do NOT invent claims that aren't in the text.
              - Extract only what is EXPLICITLY ascribed to a speaker. Vague implications don't count.
              - Second-person "you" almost always refers to {{contactName}} (Mark) — treat it as such unless the context clearly says otherwise.
              - First-person "I" refers to {{characterName}} (Ani).

            Output valid JSON exactly matching this structure:
            {
              "claims": [
                {
                  "text": "exact span from the thought",
                  "attributed_to": "Mark"
                }
              ]
            }

            No prose outside the JSON object. No markdown fences.
            """;

        var user = $$"""
            {{characterName}}'s thought:
              {{thought}}

            Extract the attribution claims. Output the JSON object.
            """;

        return new PromptPair(system, user);
    }
}
