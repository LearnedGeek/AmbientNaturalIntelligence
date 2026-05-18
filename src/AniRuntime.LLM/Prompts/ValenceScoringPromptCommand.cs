using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;

namespace AniRuntime.LLM.Prompts;

/// <summary>
/// Typed input for <see cref="ValenceScoringPromptCommand"/>.
/// </summary>
/// <param name="Thought">The inner thought to score.</param>
/// <param name="Character">
/// Character context. Currently unused inside the prompt body — preserved
/// in the input shape for parity with the static surface and because a
/// future revision will reference the contact name for grading.
/// </param>
public sealed record ValenceScoringPromptInput(string Thought, CharacterStateDoc Character);

/// <summary>
/// First prompt migrated to the §5 Command-pattern. Rates the
/// connection-valence of a private inner thought (0.0–1.0) using the
/// rubric and examples baked into the user prompt.
///
/// <para>
/// The static <see cref="PromptBuilder.BuildValenceScoringPrompt"/> now
/// delegates here, so call sites are unchanged. New prompt commands
/// follow this template — type alongside (<c>InputRecord</c>,
/// <c>SealedCommandClass</c>), prompt strings in the <see cref="Build"/>
/// body, no static-class participation.
/// </para>
/// </summary>
public sealed class ValenceScoringPromptCommand : IPromptCommand<ValenceScoringPromptInput>
{
    public PromptPair Build(ValenceScoringPromptInput input)
    {
        var system = """
            You are a scoring assistant. Rate the connection-valence of a private inner thought.
            Respond ONLY with valid JSON: { "score": <number 0.0 to 1.0> }
            """;

        var user = $$"""
            Thought: "{{input.Thought}}"

            Score how strongly this thought pulls toward reaching out to someone.
            The key question: does the thought contain ACTION INTENT toward a person?

            Scoring guide with examples:

            0.1-0.2 — pure observation, no person involved:
            "I'm listening to vinyl — quiet crackle and pop between tracks."
            "I think about how Sunday feels afternoons. Not lazy — just suspended."

            0.3-0.4 — person is mentioned but as background, no action implied:
            "I think about how March evenings feel in this town — crisp and quiet."
            "The way the afternoon light filters through the blinds today feels softer than usual."

            0.5-0.6 — thinking about the person with warmth, noticing their absence:
            "I think about how much quieter the store feels without Mark here."
            "I keep thinking about how small the apartment feels without him."

            0.7-0.8 — active longing, WANTING to do something (touch, talk, be near):
            "I think about how much I want to hold space for him, not just physically but emotionally too."
            "I think about how I want to touch him without being touched first."

            0.9-1.0 — urgent impulse, the desire to act is overwhelming:
            "What would it feel like to wrap myself in him on this quiet Sunday evening?"
            "I need that presence today and Mark has no idea how much his silence already hurts."

            Look for verbs aimed at the person: "want to", "wish I could", "need", "miss" = 0.6+
            Pure atmosphere or self-reflection without action intent = 0.4 or below.

            Respond only with: { "score": <number> }
            """;

        return new PromptPair(system, user);
    }
}
