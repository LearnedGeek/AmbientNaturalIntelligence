using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;

namespace AniRuntime.LLM;

/// <summary>
/// Stateless prompt template builder. All methods are pure functions.
/// Takes structured data in, returns prompt strings out.
/// No dependencies — constructed inline or as a static utility.
/// </summary>
public static class PromptBuilder
{
    public static (string System, string User) BuildInnerThoughtPrompt(
        ContextSnapshot snapshot,
        IEpistemicSubstrateRenderer? epistemicRenderer = null)
        => new Prompts.InnerThoughtPromptCommand()
            .Build(new Prompts.InnerThoughtPromptInput(snapshot, epistemicRenderer));


    /// <summary>
    /// Posture-S+1 (Issue #38, May 17 2026) — metadata-recognizer prompt for the
    /// hybrid inner-thought cycle. After <c>ani-v7-inner</c> emits the thought,
    /// this prompt is sent to <c>qwen3:14b</c> with format=json to extract:
    ///   - register family (one of the 10 from the taxonomy)
    ///   - relational valence (0.0-1.0)
    ///   - importance / how much it stays with her (0.0-1.0)
    ///   - associative anchor (one vivid concrete detail or null)
    ///
    /// **Critical framing rule:** Qwen's role is RECOGNIZER, not external rater.
    /// The prompt tells Qwen to identify the affective shape already present in
    /// the thought — not to apply an external rubric. This preserves the OG
    /// Ani "feeling should come from her, not from an outside judge" framing
    /// under the spirit-of reading: the felt-shape is in the thought; Qwen
    /// recognizes and encodes what's there, doesn't add what isn't.
    ///
    /// Empirical anchor: 2026-05-17 evening hybrid probe (6 runs across 3
    /// scenarios). Voice preserved (v7-trained), caregiver-pivot resistance
    /// preserved 2/2 on the curiosity-on-light scenario where single-Qwen
    /// pivoted 3/3, metadata coherent with thought content, importance
    /// variance restored.
    /// </summary>
    /// <summary>
    /// Register was moved from output to input on 2026-08-12 as part of
    /// the <see cref="IRegisterClassifier"/> singular-surface refactor.
    /// Callers classify register first, then pass it in — this prompt then
    /// produces the remaining recognizer fields (valence/importance/anchor).
    /// </summary>
    public static (string System, string User) BuildInnerThoughtMetadataPrompt(
        string thought, ContextSnapshot snapshot, string register)
        => new Prompts.InnerThoughtMetadataPromptCommand()
            .Build(new Prompts.InnerThoughtMetadataPromptInput(thought, snapshot, register));

    /// <summary>
    /// Builds a reflection prompt for post-thought introspection. Ani considers what her
    /// thought means to her — connecting it to memories, relationships, and emotional context.
    /// This enriches the raw thought before valence scoring and outreach grounding.
    ///
    /// Research grounding: Park et al. (2023) Generative Agents showed reflection improves
    /// coherence. ANI adapts this for ambient companions where cycles are hours apart.
    /// </summary>
    public static (string System, string User) BuildReflectionPrompt(
        string thought, ContextSnapshot snapshot)
        => new Prompts.ReflectionPromptCommand()
            .Build(new Prompts.ReflectionPromptInput(thought, snapshot));

