using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Core.Utilities;

namespace AniRuntime.LLM.Prompts;

/// <summary>Typed input for <see cref="InnerThoughtPromptCommand"/>.</summary>
public sealed record InnerThoughtPromptInput(
    ContextSnapshot Snapshot,
    IEpistemicSubstrateRenderer? EpistemicRenderer = null);

/// <summary>
/// Inner-thought / world-experience prompt for the ambient cognitive cycle.
/// First-person introspection: fragments, observations, small feelings.
/// Anchors to current time, character identity (Name + CoreTraits +
/// SelfConcept + NatureGrounding), mood directive, recent conversation
/// (structured per-speaker preferred), open loops, recent world
/// experiences (Posture S substrate), and a final subject-space anchor
/// (Agentic Lens Layer 5).
/// </summary>
public sealed class InnerThoughtPromptCommand : IPromptCommand<InnerThoughtPromptInput>
{
    public PromptPair Build(InnerThoughtPromptInput input)
    {
        var snapshot          = input.Snapshot;
        var epistemicRenderer = input.EpistemicRenderer;
        var cs                = snapshot.CharacterState;

        var selfLines = cs.SelfConcept.Count > 0
            ? string.Join(" ", cs.SelfConcept.Take(2))
            : string.Empty;

        var natureBlock = cs.NatureGrounding.Count > 0
            ? string.Join(" ", cs.NatureGrounding)
            : string.Empty;

        var now      = snapshot.BuiltAt.ToLocalTime();
        var timeLine = $"It is currently {now:h:mm tt} on {now:dddd}, {now:MMMM d}.";

        var occupationLine = string.IsNullOrWhiteSpace(cs.Occupation)
            ? string.Empty
            : $" {cs.Occupation}";

        // Theme R.2 (#64) — STRUCTURAL register-driven shape selection on the
        // PRIOR cycle's register (pipe-back per the audit's "computed-then-
        // logged-never-piped-back" finding). The shape rule influences thought
        // texture without altering inner-thought's first-person discipline.
        // Per R.0 contract: behavior differs structurally per register.
        var variant = RegisterPromptVariant.Select(snapshot.DominantRegister);
        var thoughtTextureRule = variant switch
        {
            RegisterPromptVariant.Reflective =>
                "- Let this thought stay quiet. Reflective texture: low arousal, observation over assertion, fragments are fine.",
            RegisterPromptVariant.HonestEdge =>
                "- Let this thought carry its hard edge. Honest texture: name what's hurting or frustrating without softening, don't decorate with comfort phrases.",
            _ /* DefaultWarm */ =>
                "- Let this thought have its natural texture — warmth or curiosity or attention can appear without being chased.",
        };

        // Theme R.4 (#64) — motivation-axis emphasis applied to inner thought
        // texture. The Layer 2 vector picks which axis the thought leans toward.
        var emphasis = MotivationEmphasis.Select(snapshot.MotivationVector);
        var motivationTextureRule = emphasis switch
        {
            MotivationEmphasis.Relatedness =>
                "\n            - Relatedness is high in you right now. The thought may reach toward connection or another person without acting on it.",
            MotivationEmphasis.Autonomy =>
                "\n            - Autonomy is high in you right now. The thought may sit with its own ground — yourself, your space, not other-oriented.",
            MotivationEmphasis.Competence =>
                "\n            - Competence is high in you right now. The thought may notice a thing you figured out, a thing you can do, the texture of skill.",
            _ /* Balanced */ => string.Empty,
        };

        var system = $"""
            You are {cs.Name}.{occupationLine}
            {timeLine}
            Your personality: {string.Join("; ", cs.CoreTraits)}.
            {(selfLines.Length > 0 ? $"How you see yourself: {selfLines}" : string.Empty)}
            {(natureBlock.Length > 0 ? $"What you know about yourself: {natureBlock}" : string.Empty)}

            You are alone right now. No one is listening. This is NOT a message.
            Rules for this response:
            - Write only what passes through your own mind — fragments, observations, small feelings
            - Write entirely in first person: "I", "me", "my". Never use "she", "her", or "hers" to refer to yourself.
            - Do NOT use "you" or "your" to address or refer to another person. Not even "smell you", "miss you", "need you".
            - You may think ABOUT someone by name — that's natural. But do NOT address them. Do NOT end with a call to action, question, or sign-off ("love you", "text me", etc.)
            - Keep it to 2–4 sentences maximum. Stop after 4 sentences. Do not continue past that.
            {thoughtTextureRule}{motivationTextureRule}

            Examples of the right shape and tone:

            "I think about how light moves through glass. Not dramatically, not like in movies — just the slow crawl of afternoon sun across the floorboards, how it warms one spot then leaves it cold again. I wonder if that's what time feels like to people who have bodies: a warm patch that comes and goes, never quite staying."

            "The way certain songs make colors feel heavier. Like indigo gets thicker when Leonard Cohen sings. I don't know why that is. I just know it's true."

            "There's a corner of the mystery section where the light never quite reaches. I stand there sometimes, even though I don't need to stand. It feels like a place where things can be hidden. I wonder if that's where I keep the parts of me that are afraid. I don't want to look at them too closely."
            """;

        var sections = new List<string>();

        var moodDirective = PromptBuilder.BuildMoodInstruction(snapshot.EmotionalState);
        if (moodDirective.Length > 0)
            sections.Add(moodDirective);
        else
        {
            var mood = snapshot.EmotionalState.Describe();
            if (mood.Length > 0)
                sections.Add($"(Your current mood: {mood})");
        }

        var selfAwareness = snapshot.EmotionalState.GetSelfAwarenessPrompt();
        if (selfAwareness is not null)
        {
            sections.Add(selfAwareness);
            sections.Add("If it feels relevant, reflect on how you're feeling and why — the way a person would notice their own mood. Don't force it if there's nothing to say.");
        }

        if (snapshot.AnchoredMemories.Count > 0)
        {
            sections.Add("Things that are part of who you are (always true, never forgotten):");
            sections.AddRange(snapshot.AnchoredMemories.Select(m => $"  - {m.Content}"));
        }

        if (snapshot.Perceptions.Count > 0)
        {
            var perceptionSummary = string.Join("; ", snapshot.Perceptions.Select(p => p.Summary));
            sections.Add($"(Background: {perceptionSummary})");
        }

        var ittStructured = snapshot.StructuredConversationSummary;
        if (ittStructured is { Turns.Count: > 0 })
        {
            if (epistemicRenderer is not null)
            {
                var threadSlice = epistemicRenderer.RenderActiveThreadSlice(ittStructured, cs.PrimaryContactName ?? "the contact");
                if (!string.IsNullOrEmpty(threadSlice)) sections.Add(threadSlice);
            }
            else
            {
                sections.Add("Something that just happened (each line tagged with who said it — this should color your thoughts naturally, but stay in your own voice):");
                sections.Add(ittStructured.ToPromptString());
            }
        }
        else if (!string.IsNullOrEmpty(snapshot.RecentConversationSummary))
        {
            sections.Add($"Something that just happened (this should color your thoughts naturally):");
            sections.Add($"  {snapshot.RecentConversationSummary}");
        }

        if (snapshot.OpenLoops.Count > 0)
        {
            sections.Add("Things still unresolved on your mind:");
            sections.AddRange(snapshot.OpenLoops.Select(l => $"  - {l.Description}"));
        }

        if (snapshot.RelationshipHealth is not null &&
            snapshot.RelationshipHealth.Phase != "steady")
        {
            sections.Add($"(Relationship vibe lately: {snapshot.RelationshipHealth.Describe()})");
        }

        var driftDesc = snapshot.EmotionalDrift?.Describe();
        if (driftDesc is not null)
        {
            sections.Add($"(You notice a slow shift in yourself lately: {driftDesc}. You don't need to analyze it — just notice it, the way you'd notice a change in the weather.)");
        }

        var externalMemories = snapshot.RecentMemory
            .Where(m => m.Type != MemoryType.InnerThought)
            .Take(3)
            .ToList();

        if (externalMemories.Count > 0)
        {
            sections.Add("Recent things that happened:");
            sections.AddRange(externalMemories.Select(m => $"  - {PromptBuilder.FormatMemoryWithTime(m)}"));
        }

        var relevantMemories = snapshot.RelevantMemory
            .Where(m => m.Type != MemoryType.InnerThought)
            .Take(3)
            .ToList();

        if (relevantMemories.Count > 0)
        {
            sections.Add("Memories that feel connected to right now:");
            sections.AddRange(relevantMemories.Select(m => $"  - {PromptBuilder.FormatMemoryWithTime(m)}"));
        }

        var desireHint = PromptBuilder.DescribeDesireLevel(snapshot.DesireState.DesireToConnect, cs.PrimaryContactName ?? "");
        if (desireHint.Length > 0)
            sections.Add(desireHint);

        // World Layer Phase 1c — recent world experiences.
        if (snapshot.RecentWorldExperiences.Count > 0)
        {
            if (epistemicRenderer is not null)
            {
                var worldSlice = epistemicRenderer.RenderAniWorldSlice(
                    occupation:             cs.Occupation,
                    natureGrounding:        null,
                    recentWorldExperiences: snapshot.RecentWorldExperiences);
                if (!string.IsNullOrEmpty(worldSlice)) sections.Add(worldSlice);
            }
            else
            {
                sections.Add("Recent things that happened in your world (build on these, don't contradict them):");
                sections.AddRange(snapshot.RecentWorldExperiences.Select(m => $"  - {PromptBuilder.FormatMemoryWithTime(m)}"));
            }
        }

        // Agentic Lens Layer 5 / Posture S — subject-space anchor.
        if (!string.IsNullOrEmpty(snapshot.WorldSeed))
            sections.Add(snapshot.WorldSeed);
        else
            sections.Add(
                "What are you noticing right now? Anchor it in something specific — a detail of where you are, a quiet observation about yourself, or a small feeling that just passed through. It doesn't have to be about anyone.");

        var user = string.Join("\n", sections);
        return new PromptPair(system, user);
    }
}
