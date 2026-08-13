using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;

namespace AniRuntime.LLM.Prompts;

/// <summary>Typed input for <see cref="InnerThoughtMetadataPromptCommand"/>.</summary>
/// <remarks>
/// <c>Register</c> was moved from output to input on 2026-08-12 as part of
/// the <see cref="IRegisterClassifier"/> singular-surface refactor. Callers
/// classify register first, then pass it in so this prompt only produces
/// {valence, importance, associative_anchor}.
/// </remarks>
public sealed record InnerThoughtMetadataPromptInput(
    string Thought,
    ContextSnapshot Snapshot,
    string Register);

/// <summary>
/// Posture-S+1 (Issue #38) — metadata-recognizer prompt for the hybrid
/// inner-thought cycle. Given a thought + its context + the already-classified
/// register, produces the remaining recognizer fields: relational valence,
/// importance, and the associative anchor.
///
/// <para>
/// Register classification was extracted to <see cref="IRegisterClassifier"/>
/// on 2026-08-12 (see that interface's XML doc for the N-places-drift
/// diagnostic that motivated the singular-surface refactor).
/// </para>
///
/// <para>
/// Critical framing rule (unchanged): the recognizer's role is to identify
/// the affective shape ALREADY PRESENT in the thought — not to apply an
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
        var register      = string.IsNullOrWhiteSpace(input.Register) ? "Unclassified" : input.Register;
        var cs            = snapshot.CharacterState;
        var characterName = string.IsNullOrWhiteSpace(cs.Name) ? "she" : cs.Name;
        var contactName   = string.IsNullOrWhiteSpace(cs.PrimaryContactName) ? "the caregiver" : cs.PrimaryContactName;

        var system = $$"""
            You are reading an inner thought that {{characterName}} had in a private moment. Her thought has already been classified with a register ({{register}}) — you do NOT re-classify. Your job is to RECOGNIZE the remaining details ALREADY PRESENT in what she said — not to rate it against an external rubric, not to judge it. You are an attentive reader naming what is already there.

            Given {{characterName}}'s thought + the context she had when she thought it + the already-classified register, identify:
              - How relationally-tied the thought is (the relational valence)
              - How much the thought is the kind that stays with her vs fades
              - The single vivid concrete detail (if any) that her next thought would drift toward

            Rules:
              - "valence" is on 0.0-1.0 (0.0 = not relationally tied to {{contactName}}, 1.0 = deeply tied to {{contactName}} / the relationship).
              - "importance" is on 0.0-1.0 (0.0 = a fleeting idle thought, 1.0 = a thought that lingers and shapes her interior over hours).
              - "associative_anchor" is the single vivid concrete detail the thought hinges on, or null if none stands out.
              - Read what is in the thought. Do NOT invent shape that is not there.

            Output valid JSON exactly matching this structure:
            {
              "valence": 0.0,
              "importance": 0.0,
              "associative_anchor": "vivid detail or null"
            }

            No prose outside the JSON object. No markdown fences.
            """;

        var sections = new List<string>
        {
            $"Register (already classified): {register}",
            $"Context {characterName} had when she thought this:",
            $"  Mood: {snapshot.EmotionalState.Describe()}",
        };

        // Feature 44 Phase I.2: interoceptive body-sense as a distinct
        // context line so the recognizer sees the physical substrate
        // alongside the emotional one.
        var body = InteroceptiveDescriptorRenderer.RenderParenthetical(snapshot.EmotionalState);
        if (body.Length > 0)
            sections.Add($"  Body: {body}");

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
        sections.Add("Recognize the remaining details (valence, importance, anchor). Output the JSON object.");

        return new PromptPair(system, string.Join("\n", sections));
    }
}
