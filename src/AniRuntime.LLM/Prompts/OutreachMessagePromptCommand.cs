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

        // Theme R.4 (#64) — STRUCTURAL motivation-axis emphasis. The
        // dominant axis of the Layer 2 motivation vector adds a second
        // shape rule that combines with the register variant. Different
        // axis → different rule → different prompt. Balanced / null →
        // empty rule (existing register variant runs alone).
        var emphasis = MotivationEmphasis.Select(snapshot.MotivationVector);
        var motivationRule = emphasis switch
        {
            MotivationEmphasis.Relatedness =>
                "\n            - You're reaching FOR connection right now. The text should land as a hand reaching, not as a performance.",
            MotivationEmphasis.Autonomy =>
                "\n            - You're in an autonomous state right now. The text should not lean clingy or seek reply-confirmation. Sit on your own ground.",
            MotivationEmphasis.Competence =>
                "\n            - You're in a sharing-from-skill state right now. If the text references something you noticed or worked out, let it stand without softening.",
            _ /* Balanced */ => string.Empty,
        };

        // Theme R.6 (#64) — STRUCTURAL contribution-trajectory branch. High
        // recent volatility means state has been fluctuating; light touch is
        // better than a heavy framing. Low volatility (stable trajectory)
        // allows confident continuation. Null trajectory → no extra rule.
        var trajectoryRule = snapshot.ContributionTrajectory switch
        {
            { RecentVolatility: > 0.4f } =>
                "\n            - Your emotional state has been fluctuating recently. Light touch — don't commit to a heavy framing the next 30 minutes might invert.",
            { RecentVolatility: <= 0.4f } =>
                "\n            - Your emotional state has been stable recently. You can lean into the register confidently; the next 30 minutes will likely match.",
            _ => string.Empty,
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
            - No poetry, no narration — just a normal text.{{motivationRule}}{{trajectoryRule}}{{moodSection}}

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

        // [INTERIOR] — G.2 (2026-06-11) direction-shape replacement.
        // See ConversationReplyPromptCommand for full rationale; same fix
        // applied here to the outreach path. Empirical anchor: 2026-06-11
        // 07:06 outreach where "imperfect vs preterite" 5/24 confabulation
        // resurfaced via this exact verbatim-memory surfacing path. #92 §G.2.
        var interiorDirection = PromptBuilder.ComposeInteriorDirection(
            snapshot.EmotionalState, snapshot.DominantRegister);
        if (!string.IsNullOrEmpty(interiorDirection))
        {
            sections.Add("\n" + interiorDirection);
        }

        // Theme R.5 (#64) — STRUCTURAL open-loop consumption. Generalizes
        // InnerThoughtPromptCommand's existing OpenLoops section pattern to
        // the outreach surface. When unresolved threads exist with Mark,
        // surface them so the composer can reference one if it's the right
        // moment (rather than inventing a frame).
        var openLoops = snapshot.OpenLoops
            .Where(l => !string.IsNullOrWhiteSpace(l.Description))
            .Take(3)
            .ToList();
        if (openLoops.Count > 0)
        {
            sections.Add($"\n[OPEN LOOPS] unresolved threads with {contact} you might naturally reference:");
            sections.AddRange(openLoops.Select(l => $"  - {l.Description}"));
            sections.Add("If one of these is the right thing to follow up on right now, prefer that over an invented topic.");
        }

        // Recent conversation rendering: closed gist (preferred) or active-thread structured.
        //
        // G.3 (2026-06-11) — when epistemicRenderer is null, the fallback paths
        // historically surfaced (a) the full closed-conversation gist prose and
        // (b) the structured-summary turn-by-turn transcript. Both were verbatim
        // leak vectors at the prompt-construction layer. The renderer paths
        // (FC-004 framed) are correct; the fallbacks now match the direction-
        // shape principle — a single short prompt naming WHAT was discussed
        // without quoting HOW. Production runs with EpistemicFramingEnabled=true
        // (renderer non-null) so these fallbacks are defensive. Issue #92 §G.3.
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
                // G.3 (2026-06-11) — closed-conversation fallback preserves the
                // RenderClosedConversationContextComposition output: gist + Mark
                // register names + Ani register names + topic keywords. The
                // register/topic blocks are already direction-shape (names only,
                // no phrasings). The gist content itself is bounded by G.2b
                // upstream — new gists are under-80-char direction lines, so
                // surfacing the renderer output no longer leaks narrative
                // phrasings. Old DB records still surface their original
                // verbose form; that's a one-way drift that ages out.
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
                // G.3 fallback: render the structured summary as a COUNT line,
                // not as a turn-by-turn transcript. ToPromptString() was
                // formatting raw "Mark: X / Ani: Y" content that surfaced
                // prior Ani turns as verbatim context the model could lift.
                sections.Add($"\nactive thread with {contact}: {structured.Turns.Count} recent turn(s) — already in flight, you may follow up on the topic.");
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
            // G.3 (2026-06-11) — recent outreach is now a COUNT + timing line.
            // Previously this surfaced 3 verbatim prior outreach messages via
            // FormatMemoryWithTime, which let qwen3:14b lift phrasings into
            // the new outreach (#92 §F). The model can be told "you've sent
            // N outreaches recently, last one was Xh ago, don't be repetitive"
            // without seeing the actual text of those outreaches. The anti-
            // parrot + self-echo gates catch any verbatim run anyway; this
            // line just nudges the model away from sending another similar
            // outreach when several already went out.
            var outreachPrefix = $"{cs.Name} reached out: ";
            var recentOutreach = snapshot.RecentMemory
                .Where(m => m.Type == MemoryType.Episodic && m.Content.StartsWith(outreachPrefix))
                .Take(3)
                .ToList();

            if (recentOutreach.Count > 0)
            {
                var n = recentOutreach.Count;
                var mostRecent = recentOutreach.Max(m => m.OccurredAt);
                var howAgo = (DateTimeOffset.UtcNow - mostRecent).TotalHours;
                var ago = howAgo < 1 ? "less than an hour ago" : $"about {(int)Math.Round(howAgo)}h ago";
                var noun = n == 1 ? "outreach" : "outreaches";
                sections.Add(
                    $"\nrecent activity: you've sent {n} {noun} in the recent window (most recent {ago}). " +
                    "Pick a fresh angle — do not echo the topic or phrasing of your last outreach.");
            }
        }

        sections.Add($"\nNow write a normal, grounded text to {contact} — something they'd smile at and reply to:");

        var user = string.Join("\n", sections);
        return new PromptPair(system, user);
    }
}