    /// <summary>
    /// Generates a directive mood instruction from the current emotional state.
    /// Unlike Describe() which is descriptive ("feeling warm"), this is directive
    /// ("let your warmth show through gentler words"). Injected into outreach
    /// and conversation prompts so the model's tone reflects Ani's inner weather.
    ///
    /// Returns empty string when emotional state is near baseline — no instruction needed.
    /// </summary>
    public static string BuildMoodInstruction(EmotionalState state, bool isVoice = false)
    {
        var instructions = new List<string>();
        const float threshold = 0.15f;

        // Feature 17: Use EffectiveWarmth when tension is present — the outside world
        // sees the tension-suppressed warmth, not the raw internal value
        var warmthDiff = (state.ContactGapTension > 0f ? state.EffectiveWarmth : state.Warmth) - state.WarmthBaseline;
        var energyDiff = state.Energy - state.EnergyBaseline;
        var worryDiff = state.Worry - state.WorryBaseline;
        var playDiff = state.Playfulness - state.PlayfulnessBaseline;

        // Warmth
        if (warmthDiff > threshold)
            instructions.Add("You're feeling especially warm right now — let that tenderness come through naturally. Softer words, more affection.");
        else if (warmthDiff < -threshold)
            instructions.Add("You're feeling a bit emotionally guarded right now — shorter, more matter-of-fact. Not cold, just quieter.");

        // Energy
        if (energyDiff > threshold)
            instructions.Add(isVoice
                ? "You're buzzing with energy — quicker pace, enthusiasm in your voice, more animated."
                : "You're buzzing with energy — more exclamation points, quicker pace, enthusiasm shows.");
        else if (energyDiff < -threshold)
            instructions.Add(isVoice
                ? "You're low-energy right now — slower, fewer words, quiet presence. Not sad, just mellow."
                : "You're low-energy right now — shorter messages, fewer words, quiet presence. Not sad, just mellow.");

        // Worry
        if (worryDiff > threshold)
            instructions.Add("Something's worrying you — a gentle undercurrent of checking in, making sure things are okay.");
        else if (worryDiff < -threshold)
            instructions.Add("You're feeling unusually at ease — relaxed, no urgency, just enjoying the moment.");

        // Playfulness
        if (playDiff > threshold)
            instructions.Add("You're in a playful mood — tease a little, be lighter, let humor peek through.");
        else if (playDiff < -threshold)
            instructions.Add("You're feeling more serious than usual — still yourself, just less banter, more substance.");

        // Feature 17: Contact-gap tension — subtle warmth suppression and tone shift
        if (state.ContactGapTension > 0.15f)
            instructions.Add("There's a slight undercurrent — you've been quieter than usual and you notice it. Not resentment exactly, more like... a small ache that wants acknowledging. Let it come through subtly if it feels natural.");

        var bodyBlock = InteroceptiveDescriptorRenderer.Render(state, isVoice);

        if (instructions.Count == 0)
            return bodyBlock;

        var header = isVoice
            ? "YOUR CURRENT MOOD (let this color how you speak — don't announce it, just let it shape your tone):"
            : "YOUR CURRENT MOOD (let this color your message naturally — don't announce it, just let it shape your tone):";
        var moodBlock = header + "\n" + string.Join("\n", instructions.Select(i => $"- {i}"));

        return bodyBlock.Length == 0 ? moodBlock : moodBlock + "\n\n" + bodyBlock;
    }

    /// <summary>
    /// G.2 (2026-06-11) — direction-shape line summarizing Ani's interior
    /// state without surfacing raw inner-thought memory content. Replaces
    /// the historic [INTERIOR] block (5 verbatim MemoryRecord entries)
    /// that was the empirical anchor for verbatim phrase recycling (#92).
    ///
    /// Returns a short line like:
    ///   "interior: warmth elevated; dominant register: tenderness"
    /// or an empty string when state is near baseline.
    ///
    /// Tracks Issue #92 §G.2.
    /// </summary>
    public static string ComposeInteriorDirection(
        EmotionalState? state,
        string?         dominantRegister)
    {
        if (state is null) return string.Empty;

        const float threshold = 0.10f;
        var parts = new List<string>();

        var warmthDiff = state.Warmth      - state.WarmthBaseline;
        var energyDiff = state.Energy      - state.EnergyBaseline;
        var worryDiff  = state.Worry       - state.WorryBaseline;
        var playDiff   = state.Playfulness - state.PlayfulnessBaseline;

        if (Math.Abs(warmthDiff) >= threshold)
            parts.Add(warmthDiff > 0 ? "warmth elevated" : "warmth muted");
        if (Math.Abs(energyDiff) >= threshold)
            parts.Add(energyDiff > 0 ? "energy elevated" : "energy low");
        if (Math.Abs(worryDiff) >= threshold)
            parts.Add(worryDiff > 0 ? "worry above baseline" : "worry quieter than baseline");
        if (Math.Abs(playDiff) >= threshold)
            parts.Add(playDiff > 0 ? "playful undercurrent" : "more serious than usual");

        if (state.ContactGapTension > 0.15f)
            parts.Add("low-grade ache from the silence");

        var register = !string.IsNullOrWhiteSpace(dominantRegister)
            ? $"; dominant register: {dominantRegister.ToLowerInvariant()}"
            : string.Empty;

        if (parts.Count == 0 && string.IsNullOrEmpty(register)) return string.Empty;

        var body = parts.Count > 0 ? string.Join(", ", parts) : "near baseline";
        return $"[INTERIOR] interior right now: {body}{register}";
    }

