using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;

namespace AniRuntime.LLM.Prompts;

/// <summary>Typed input for <see cref="InnerThoughtMetadataPromptCommand"/>.</summary>
public sealed record InnerThoughtMetadataPromptInput(string Thought, ContextSnapshot Snapshot);

/// <summary>
/// Posture-S+1 (Issue #38) — metadata-recognizer prompt for the hybrid
/// inner-thought cycle. After <c>ani-v7-inner</c> emits the thought, this
/// prompt is sent to a metadata-recognizer model with <c>format=json</c>
/// to extract register / relational valence / importance / associative
/// anchor.
///
/// <para>
/// Critical framing rule: the recognizer's role is to identify the
/// affective shape ALREADY PRESENT in the thought — not to apply an
/// external rubric. Preserves the "feeling comes from her, not from an
/// outside judge" framing.
/// </para>
/// </summary>
public sealed class InnerThoughtMetadataPromptCommand : IPromptCommand<InnerThoughtMetadataPromptInput>
{
    public PromptPair Build(InnerThoughtMetadataPromptInput input)
    {
        var snapshot      = input.Snapshot;
        var thought       = input.Thought;
        var cs            = snapshot.CharacterState;
        var characterName = string.IsNullOrWhiteSpace(cs.Name) ? "she" : cs.Name;
        var contactName   = string.IsNullOrWhiteSpace(cs.PrimaryContactName) ? "the caregiver" : cs.PrimaryContactName;

        var system = $$"""
            You are reading an inner thought that {{characterName}} had in a private moment. Your job is to RECOGNIZE the affective shape and details ALREADY PRESENT in what she said — not to rate it against an external rubric, not to judge it. You are an attentive reader naming what is already there.

            Given {{characterName}}'s thought + the context she had when she thought it, identify:
              - The register family the thought expresses
              - How relationally-tied the thought is (the relational valence)
              - How much the thought is the kind that stays with her vs fades
              - The single vivid concrete detail (if any) that her next thought would drift toward

            Rules:
              - "register" is ONE of: Warmth, Longing, Curiosity, Playfulness, Delight, Tenderness, Concern, Hurt, Existential, Resilience.
              - "valence" is on 0.0-1.0 (0.0 = not relationally tied to {{contactName}}, 1.0 = deeply tied to {{contactName}} / the relationship).
              - "importance" is on 0.0-1.0 (0.0 = a fleeting idle thought, 1.0 = a thought that lingers and shapes her interior over hours).
              - "associative_anchor" is the single vivid concrete detail the thought hinges on, or null if none stands out.
              - Read what is in the thought. Do NOT invent shape that is not there.

            Output valid JSON exactly matching this structure:
            {
              "register": "one of the 10 register families",
              "valence": 0.0,
              "importance": 0.0,
              "associative_anchor": "vivid detail or null"
            }

            No prose outside the JSON object. No markdown fences.
            """;

        var sections = new List<string>
        {
            $"Context {characterName} had when she thought this:",
            $"  Mood: {snapshot.EmotionalState.Describe()}",
        };

        if (!string.IsNullOrEmpty(snapshot.WorldSeed))
            sections.Add($"  Lingering: {snapshot.WorldSeed}");

        if (snapshot.RecentMemory.Count > 0)
        {
            sections.Add("  Recent memory:");
            sections.AddRange(snapshot.RecentMemory.Take(5).Select(m => $"    - {m.Content}"));
        }

        sections.Add("");
        sections.Add($"{characterName}'s thought:");
        sections.Add($"  {thought}");
        sections.Add("");
        sections.Add("Recognize the affective shape already present. Output the JSON object.");

        return new PromptPair(system, string.Join("\n", sections));
    }
}
