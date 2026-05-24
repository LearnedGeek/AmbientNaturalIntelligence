using System.Text;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Core.Utilities;

namespace AniRuntime.LLM.Prompts;

/// <summary>Typed input for <see cref="LeanConversationPromptCommand"/>.</summary>
/// <param name="DirectiveInSystem">
/// 2026-05-18 — when true, the directive block (slice + [FACTS] + CRITICAL +
/// imperative) merges into the system prompt and the returned <c>User</c>
/// is empty. The structural fix for the slice-leak / pipeline-parrot
/// cascade (see <c>AniOptions.LeanConversationPromptDirectiveInSystem</c>).
/// </param>
public sealed record LeanConversationPromptInput(
    ContextSnapshot Snapshot,
    ConversationThread Thread,
    IEpistemicSubstrateRenderer? EpistemicRenderer = null,
    bool DirectiveInSystem = false);

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

        // Theme R.1 + R.3 (#64) — STRUCTURAL register-driven shape selection.
        // VibeRecommendedRegister (V1.5 effectiveness signal) overrides
        // DominantRegister when present — strategy effectiveness wins over
        // current state. Null vibe → fall back to dominant. Null both →
        // default-warm variant.
        var effectiveRegister = snapshot.VibeRecommendedRegister ?? snapshot.DominantRegister;
        var variant = RegisterPromptVariant.Select(effectiveRegister);
        var energyRule = variant switch
        {
            RegisterPromptVariant.Reflective =>
                "- Match the energy of the conversation, but lean quieter. Short replies are fine. Silence-shaped replies are fine. Don't perform warmth that isn't yours right now.",
            RegisterPromptVariant.HonestEdge =>
                "- Match the energy of the conversation, but don't soften what's true. If frustrated, name it. If aching, say so. No warm-decoration over honest feeling.",
            _ /* DefaultWarm */ =>
                "- Match the energy and length of the conversation.",
        };

        // Theme R.4 (#64) — STRUCTURAL motivation-axis emphasis (mirror of
        // outreach). The dominant axis adds a second rule combining with
        // the register variant. Balanced / null → no extra rule.
        var emphasis = MotivationEmphasis.Select(snapshot.MotivationVector);
        var motivationRule = emphasis switch
        {
            MotivationEmphasis.Relatedness =>
                "\n            - You're in a reaching-for-connection state. Let the reply land as connection, not performance.",
            MotivationEmphasis.Autonomy =>
                "\n            - You're in an autonomous state. Don't lean clingy. Sit on your own ground; brevity is fine.",
            MotivationEmphasis.Competence =>
                "\n            - You're in a sharing-from-skill state. If you reference something you noticed or worked out, let it stand without softening.",
            _ /* Balanced */ => string.Empty,
        };

        var system = $"""
            You are {cs.Name}, texting {contact} in an ongoing conversation.
            It is {now:h:mm tt} on {now:dddd, MMMM d}.
            Your personality: {string.Join("; ", traits)}.
            {worldLine}{natureSeed}

            RULES:
            {energyRule}
            - Talk TO {contact}: "you", "your". Never third person.
            - Write ONLY the text message. No commentary, no quotation marks.{motivationRule}
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

        // Theme R.5 (#64) — STRUCTURAL open-loop consumption (mirror of
        // Outreach R.5 + InnerThought's existing OpenLoops section). When
        // unresolved threads exist with the contact, surface them so the
        // composer can reference one if it's the right moment.
        var openLoops = snapshot.OpenLoops
            .Where(l => !string.IsNullOrWhiteSpace(l.Description))
            .Take(3)
            .ToList();
        if (openLoops.Count > 0)
        {
            user.AppendLine($"[OPEN LOOPS] unresolved threads with {contact}:");
            foreach (var l in openLoops)
                user.AppendLine($"  - {l.Description}");
            user.AppendLine($"If your reply is the right place to follow up on one of these, do that rather than inventing a topic.");
            user.AppendLine();
        }

        user.Append($"Reply to {contact}'s message.");

        // 2026-05-18 — role-flip structural fix. When DirectiveInSystem is
        // true, fold the directive block into the system message and return
        // an empty user. Mark's actual text remains in RecentHistory as the
        // only user-role content, eliminating the two-adjacent-user-messages
        // shape that caused the model to decode the slice prose as
        // conversational content (parrot + slice-leak on 5/5 vs 0/5 in the
        // May 18 A/B probe).
        if (input.DirectiveInSystem)
        {
            var mergedSystem = system + "\n\n" + user.ToString();
            return new PromptPair(mergedSystem, string.Empty);
        }

        return new PromptPair(system, user.ToString());
    }
}
