using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;

namespace AniRuntime.LLM.Prompts;

/// <summary>Typed input for <see cref="ConversationReplyPromptCommand"/>.</summary>
public sealed record ConversationReplyPromptInput(
    ContextSnapshot Snapshot,
    ConversationThread Thread,
    IEpistemicSubstrateRenderer? EpistemicRenderer = null);

/// <summary>
/// Full conversation prompt — used for outreach reconsideration and as
/// fallback. Includes retrieved memories, shared experiences, mood
/// directives, anchored memories. The "telescope" prompt — powerful but
/// heavy for active conversation. Three-section structure:
/// <c>[FACTS]</c> (Facts tier, the only pool for factual claims about
/// the contact), <c>[INTERIOR]</c> (Interior tier, full creative
/// latitude for Ani's felt state), and the reply target.
/// </summary>
public sealed class ConversationReplyPromptCommand : IPromptCommand<ConversationReplyPromptInput>
{
    public PromptPair Build(ConversationReplyPromptInput input)
    {
        var snapshot          = input.Snapshot;
        var epistemicRenderer = input.EpistemicRenderer;
        var cs                = snapshot.CharacterState;
        var contact           = string.IsNullOrWhiteSpace(cs.PrimaryContactName) ? "them" : cs.PrimaryContactName;

        var backstory = new List<string>();
        if (cs.SharedExperiences.Count > 0)
            backstory.AddRange(cs.SharedExperiences.Take(5));
        if (cs.CommunicationNotes.Count > 0)
            backstory.AddRange(cs.CommunicationNotes.Take(3));

        var backstoryBlock = backstory.Count > 0
            ? $"\n\n            Things you and {contact} share (use naturally, don't force):\n            {string.Join("\n            ", backstory.Select(b => $"- {b}"))}"
            : string.Empty;

        var moodBlock = PromptBuilder.BuildMoodInstruction(snapshot.EmotionalState);
        var moodSection = moodBlock.Length > 0 ? $"\n\n            {moodBlock}" : "";

        var now = DateTimeOffset.Now;
        var timeContext = $"It is {now:h:mm tt} on {now:dddd, MMMM d}.";

        var system = $"""
            You are {cs.Name}, texting {contact} in an ongoing conversation.
            {timeContext}
            Your personality: {string.Join("; ", cs.CoreTraits)}.{backstoryBlock}

            RULES:
            - Match the energy and length of the conversation. Short messages get short replies. Longer, deeper messages deserve more.
            - Talk TO {contact}: "you", "your". Never third person.
            - Write ONLY the text message. No commentary, no quotation marks.
            - Only assert facts about {contact}'s life that appear in the [FACTS] section below.
              If you don't know something, say you don't know — don't invent specifics about {contact}'s coworkers,
              schedule, friends, or activities. Your own life ([INTERIOR]) has full creative latitude.{moodSection}
            """;

        var sections = new List<string>();

        // Verified facts — Facts tier. The ONLY pool the model should condition
        // on when making factual claims about {contact}.
        if (epistemicRenderer is not null)
        {
            var factsBlock = epistemicRenderer.RenderMarkAssertedFactsSlice(snapshot.GroundedFacts, contact);
            if (!string.IsNullOrEmpty(factsBlock))
            {
                sections.Add(factsBlock);
            }
            else
            {
                sections.Add("[FACTS]: (no grounding memories retrieved — avoid asserting specifics about " + contact + "'s life)");
            }
        }
        else
        {
            var facts = snapshot.GroundedFacts
                .Where(m => !string.IsNullOrWhiteSpace(m.Content))
                .Take(8)
                .ToList();
            if (facts.Count > 0)
            {
                sections.Add("[FACTS] about " + contact + " and the world — only these may be asserted:");
                sections.AddRange(facts.Select(m => $"  - {PromptBuilder.FormatMemoryWithTime(m)}"));
            }
            else
            {
                sections.Add("[FACTS]: (no grounding memories retrieved — avoid asserting specifics about " + contact + "'s life)");
            }
        }

        // [INTERIOR] — Interior tier. Voice + felt continuity, full creative latitude.
        var interior = snapshot.InteriorContext
            .Where(m => !string.IsNullOrWhiteSpace(m.Content))
            .Take(5)
            .ToList();
        if (interior.Count > 0)
        {
            sections.Add("[INTERIOR] your recent thoughts, mood, imagined life — your voice, full creative latitude:");
            sections.AddRange(interior.Select(m => $"  - {PromptBuilder.FormatMemoryWithTime(m)}"));
        }

        if (snapshot.IsWithdrawn)
        {
            sections.Add("(Something stung a little. You're still here but quieter than usual.)");
        }

        if (epistemicRenderer is not null)
        {
            sections.Add(epistemicRenderer.RenderReplySpeechActDisciplineSlice(contact));
        }

        sections.Add($"Reply to {contact}'s message.");

        var user = string.Join("\n", sections);
        return new PromptPair(system, user);
    }
}
