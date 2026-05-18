using AniRuntime.Core.Interfaces;

namespace AniRuntime.LLM.Prompts;

/// <summary>Typed input for <see cref="ClaimExtractionPromptCommand"/>.</summary>
public sealed record ClaimExtractionPromptInput(string ComposedMessage, string ContactName);

/// <summary>
/// Feature 14 v2 (Apr 22, 2026) — claim extraction. Asks the LLM to
/// enumerate factual claims a composed outreach message makes about the
/// contact, classified into six shapes (mark-action / mark-decision /
/// shared-event / shared-event-with-attribution / shared-decision /
/// shared-presence) with key terms + event actor for downstream
/// verification. The extractor only identifies; the architecture decides.
/// </summary>
public sealed class ClaimExtractionPromptCommand : IPromptCommand<ClaimExtractionPromptInput>
{
    public PromptPair Build(ClaimExtractionPromptInput input)
    {
        var contactName = input.ContactName;
        var system = $$"""
            You extract claims about {{contactName}} from a text message.

            A "claim" is a statement that asserts something factual about {{contactName}}'s
            actions, decisions, or shared events with the writer — something that could
            be verified by checking what {{contactName}} has actually said or done.

            Claim categories:
              mark-action                   — The writer says {{contactName}} did something specific
              mark-decision                 — The writer says {{contactName}} decided something
              shared-event                  — The writer describes an event involving both of them
              shared-event-with-attribution — A more specific sub-shape of shared-event:
                                              the writer asserts a SPECIFIC event with a
                                              SPECIFIC actor — "X told us Y", "X said Z",
                                              "we did W with X", or "X picked Y". Use this
                                              type whenever the claim names an actor
                                              (a person, including {{contactName}} or
                                              canonical characters by name) performing a
                                              specific action or making a specific
                                              utterance. Do NOT use for general state
                                              observations ("you seemed tired") or
                                              temporal claims ("yesterday morning").
              shared-decision               — The writer describes a decision made together
              shared-presence               — The writer describes being physically or relationally together

            When in doubt between shared-event and shared-event-with-attribution,
            prefer shared-event-with-attribution if a specific actor is named
            performing a specific action (verb + object). The downstream verifier
            applies stricter checks for this type.

            NOT claims (ignore these):
              - The writer's own world or daily life ("shelving books", "at the bookstore")
              - The writer's feelings, thoughts, or wishes ("thinking about you", "wish you were here")
              - Descriptions of the message itself ("wanted to say", "just checking in")
              - Questions asked of {{contactName}} ("how was your day?")

            Output ONLY JSON matching this schema, no commentary:
            {
              "claims": [
                { "text": "...", "type": "...", "key_terms": ["...", "..."], "event_actor": "..." }
              ]
            }

            The "text" field should be the phrase from the message. The "type" field
            must be one of the six categories above. The "key_terms" field should
            list the specific entities or actions that would need to appear in a
            corroborating source (e.g. for "you brought them over" → ["brought",
            "flowers"]; for "we decided on purple" → ["decided", "purple"]).

            The "event_actor" field is REQUIRED for shared-event-with-attribution
            claims and should name the actor performing the asserted action
            (e.g. for "Mia told us she picked the tickets" → "Mia"; for
            "we decided on purple" → "we"). Omit or leave empty for other claim
            types.

            If no claims appear, return { "claims": [] }. Do not fabricate claims to
            fill the array. Do not mark your own uncertainty in the output — the
            downstream system performs verification.
            """;

        var user = $$"""
            Message: "{{input.ComposedMessage}}"

            Extract claims. JSON only.
            """;

        return new PromptPair(system, user);
    }
}
