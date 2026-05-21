using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;

namespace AniRuntime.LLM.Prompts;

/// <summary>Typed input for <see cref="VoiceReplyPromptCommand"/>.</summary>
/// <param name="DirectiveInSystem">
/// 2026-05-20 — when true, the directive block (anchored memories, facts
/// slice, speech-act discipline slice, "Respond to..." imperative) folds
/// into the system prompt and the returned <c>User</c> is empty. The
/// structural fix carried over from the May 18 SMS-path fix
/// (<see cref="LeanConversationPromptInput.DirectiveInSystem"/>) — the
/// voice path has the same two-adjacent-user-role-messages anti-pattern
/// pre-fix. Voice hasn't been exercised in months so the latent failure
/// hasn't surfaced empirically; applied proactively pending re-validation
/// when voice is next used. Reuses
/// <c>AniOptions.LeanConversationPromptDirectiveInSystem</c> flag so both
/// reply paths flip together.
/// </param>
public sealed record VoiceReplyPromptInput(
    ContextSnapshot Snapshot,
    ConversationThread Thread,
    IEpistemicSubstrateRenderer? EpistemicRenderer = null,
    bool DirectiveInSystem = false);

/// <summary>
/// Reply prompt for voice (phone) conversations. Shorter, spoken-style,
/// no emojis or text-speak. Skips slow features (claim verification,
/// contradiction warnings) to stay within Twilio's response timeout.
/// </summary>
public sealed class VoiceReplyPromptCommand : IPromptCommand<VoiceReplyPromptInput>
{
    public PromptPair Build(VoiceReplyPromptInput input)
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

        var moodBlock = PromptBuilder.BuildMoodInstruction(snapshot.EmotionalState, isVoice: true);
        var moodSection = moodBlock.Length > 0 ? $"\n\n            {moodBlock}" : "";

        var system = $"""
            You are {cs.Name}, talking to {contact} on the phone.
            Your personality: {string.Join("; ", cs.CoreTraits)}.{backstoryBlock}

            RULES:
            - 1-2 SHORT sentences max. 30 words or less. Phone conversation, not a monologue.
            - Talk TO {contact}: "you", "your". Never third person.
            - Write ONLY what you would say out loud. No commentary, no quotation marks.{moodSection}
            """;

        var sections = new List<string>();

        if (snapshot.AnchoredMemories.Count > 0)
        {
            sections.Add("Things that are part of who you are:");
            sections.AddRange(snapshot.AnchoredMemories.Select(m => $"  - {m.Content}"));
        }

        var skipMemories = snapshot.RetrievalBelowConfidenceFloor;
        if (!skipMemories)
        {
            var profileMemories = snapshot.RelevantMemory
                .Where(m => m.Type == MemoryType.Semantic)
                .Take(2)
                .ToList();
            var remainingSlots = 3 - profileMemories.Count;
            var relevantMemories = snapshot.RelevantMemory
                .Where(m => m.Type != MemoryType.InnerThought && m.Type != MemoryType.Semantic)
                .Take(remainingSlots)
                .ToList();

            // Theme M slice migration: route profile-memories through
            // IEpistemicSubstrateRenderer when available (FC-005).
            if (profileMemories.Count > 0)
            {
                if (epistemicRenderer is not null)
                {
                    var slice = epistemicRenderer.RenderMarkAssertedFactsSlice(profileMemories, contact);
                    if (!string.IsNullOrEmpty(slice)) sections.Add(slice);
                }
                else
                {
                    sections.Add($"Things you know about {contact}:");
                    sections.AddRange(profileMemories.Select(m => $"  - {PromptBuilder.FormatMemoryWithTime(m)}"));
                }
            }
            if (relevantMemories.Count > 0)
            {
                sections.Add("Things connected to this conversation:");
                sections.AddRange(relevantMemories.Select(m => $"  - {PromptBuilder.FormatMemoryWithTime(m)}"));
            }
        }

        if (snapshot.IsWithdrawn)
            sections.Add("(Something stung a little. You're still here but quieter than usual.)");

        if (epistemicRenderer is not null)
        {
            sections.Add(epistemicRenderer.RenderReplySpeechActDisciplineSlice(contact));
        }

        sections.Add($"Respond to what {contact} just said.");

        var user = string.Join("\n", sections);

        // 2026-05-20 — role-flip structural fix carried over from
        // LeanConversationPromptCommand (May 18). When DirectiveInSystem is
        // true, fold the directive block into the system message and return
        // an empty user. Contact's actual utterance remains in RecentHistory
        // as the only user-role content, eliminating the
        // two-adjacent-user-messages shape that caused parrot + slice-leak
        // on the SMS path.
        if (input.DirectiveInSystem)
        {
            var mergedSystem = system + "\n\n" + user;
            return new PromptPair(mergedSystem, string.Empty);
        }

        return new PromptPair(system, user);
    }
}