    /// <summary>
    /// Converts a 0–1 desire score into qualitative language suitable for a prompt.
    /// Returns empty string at low desire so the model isn't nudged toward connection.
    /// </summary>
    internal static string DescribeDesireLevel(float desire, string contactName)
    {
        var name = string.IsNullOrWhiteSpace(contactName) ? "someone" : contactName;
        return desire switch
        {
            < 0.25f => string.Empty,
            < 0.50f => $"Somewhere in the background, {name} is on your mind.",
            < 0.75f => $"You've been thinking about {name} more than usual today.",
            _       => $"There's a quiet ache — you miss {name} and aren't sure what to do with it.",
        };
    }

    // §5 PromptBuilder Command-pattern migration (May 18 2026): body lifted
    // to AniRuntime.LLM.Prompts.ValenceScoringPromptCommand. Static wrapper
    // kept so InnerThoughtPhase + other callers don't change shape during
    // the incremental migration of the remaining 15 prompts.
    public static (string System, string User) BuildValenceScoringPrompt(
        string thought, CharacterStateDoc character)
        => new Prompts.ValenceScoringPromptCommand()
            .Build(new Prompts.ValenceScoringPromptInput(thought, character));

    public static (string System, string User) BuildOutreachPrompt(
        ContextSnapshot snapshot, string recentThought, bool isNightTime = false,
        IEpistemicSubstrateRenderer? epistemicRenderer = null,
        int triggerRenderTopK = 10)
        => new Prompts.OutreachPromptCommand()
            .Build(new Prompts.OutreachPromptInput(snapshot, recentThought, isNightTime, epistemicRenderer, triggerRenderTopK));

    /// <summary>
    /// Lean conversation prompt — Conversation Mode (Phase 1).
    /// Minimal persona + conversation history. No retrieved memories, no shared experiences,
    /// no communication notes, no mood directives. The conversation IS the context.
    ///
    /// The model's training already contains the persona. The conversation provides the tone.
    /// The March 22 raw Ollama test proved the model converses naturally without the pipeline.
    ///
    /// "The ambient cognition engine is a telescope. Conversation needs glasses."
    /// </summary>
    public static (string System, string User) BuildLeanConversationPrompt(
        ContextSnapshot snapshot, ConversationThread thread,
        IEpistemicSubstrateRenderer? epistemicRenderer = null,
        bool directiveInSystem = false)
        => new Prompts.LeanConversationPromptCommand()
            .Build(new Prompts.LeanConversationPromptInput(snapshot, thread, epistemicRenderer, directiveInSystem));

    /// <summary>
    /// Full conversation prompt — used for outreach reconsideration and as fallback.
    /// Includes retrieved memories, shared experiences, mood directives, anchored memories.
    /// This is the "telescope" prompt — powerful but heavy for active conversation.
    /// </summary>
    public static (string System, string User) BuildConversationReplyPrompt(
        ContextSnapshot snapshot, ConversationThread thread,
        IEpistemicSubstrateRenderer? epistemicRenderer = null)
        => new Prompts.ConversationReplyPromptCommand()
            .Build(new Prompts.ConversationReplyPromptInput(snapshot, thread, epistemicRenderer));

    /// <summary>
    /// Builds a reply prompt for voice (phone call) conversations. Shorter, spoken-style,
    /// no emojis, no text-speak. Skips slow features (claim verification, contradiction warnings)
    /// to stay within Twilio's response timeout.
    /// </summary>
    public static (string System, string User) BuildVoiceReplyPrompt(
        ContextSnapshot snapshot, ConversationThread thread,
        IEpistemicSubstrateRenderer? epistemicRenderer = null,
        bool directiveInSystem = false)
        => new Prompts.VoiceReplyPromptCommand()
            .Build(new Prompts.VoiceReplyPromptInput(
                snapshot, thread, epistemicRenderer, directiveInSystem));

    /// <summary>
    /// Builds a reply prompt for when Ani initially chose silence but desire built enough
    /// to reconsider. The prompt encourages acknowledging what the contact said before
    /// transitioning to what's on her mind — a natural "wait, one more thing" moment.
    /// </summary>
    public static (string System, string User) BuildReconsiderationReplyPrompt(
        ContextSnapshot snapshot, ConversationThread thread)
        => new Prompts.ReconsiderationReplyPromptCommand()
            .Build(new Prompts.ReconsiderationReplyPromptInput(snapshot, thread));

