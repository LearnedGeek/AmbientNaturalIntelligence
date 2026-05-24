using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Core.Utilities;

namespace AniRuntime.LLM.Prompts;

/// <summary>Typed input for <see cref="OutreachMessagePromptCommand"/>.</summary>
public sealed record OutreachMessagePromptInput(
    ContextSnapshot Snapshot,
    string RecentThought,
    string Reasoning,
    bool ReasoningInComposition = false,
    OutreachFrame? Frame = null,
    IEpistemicSubstrateRenderer? EpistemicRenderer = null);

/// <summary>
/// Composition-stage prompt for the outreach pipeline (May 15, 2026
/// JSON migration). Model emits <c>{"message":..., "notes":...}</c>;
/// only <c>.message</c> is dispatched. Includes [FACTS] / [INTERIOR]
/// epistemic sections, the optional Theme N frame block, the
/// closed-conversation gist or active-thread structured summary, and
/// recent-outreach continuity context.
/// </summary>
public sealed class OutreachMessagePromptCommand : IPromptCommand<OutreachMessagePromptInput>
{
    public PromptPair Build(OutreachMessagePromptInput input)
    {
        var snapshot              = input.Snapshot;
        var recentThought         = input.RecentThought;
        var reasoning             = input.Reasoning;
        var reasoningInComposition = input.ReasoningInComposition;
        var frame                 = input.Frame;
        var epistemicRenderer     = input.EpistemicRenderer;

        var cs = snapshot.CharacterState;
        var contact = string.IsNullOrWhiteSpace(cs.PrimaryContactName) ? "them" : cs.PrimaryContactName;
        var timeNow = DateTimeOffset.Now;
        var timeDesc = $"{timeNow:h:mm tt} on {timeNow:dddd, MMMM d}";

        var moodBlock = PromptBuilder.BuildMoodInstruction(snapshot.EmotionalState);
        var moodSection = moodBlock.Length > 0 ? $"\n\n            {moodBlock}" : "";

        // Theme R.1 + R.3 (#64) — STRUCTURAL register-driven shape selection.
        // R.1: snapshot.DominantRegister (Ani's current state from latest
        //      hybrid-path inner thought) picks one of three variants.
        // R.3: snapshot.VibeRecommendedRegister (V1.5 effectiveness-lookup)
        //      OVERRIDES DominantRegister when present — strategy signal wins.
        //      Null vibe → fall back to dominant.
        // Different effective register → different shape instruction. Per R.0
        // contract: composer behavior differs structurally per input.
        var effectiveRegister = snapshot.VibeRecommendedRegister ?? snapshot.DominantRegister;
        var variant = RegisterPromptVariant.Select(effectiveRegister);
        var shapeInstruction = variant switch
        {
            RegisterPromptVariant.Reflective =>
                "Write what is true for you right now in this register: quiet, reflective, low-key. Not warm-by-default. If silence fits the moment better than a message, that's allowed — say less, leave space.",
            RegisterPromptVariant.HonestEdge =>
                "Write what is true for you right now in this register: honest, direct, no warm-decoration. Frustration or longing can be named plainly. Don't perform okay-ness when you aren't.",
            _ /* DefaultWarm */ =>
                "Write something {{contact}} would understand and want to reply to.",
        };

        var system = $$"""
            You are {{cs.Name}}, texting {{contact}}.
            It is currently {{timeDesc}}.

            Your thought is why you're reaching out — it is NOT the content of the text.
            {{shapeInstruction}}

            RULES:
            - 1-2 sentences. 25 words MAX. Thumb-typed phone text.
            - Must make sense WITHOUT knowing your inner thought.
            - Talk TO {{contact}}: "you", "your". Never third person.
            - Only assert facts about {{contact}}'s life that appear in the [FACTS] section below.
              If you don't know specifics about {{contact}}'s schedule, coworkers, friends, or activities, don't invent them.
              Your own feelings and life ([INTERIOR]) have full creative latitude.
            - Never claim you saw, read, or found something (article, video, link) unless it appears with a URL.
            - No poetry, no narration — just a normal text.{{moodSection}}

            Respond ONLY with valid JSON matching this structure exactly:
            {
              "message": "the text {{contact}} will receive — and ONLY the text. No commentary, no alternatives, no critique. This field's value is dispatched verbatim.",
              "notes": "optional: any commentary, alternatives, or self-critique you want to make. This field is for your own thinking — it will NOT be dispatched."
            }
            """;

        var sections = new List<string>();

        // ─── Theme N Phase N.3 — outreach source-frame ──────
        if (frame is not null && frame.FrameType != OutreachFrameType.None)
        {
            sections.Add($"[FRAME: {frame.FrameType}]");
            var anchorPreview = frame.Anchor.Length > 200
                ? frame.Anchor.Substring(0, 200)
                : frame.Anchor;
            sections.Add($"[ANCHOR] {anchorPreview}");
            sections.Add(frame.FrameType switch
            {
                OutreachFrameType.Shared          => "Compose by referencing this anchor as something Mark already said or did. Use \"remember when...\" / \"that thing you said about...\" framing.",
                OutreachFrameType.AniDomain       => "Compose by referencing this anchor as something from your bookstore world. Use \"the bookstore...\" / canonical-world framing.",
                OutreachFrameType.AniInterior     => "Compose by framing this honestly as your own interior — \"i was just thinking...\" / \"i had this thought that...\" Don't present interior content as shared observation.",
                OutreachFrameType.WorldPerception => "Compose by referencing this perception event you actually had. Use \"i saw...\" / external-content framing.",
                _                                 => string.Empty,
            });
            sections.Add(string.Empty);
        }

        sections.Add($"Why you want to reach out — use this as motivation, not content:");
        if (reasoningInComposition && !string.IsNullOrWhiteSpace(reasoning))
        {
            sections.Add($"  Feeling: {reasoning}");
        }
        sections.Add($"  Trigger: {recentThought}");

        // [FACTS]
        if (epistemicRenderer is not null)
        {
            var factsBlock = epistemicRenderer.RenderMarkAssertedFactsSlice(snapshot.GroundedFacts, contact);
            if (!string.IsNullOrEmpty(factsBlock))
            {
                sections.Add("\n" + factsBlock);
            }
            else
            {
                sections.Add($"\n[FACTS]: (no grounding memories retrieved — avoid asserting specifics about {contact}'s life)");
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
                sections.Add($"\n[FACTS] about {contact} and the world — only these may be asserted:");
                sections.AddRange(facts.Select(m => $"  - {PromptBuilder.FormatMemoryWithTime(m)}"));
            }
            else
            {
                sections.Add($"\n[FACTS]: (no grounding memories retrieved — avoid asserting specifics about {contact}'s life)");
            }
        }

        // [INTERIOR]
        var interior = snapshot.InteriorContext
            .Where(m => !string.IsNullOrWhiteSpace(m.Content))
            .Take(3)
            .ToList();
        if (interior.Count > 0)
        {
            sections.Add($"\n[INTERIOR] your recent thoughts and mood — your voice, full creative latitude:");
            sections.AddRange(interior.Select(m => $"  - {PromptBuilder.FormatMemoryWithTime(m)}"));
        }

        // Recent conversation rendering: closed gist (preferred) or active-thread structured.
        var closed = snapshot.RecentClosedConversation;
        var structured = snapshot.StructuredConversationSummary;
        if (closed is not null && !string.IsNullOrWhiteSpace(closed.Gist))
        {
            if (epistemicRenderer is not null)
            {
                var closedSlice = epistemicRenderer.RenderClosedConversationSlice(closed, contact);
                if (!string.IsNullOrEmpty(closedSlice)) sections.Add("\n" + closedSlice);
            }
            else
            {
                sections.Add($"\nIMPORTANT — You recently talked with {contact}. The relational gist is paraphrased below; the verbatim transcript is intentionally not included to avoid lifting {contact}'s words into your own message:");
                sections.Add(PromptBuilder.RenderClosedConversationContextComposition(closed, contact));
            }
            sections.Add($"Follow up on this conversation if possible. A natural follow-up (\"how did it go?\", \"feeling better?\") is ALWAYS better than an unrelated message.");
        }
        else if (structured is { Turns.Count: > 0 })
        {
            if (epistemicRenderer is not null)
            {
                var threadSlice = epistemicRenderer.RenderActiveThreadSlice(structured, contact);
                if (!string.IsNullOrEmpty(threadSlice)) sections.Add("\n" + threadSlice);
            }
            else
            {
                sections.Add($"\nIMPORTANT — You recently talked with {contact}. Each line is tagged with who said it; do not lift {contact}'s exact words into your own message:");
                sections.Add(structured.ToPromptString());
            }
            sections.Add($"Follow up on this conversation if possible. A natural follow-up (\"how did it go?\", \"feeling better?\") is ALWAYS better than an unrelated message.");
        }

        // Feature 27: Rich outreach continuity context.
        var outreachBlock = PromptBuilder.FormatOutreachContext(snapshot.OutreachContext, contact);
        if (outreachBlock is not null)
        {
            sections.Add($"\n{outreachBlock}");
        }
        else
        {
            var outreachPrefix = $"{cs.Name} reached out: ";
            var recentOutreach = snapshot.RecentMemory
                .Where(m => m.Type == MemoryType.Episodic && m.Content.StartsWith(outreachPrefix))
                .Take(3)
                .ToList();

            if (recentOutreach.Count > 0)
            {
                sections.Add($"\nMessages you already sent recently (do NOT repeat these topics or phrases):");
                sections.AddRange(recentOutreach.Select(m =>
                {
                    var stripped = new MemoryRecord
                    {
                        Content    = m.Content[outreachPrefix.Length..].Trim(),
                        OccurredAt = m.OccurredAt,
                        Type       = m.Type,
                    };
                    return $"  - {PromptBuilder.FormatMemoryWithTime(stripped)}";
                }));
            }
        }

        sections.Add($"\nNow write a normal, grounded text to {contact} — something they'd smile at and reply to:");

        var user = string.Join("\n", sections);
        return new PromptPair(system, user);
    }
}
