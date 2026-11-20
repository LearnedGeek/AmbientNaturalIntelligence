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

        // #203 follow-up (2026-11-20): all mood + body scaffolds land in
        // SYSTEM. BuildMoodInstruction was already in SYSTEM here; the
        // describe-form fallback and body parenthetical were in USER and
        // provided the material Llama v7-inner echoed back into saved
        // thoughts. See InnerThoughtPromptCommand for the full rationale.
        var moodDirective = PromptBuilder.BuildMoodInstruction(snapshot.EmotionalState);
        string moodSystemBlock;
        if (moodDirective.Length > 0)
        {
            moodSystemBlock = "\n\n            " + moodDirective;
        }
        else
        {
            var moodParts = new List<string>();
            var moodDesc = snapshot.EmotionalState.Describe();
            if (moodDesc.Length > 0)
                moodParts.Add($"(Your current mood: {moodDesc})");
            var body = InteroceptiveDescriptorRenderer.RenderParenthetical(snapshot.EmotionalState);
            if (body.Length > 0)
                moodParts.Add(body);
            moodSystemBlock = moodParts.Count > 0
                ? "\n\n            " + string.Join("\n            ", moodParts)
                : string.Empty;
        }

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
            - Write ONLY the text message. No commentary, no quotation marks.{moodSystemBlock}
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

        sections.Add($"Acknowledge {contact}'s message, then share what's on your mind.");

        var user = string.Join("\n", sections);
        return new PromptPair(system, user);
    }
}
