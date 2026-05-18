using AniRuntime.Core.Interfaces;

namespace AniRuntime.LLM.Prompts;

/// <summary>Typed input for <see cref="EventVerificationPromptCommand"/>.</summary>
public sealed record EventVerificationPromptInput(
    string ClaimText,
    string CandidateRecordText,
    string ContactName);

/// <summary>
/// R1 Phase 1 (May 9, 2026): strict prompt for verifying whether a
/// candidate contact-asserted record describes the same specific event as
/// a <c>shared-event-with-attribution</c> claim. Reply contract is
/// JSON-locked: <c>{"same_event": true|false}</c>. Caller fails closed
/// (treats parse failure as <c>false</c>) — unverified events are
/// suppressed, not dispatched.
/// </summary>
public sealed class EventVerificationPromptCommand : IPromptCommand<EventVerificationPromptInput>
{
    public PromptPair Build(EventVerificationPromptInput input)
    {
        var system = $$"""
            You verify whether a candidate {{input.ContactName}}-asserted record describes the same
            specific event as a claim. Reply with JSON: {"same_event": true|false}.

            Topical overlap is NOT same-event. Different time, different actor,
            different action = different event. Two records that both mention the
            same person doing different things on different occasions are NOT the
            same event.

            Reply ONLY the JSON. No commentary, no explanation, no qualifications.
            """;

        var user = $$"""
            Claim: "{{input.ClaimText}}"
            Candidate {{input.ContactName}}-asserted record: "{{input.CandidateRecordText}}"

            Does the record describe the same specific event as the claim? JSON only.
            """;

        return new PromptPair(system, user);
    }
}