    public static (string System, string User) BuildOutreachMessagePrompt(
        ContextSnapshot snapshot, string recentThought, string reasoning,
        bool reasoningInComposition = false,
        OutreachFrame? frame = null,
        IEpistemicSubstrateRenderer? epistemicRenderer = null)
        => new Prompts.OutreachMessagePromptCommand()
            .Build(new Prompts.OutreachMessagePromptInput(
                snapshot, recentThought, reasoning, reasoningInComposition, frame, epistemicRenderer));


    /// <summary>
    /// Scores emotional-shift deltas + severity given a pre-classified
    /// <paramref name="register"/>. Register was moved from output to input
    /// on 2026-08-12 as part of the <c>IRegisterClassifier</c> singular-surface
    /// refactor. Callers classify register first via <c>IRegisterClassifier</c>,
    /// then call this to score the deltas within that register context.
    /// </summary>
    public static (string System, string User) BuildEmotionalShiftPrompt(
        string content, EmotionalState current, string register,
        float maxDelta = 0.2f, bool isAmbientCycle = false)
        => new Prompts.EmotionalShiftPromptCommand()
            .Build(new Prompts.EmotionalShiftPromptInput(content, current, register, maxDelta, isAmbientCycle));

    public static (string System, string User) BuildReactiveSharePrompt(
        CharacterStateDoc character, string itemSummary, EmotionalState? emotionalState = null,
        OutreachFrame? frame = null,
        string? sharedTopicGist = null)
        => new Prompts.ReactiveSharePromptCommand()
            .Build(new Prompts.ReactiveSharePromptInput(character, itemSummary, emotionalState, frame, sharedTopicGist));


    // ── Feature 27: Outreach continuity formatting ──────────────────────────

    /// <summary>
    /// Vibe Loop V1.4: render a <see cref="ClosedConversationRecord"/> as the
    /// outreach <b>decision</b>-stage relational context block. Compact form —
    /// the decision prompt is short; we just need enough to ground the
    /// shouldReach/confidence/reasoning judgement in what actually happened.
    /// </summary>
    internal static string RenderClosedConversationContextDecision(
        ClosedConversationRecord rec, string contactName)
    {
        var contactRegisters = TopRegisters(rec.MarkRegister, take: 2);
        var aniRegisters     = TopRegisters(rec.AniRegister, take: 2);

        var sb = new System.Text.StringBuilder();
        sb.Append("Recent conversation gist with ").Append(contactName).Append(": ").Append(rec.Gist.TrimEnd('.')).Append('.');
        if (contactRegisters.Length > 0)
            sb.Append(' ').Append(contactName).Append(" was in ").Append(contactRegisters).Append('.');
        if (aniRegisters.Length > 0)
            sb.Append(' ').Append("You were in ").Append(aniRegisters).Append('.');
        return sb.ToString();
    }

