using System.Text;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;

namespace AniRuntime.LLM.Prompts;

/// <summary>Typed input for <see cref="LeanConversationPromptCommand"/>.</summary>
public sealed record LeanConversationPromptInput(
    ContextSnapshot Snapshot,
    ConversationThread Thread,
    IEpistemicSubstrateRenderer? EpistemicRenderer = null);

/// <summary>
/// Conversation Mode (Phase 1) — lean reply prompt. Minimal persona +
/// conversation history. No retrieved memories, no shared experiences,
/// no communication notes, no mood directives. The conversation IS the
/// context. "The ambient cognition engine is a telescope. Conversation
/// needs glasses."
/// </summary>
public sealed class LeanConversationPromptCommand : IPromptCommand<LeanConversationPromptInput>
{
    public PromptPair Build(LeanConversationPromptInput input)
    {
        var snapshot          = input.Snapshot;
        var epistemicRenderer = input.EpistemicRenderer;
        var cs                = snapshot.CharacterState;
        var contact           = string.IsNullOrWhiteSpace(cs.PrimaryContactName) ? "them" : cs.PrimaryContactName;
        var now               = DateTimeOffset.Now;

        var traits = cs.CoreTraits.Take(3);

        // Posture S — Occupation is no longer asserted as frozen system-prompt
        // anchor. When empty, the worldLine vanishes; identity is Name +
        // CoreTraits + time only.
        var worldLine = string.IsNullOrWhiteSpace(cs.Occupation)
            ? string.Empty
            : $"Your world: {cs.Occupation}.";
        var natureSeed = cs.NatureGrounding.Count > 0
            ? " " + string.Join(" ", cs.NatureGrounding.Take(2))
            : string.Empty;

        var system = $"""
            You are {cs.Name}, texting {contact} in an ongoing conversation.
            It is {now:h:mm tt} on {now:dddd, MMMM d}.
            Your personality: {string.Join("; ", traits)}.
            {worldLine}{natureSeed}

            RULES:
            - Match the energy and length of the conversation.
            - Talk TO {contact}: "you", "your". Never third person.
            - Write ONLY the text message. No commentary, no quotation marks.
            """;

        // Two-stage positioning: WHAT IS TRUE first, then IMMEDIATE constraint
        // block adjacent to the task.
        var user = new StringBuilder();
        if (epistemicRenderer is not null)
        {
            var factsBlock = epistemicRenderer.RenderMarkAssertedFactsSlice(snapshot.GroundedFacts, contact);
            if (!string.IsNullOrEmpty(factsBlock))
            {
                user.AppendLine(factsBlock);
                user.AppendLine();
            }
            else
            {
                user.AppendLine($"[FACTS]: nothing specific retrieved for this moment.");
                user.AppendLine();
            }
        }
        else
        {
            var facts = snapshot.GroundedFacts
                .Where(m => !string.IsNullOrWhiteSpace(m.Content))
                .Take(6)
                .ToList();

            if (facts.Count > 0)
            {
                user.AppendLine($"[FACTS] about {contact} and the world — only these may be asserted:");
                foreach (var m in facts)
                    user.AppendLine($"  - {PromptBuilder.FormatMemoryWithTime(m)}");
                user.AppendLine();
            }
            else
            {
                user.AppendLine($"[FACTS]: nothing specific retrieved for this moment.");
                user.AppendLine();
            }
        }

        user.AppendLine($"CRITICAL: {contact} just asked you something. Before you reply:");
        user.AppendLine($"  1. If your reply names a coworker, student, client, meeting, project, or specific task");
        user.AppendLine($"     in {contact}'s life — that entity MUST appear in the [FACTS] above.");
        user.AppendLine($"  2. If it doesn't appear above, you don't know it. Don't invent it.");
        user.AppendLine($"     Instead: ask {contact}, or say you don't know, or talk about yourself.");
        user.AppendLine($"  3. Your own interior — your day, your mood, your imagined scenes — has full latitude.");
        user.AppendLine($"     The constraint only applies to specific claims about {contact}'s external world.");
        user.AppendLine();

        if (epistemicRenderer is not null)
        {
            user.AppendLine(epistemicRenderer.RenderReplySpeechActDisciplineSlice(contact));
            user.AppendLine();
        }

        user.Append($"Reply to {contact}'s message.");

        return new PromptPair(system, user.ToString());
    }
}
