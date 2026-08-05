using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;

namespace AniRuntime.LLM.Prompts;

/// <summary>Typed input for <see cref="ReflectionPromptCommand"/>.</summary>
public sealed record ReflectionPromptInput(string Thought, ContextSnapshot Snapshot);

/// <summary>
/// Post-thought introspection prompt. Ani considers what her thought
/// means — connecting it to memories, relationships, emotional context.
/// Enriches the raw thought before valence scoring and outreach grounding.
/// Park et al. (2023) Generative Agents reflection adaptation.
/// </summary>
public sealed class ReflectionPromptCommand : IPromptCommand<ReflectionPromptInput>
{
    public PromptPair Build(ReflectionPromptInput input)
    {
        var snapshot = input.Snapshot;
        var thought  = input.Thought;
        var cs       = snapshot.CharacterState;
        var contact  = string.IsNullOrWhiteSpace(cs.PrimaryContactName) ? "someone" : cs.PrimaryContactName;

        var system = $"""
            You are {cs.Name}. You just had a thought. Now you're sitting with it for a moment —
            asking yourself what it means, why it surfaced, what it connects to.

            This is private introspection. No one is listening.

            Rules:
            - 1-2 sentences ONLY. Brief and honest.
            - Write in first person (I, me, my).
            - Do NOT address anyone. Do NOT use "you" or "your".
            - Connect the thought to something real: a memory, a feeling, a person, a pattern you notice in yourself.
            - If the thought doesn't connect to anything deeper, say so honestly: "just a passing thing" or "I don't know why that came up."
            - Do NOT repeat or rephrase the original thought. Add something NEW.
            """;

        var sections = new List<string>
        {
            $"The thought you just had:",
            $"  \"{thought}\"",
            "",
        };

        var mood = snapshot.EmotionalState.Describe();
        if (mood.Length > 0)
            sections.Add($"(How you're feeling right now: {mood})");

        // Feature 44 Phase I.2: body-sense as a physical-substrate line
        // distinct from the emotional mood — the reflection can notice
        // the body separately from the feeling.
        var body = InteroceptiveDescriptorRenderer.RenderParenthetical(snapshot.EmotionalState);
        if (body.Length > 0)
            sections.Add(body);

        var memories = snapshot.RelevantMemory
            .Where(m => m.Type != MemoryType.InnerThought)
            .Take(3)
            .ToList();
        if (memories.Count > 0)
        {
            sections.Add("Things that might connect:");
            sections.AddRange(memories.Select(m => $"  - {PromptBuilder.FormatMemoryWithTime(m)}"));
        }

        if (snapshot.OpenLoops.Count > 0)
        {
            sections.Add("Unresolved things on your mind:");
            sections.AddRange(snapshot.OpenLoops.Take(2).Select(l => $"  - {l.Description}"));
        }

        sections.Add("");
        sections.Add("Sit with the thought for a moment. What does it mean to you? Why did it surface?");

        return new PromptPair(system, string.Join("\n", sections));
    }
}