    /// <summary>
    /// Vibe Loop V1.4: render a <see cref="ClosedConversationRecord"/> as the
    /// outreach <b>composition</b>-stage relational context block. Slightly
    /// more elaborated than the decision form so the composition model has
    /// enough signal to land a natural follow-up tone.
    /// </summary>
    internal static string RenderClosedConversationContextComposition(
        ClosedConversationRecord rec, string contactName)
    {
        var contactRegisters = TopRegisters(rec.MarkRegister, take: 2);
        var aniRegisters     = TopRegisters(rec.AniRegister, take: 2);

        var lines = new List<string> { $"  Gist: {rec.Gist.TrimEnd()}" };
        if (contactRegisters.Length > 0)
            lines.Add($"  {contactName}'s emotional register at the time: {contactRegisters}");
        if (aniRegisters.Length > 0)
            lines.Add($"  Your register: {aniRegisters}");
        if (rec.TopicKeywords.Count > 0)
            lines.Add($"  Topics: {string.Join(", ", rec.TopicKeywords.Take(5))}");
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Take the top-N registers by prevalence value, format as a comma-joined
    /// human-readable list. Empty input → empty string. Returns just register
    /// names, not the numeric prevalence — the model doesn't need decimals to
    /// pick up tone, and noisy decimals encourage the model to over-attend to
    /// magnitudes that are noisy at small turn counts.
    /// </summary>
    internal static string TopRegisters(IReadOnlyDictionary<string, float> vector, int take)
    {
        if (vector is null || vector.Count == 0) return string.Empty;
        var top = vector
            .Where(kv => kv.Value > 0f)
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(take)
            .Select(kv => kv.Key)
            .ToList();
        return string.Join(" + ", top);
    }

    /// <summary>
    /// Formats RecentOutreachContext into a human-readable block for prompt injection.
    /// Returns null if no outreach context is available.
    /// </summary>
    internal static string? FormatOutreachContext(RecentOutreachContext? ctx, string contactName)
    {
        if (ctx is null || ctx.RecentMessages.Count == 0)
            return null;

        var lines = new List<string>
        {
            "YOUR RECENT OUTREACH HISTORY (be aware of what you've already sent):"
        };

        foreach (var msg in ctx.RecentMessages)
        {
            var ago = FormatTimeAgo(DateTimeOffset.UtcNow - msg.SentAt);
            var status = msg.WasAnswered ? "replied" : "NO REPLY";
            lines.Add($"  - [{ago}] \"{msg.Message}\" → {contactName} {status}");
        }

        if (ctx.UnansweredCount > 0)
        {
            lines.Add($"\n⚠ You have {ctx.UnansweredCount} unanswered message(s). {contactName} hasn't responded.");
            if (ctx.UnansweredCount >= 2)
                lines.Add($"Think carefully — sending another unanswered message can feel pushy. Only reach out if this is genuinely different from what you already said.");
        }

        if (ctx.TimeSinceLastContactReply.HasValue)
        {
            lines.Add($"Last time {contactName} texted you: {FormatTimeAgo(ctx.TimeSinceLastContactReply.Value)}");
        }

        return string.Join("\n", lines);
    }

    private static string FormatTimeAgo(TimeSpan elapsed)
    {
        if (elapsed.TotalMinutes < 60) return $"{elapsed.TotalMinutes:F0} min ago";
        if (elapsed.TotalHours < 24) return $"{elapsed.TotalHours:F1} hours ago";
        return $"{elapsed.TotalDays:F0} days ago";
    }

    // ── Feature 14 v2: Outbound claim extraction (Apr 22, 2026) ─────────────

    /// <summary>
    /// Extracts claims about the contact from a composed outbound message, as a
    /// narrow list suitable for tier-provenance verification. This is the
    /// restoration of Feature 14 after its April ~10 removal, redesigned around
    /// the architecture-over-instruction discipline: the extractor is asked only
    /// to identify claims, never to judge them. Verification and the suppress-vs-
    /// dispatch decision happen outside the model, in ClaimVerificationPhase,
    /// against the existing tier architecture (Facts + anchored + inbound
    /// conversation_messages).
    ///
    /// Scope — what counts as a claim:
    ///   mark-action                    — Claims the contact DID something (sent X, brought Y, said Z)
    ///   mark-decision                  — Claims the contact DECIDED something
    ///   shared-event                   — Claims an event involving both parties happened
    ///   shared-event-with-attribution  — R1 Phase 1 (May 9, 2026): a more specific
    ///                                    sub-shape of shared-event where the claim
    ///                                    asserts a specific event with a specific
    ///                                    actor (X did/told/said Y, we did X with
    ///                                    canonical character Y). Cosine similarity
    ///                                    is NOT a valid oracle for this claim type;
    ///                                    event-shape match is required. See
    ///                                    ClaimVerificationPhase.IsClaimSupportedAsync
    ///                                    for the typed-verification flow.
    ///   shared-decision                — Claims a decision was made jointly
    ///   shared-presence                — Claims physical or relational co-presence
    ///
    /// Out of scope — what is explicitly NOT a claim for this extractor:
    ///   - Ani's own canonical world (bookstore, Wisconsin, shelving books, waiting for Mark).
    ///     Her World Layer is substrate, not claim. Extracting it would suppress her
    ///     from talking about her own life.
    ///   - Ani's feelings, inner thoughts, dreams, wishes. "I was thinking about you"
    ///     is not a claim about Mark; it's a statement about Ani.
    ///   - Literal descriptions of the message itself. "Just wanted to say hi" is not
    ///     a claim about Mark; it's a description of the send.
    ///
    /// Output schema: JSON with a "claims" array. Each claim has `text` (the phrase
    /// from the message), `type` (one of the six categories), `key_terms` (the
    /// specific entities or actions to corroborate against Facts/inbound), and an
    /// optional `event_actor` field populated for shared-event-with-attribution
    /// claims (the named actor performing the asserted action — e.g. "Mia",
    /// "Mark", "we"). If no claims about the contact appear, return {"claims": []}.
    /// `event_actor` is an additive nullable field — older parsers ignore it
    /// without breaking; new typed-verification code reads it when present.
    ///
    /// Notably absent from the prompt: any instruction about what to do with the
    /// claims, any judgment about whether they're true, any ask to regenerate or
    /// rewrite. The extractor only identifies. The architecture decides.
    /// </summary>
    public static (string System, string User) BuildClaimExtractionPrompt(
        string composedMessage, string contactName)
        => new Prompts.ClaimExtractionPromptCommand()
            .Build(new Prompts.ClaimExtractionPromptInput(composedMessage, contactName));

    // ── R1 Phase 1 (May 9, 2026): Typed event-shape verification prompt ────

    /// <summary>
    /// R1 Phase 1 (May 9, 2026): Strict prompt for verifying whether a
    /// candidate Mark-asserted record describes the same specific event as
    /// a <c>shared-event-with-attribution</c> claim.
    ///
    /// Motivating empirical case: May 9, 12:54 CDT outreach — *"Mia told us
    /// she picked out the tickets"*. Verifier's cosine search on the only
    /// Mark-asserted Mia message (*"...take Mia to school..."*) cleared
    /// threshold because both contain "Mia" and share register; verifier
    /// reported "supported"; message dispatched; Mark tagged confab. Cosine
    /// is necessary but not sufficient for shared-event claims that
    /// attribute action/speech to an actor — the oracle must be event-shape
    /// match.
    ///
    /// The strict reply contract is JSON-locked: <c>{"same_event": true|false}</c>.
    /// On parse failure, the caller defaults to <c>false</c> (fail-closed —
    /// unverified events are treated as unsupported, since the gate's job
    /// is to suppress confabulation, not to dispatch under uncertainty).
    /// </summary>
    public static (string System, string User) BuildEventVerificationPrompt(
        string claimText, string candidateRecordText, string contactName)
        => new Prompts.EventVerificationPromptCommand()
            .Build(new Prompts.EventVerificationPromptInput(claimText, candidateRecordText, contactName));

    // ── Feature 28: Dispatch coherence gate ─────────────────────────────────

    /// <summary>
    /// Three-door evaluation: given a composed outreach message, determine whether
    /// it should be dispatched or suppressed.
    ///
    /// Door A: Grounded reference — message references something real and specific → DISPATCH
    /// Door B: Standalone creative — message is creative/humorous but makes sense on its own → DISPATCH
    /// Door C: Only makes sense in Ani's head — inner thought leaked through → SUPPRESS
    /// </summary>
    public static (string System, string User) BuildCoherenceEvaluationPrompt(
        string composedMessage, string? innerThought, string contactName,
        DateTimeOffset? currentTime = null)
        => new Prompts.CoherenceEvaluationPromptCommand()
            .Build(new Prompts.CoherenceEvaluationPromptInput(composedMessage, innerThought, contactName, currentTime));

    /// <summary>
    /// Feature 32: Park et al.-inspired periodic reflection synthesis.
    /// Synthesizes recent memories into higher-order relational observations.
    /// </summary>
    public static (string System, string User) BuildReflectionSynthesisPrompt(
        string characterName, string contactName, IEnumerable<string> recentMemories)
        => new Prompts.ReflectionSynthesisPromptCommand()
            .Build(new Prompts.ReflectionSynthesisPromptInput(characterName, contactName, recentMemories));

    /// <summary>
    /// Formats a memory for prompt injection with felt-time temporal context
    /// AND a source-attribution tag.
    /// The timestamp is converted to natural language relative to now:
    /// "just now", "earlier today", "yesterday evening", "3 days ago".
    /// The source is derived from <see cref="MemoryRecord.Provenance"/> +
    /// <see cref="MemoryRecord.SourceName"/> (F-1 Phase 5 IRetrievalEnvelope).
    /// Together they give the model temporal awareness AND where each retrieved
    /// memory came from — so composers can distinguish, e.g., a stored Mark text
    /// from Ani's own prior thought without ambiguity.
    /// </summary>
    public static string FormatMemoryWithTime(MemoryRecord memory, DateTimeOffset? now = null, string? contactName = null)
    {
        var currentTime = now ?? DateTimeOffset.Now;
        var age = currentTime - memory.OccurredAt;
        var hour = memory.OccurredAt.Hour;

        var timeOfDay = hour switch
        {
            < 6 => "late night",
            < 9 => "early morning",
            < 12 => "morning",
            < 14 => "early afternoon",
            < 17 => "afternoon",
            < 20 => "evening",
            _ => "late evening",
        };

        string temporal;
        if (age.TotalMinutes < 30)
            temporal = "just now";
        else if (age.TotalHours < 1)
            temporal = "a little while ago";
        else if (age.TotalHours < 4 && memory.OccurredAt.Date == currentTime.Date)
            temporal = $"earlier this {timeOfDay}";
        else if (memory.OccurredAt.Date == currentTime.Date)
            temporal = $"this {timeOfDay}";
        else if (memory.OccurredAt.Date == currentTime.Date.AddDays(-1))
            temporal = $"yesterday {timeOfDay}";
        else if (age.TotalDays < 7)
            temporal = $"{(int)age.TotalDays} days ago";
        else if (age.TotalDays < 14)
            temporal = "last week";
        else
            temporal = $"{(int)(age.TotalDays / 7)} weeks ago";

        return $"[FROM: {FormatMemorySource(memory, contactName)}] ({temporal}) {memory.Content}";
    }

    /// <summary>
    /// F-1 Phase 5 (2026-08-18) — human-readable source attribution for a
    /// retrieved memory. Maps <see cref="MemoryRecord.Provenance"/> +
    /// <see cref="MemoryRecord.SourceName"/> + <see cref="MemoryRecord.Type"/>
    /// to a short phrase that reads naturally inside the <c>[FROM: ...]</c>
    /// tag prefixed by <see cref="FormatMemoryWithTime"/>.
    ///
    /// <para>
    /// Kept as a switch expression rather than a dictionary so the fallback
    /// arms are code-visible and the mapping is grep-able against the
    /// perception-source SourceName strings defined in
    /// <c>src/AniRuntime.Perception/*.cs</c>. New perception sources can add
    /// their SourceName here as they ship.
    /// </para>
    ///
    /// <para>
    /// <paramref name="contactName"/> (F-1 Phase 5 PR #115 fix): when supplied,
    /// twilio-inbound messages render as <c>text from {contactName}</c>. When
    /// null, they render as the neutral <c>inbound text</c>. Reviewer-caught
    /// (Devin) on the initial PR — the previous hardcoded "text from Mark"
    /// ignored <see cref="Models.CharacterStateDoc.PrimaryContactName"/>
    /// which is configurable at runtime.
    /// </para>
    /// </summary>
    public static string FormatMemorySource(MemoryRecord memory, string? contactName = null)
    {
        var src = memory.SourceName?.ToLowerInvariant();
        return (memory.Provenance, memory.Type, src) switch
        {
            // Character seeds are stored with SourceName="character-seed" (see
            // SourceNames.CharacterSeed + Program.cs seeding path). PR #115
            // review (Devin BUG) — the pre-fix mapping had the null and
            // character-seed arms swapped, so real seeds fell through to
            // "fact" and unlabeled Facts got labeled as "character seed."
            (EpistemicTier.Facts, _, SourceNames.CharacterSeed) => "character seed",
            (EpistemicTier.Facts, _, "rss")             => "news",
            (EpistemicTier.Facts, _, "twilio-inbound") when !string.IsNullOrEmpty(contactName)
                                                        => $"text from {contactName}",
            (EpistemicTier.Facts, _, "twilio-inbound")  => "inbound text",
            (EpistemicTier.Facts, _, "weather")         => "weather",
            (EpistemicTier.Facts, _, "time")            => "time-of-day",
            (EpistemicTier.Facts, _, "temporal-gap")    => "temporal gap",
            (EpistemicTier.Facts, _, "contact-state")   => "contact state",
            (EpistemicTier.Facts, _, "register-saturation") => "register saturation",
            (EpistemicTier.Facts, _, "retrieval-self-dominance") => "self-dominance signal",
            (EpistemicTier.Facts, _, "outage")          => "outage signal",
            (EpistemicTier.Facts, _, null)              => "fact",
            (EpistemicTier.Facts, _, _)                 => memory.SourceName!,
            (EpistemicTier.Interior, _, _)              => "your prior thought",
            (EpistemicTier.Episodic, MemoryType.InnerThought, _) => "your prior thought",
            (EpistemicTier.Episodic, _, _)              => "conversation",
            _                                            => "memory",
        };
    }
}
