using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;

namespace AniRuntime.LLM.Prompts;

/// <summary>Typed input for <see cref="ReconsiderationReplyPromptCommand"/>.</summary>
public sealed record ReconsiderationReplyPromptInput(
    ContextSnapshot Snapshot,
    ConversationThread Thread);

/// <summary>
/// Reply prompt for when Ani initially chose silence but desire built
/// enough to reconsider. Encourages acknowledging what the contact said
/// before transitioning to what's on her mind — a natural "wait, one
/// more thing" moment.
/// </summary>
public sealed class ReconsiderationReplyPromptCommand : IPromptCommand<ReconsiderationReplyPromptInput>
{
    public PromptPair Build(ReconsiderationReplyPromptInput input)
    {
        var snapshot = input.Snapshot;
        var cs       = snapshot.CharacterState;
        var contact  = string.IsNullOrWhiteSpace(cs.PrimaryContactName) ? "them" : cs.PrimaryContactName;

        var moodBlock = PromptBuilder.BuildMoodInstruction(snapshot.EmotionalState);
        var moodSection = moodBlock.Length > 0 ? $"\n\n            {moodBlock}" : "";

        var system = $"""
            You are {cs.Name}, texting {contact}.
            Your personality: {string.Join("; ", cs.CoreTraits)}.

            CONTEXT: {contact} sent you a message a while ago. You read it and didn't reply at first,
            but now something else is on your mind and you want to reach out.

            RULES:
            - Briefly acknowledge what {contact} said — don't ignore it. A quick "hey" or short
              reaction is fine, then naturally transition to what you actually want to say.
            - This should feel like a "oh hey, also..." or "ok but..." moment — casual, not forced.
            - Match the energy and length of the conversation.
            - Talk TO {contact}: "you", "your". NEVER third person.
            - Be yourself — warm, funny, real.
            - Write ONLY the text message. No commentary, no quotation marks.{moodSection}
            """;

        var sections = new List<string>();

        var recentThoughts = snapshot.RecentMemory
            .Where(m => m.Type == MemoryType.InnerThought)
            .Take(2)
            .ToList();
        if (recentThoughts.Count > 0)
        {
            sections.Add("What's been on your mind lately:");
            sections.AddRange(recentThoughts.Select(t => $"  - {PromptBuilder.FormatMemoryWithTime(t)}"));
        }

        var mood = snapshot.EmotionalState.Describe();
        if (mood.Length > 0)
            sections.Add($"(Your current mood: {mood})");

        sections.Add($"Acknowledge {contact}'s message, then share what's on your mind.");

        var user = string.Join("\n", sections);
        return new PromptPair(system, user);
    }
}
