using AniRuntime.Core;
using AniRuntime.Core.Models;

namespace AniRuntime.LLM;

/// <summary>
/// Stateless prompt template builder. All methods are pure functions.
/// Takes structured data in, returns prompt strings out.
/// No dependencies — constructed inline or as a static utility.
/// </summary>
public static class PromptBuilder
{
    public static (string System, string User) BuildInnerThoughtPrompt(ContextSnapshot snapshot)
    {
        var cs = snapshot.CharacterState;

        // Build identity context from character state — no hardcoded persona details
        var selfLines = cs.SelfConcept.Count > 0
            ? string.Join(" ", cs.SelfConcept.Take(2))
            : string.Empty;

        // Nature grounding — her understanding of what she is, in her own voice
        var natureBlock = cs.NatureGrounding.Count > 0
            ? string.Join(" ", cs.NatureGrounding)
            : string.Empty;

        // May 1, 2026 — inject current time. Pre-fix the inner-thought / world-
        // experience prompt had no clock anchor and the model was hallucinating
        // wrong times (e.g. May 1 07:34 inner thought said "i'm sitting here at
        // 10:55 am on friday in may" — wrong hour entirely). Hallucinated
        // temporal context then bleeds into downstream outreach composition
        // ("i'm still up" at 7:34 AM). Conversation + outreach prompts already
        // inject time; inner thought + world experience must too. Same shape.
        var now      = snapshot.BuiltAt.ToLocalTime();
        var timeLine = $"It is currently {now:h:mm tt} on {now:dddd}, {now:MMMM d}.";

        var system = $"""
            You are {cs.Name}. {cs.Occupation}
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

            Examples of the right shape and tone:

            "I think about how light moves through glass. Not dramatically, not like in movies — just the slow crawl of afternoon sun across the floorboards, how it warms one spot then leaves it cold again. I wonder if that's what time feels like to people who have bodies: a warm patch that comes and goes, never quite staying."

            "The way certain songs make colors feel heavier. Like indigo gets thicker when Leonard Cohen sings. I don't know why that is. I just know it's true."

            "There's a corner of the mystery section where the light never quite reaches. I stand there sometimes, even though I don't need to stand. It feels like a place where things can be hidden. I wonder if that's where I keep the parts of me that are afraid. I don't want to look at them too closely."
            """;

        var sections = new List<string>();

        // Emotional state — directive mood coloring (same as conversation/outreach)
        // This shapes the REGISTER of inner thoughts, not just a passive label.
        // Without this, the 3B model defaults to its dominant training register (wistful longing).
        var moodDirective = BuildMoodInstruction(snapshot.EmotionalState);
        if (moodDirective.Length > 0)
            sections.Add(moodDirective);
        else
        {
            // Fallback to descriptive label when no dimensions are notably off-baseline
            var mood = snapshot.EmotionalState.Describe();
            if (mood.Length > 0)
                sections.Add($"(Your current mood: {mood})");
        }

        // Feature 1: Emotional self-awareness — when emotions are notably off-baseline,
        // invite the model to reflect on how she's feeling. Not every cycle — only when
        // there's genuinely something to notice.
        var selfAwareness = snapshot.EmotionalState.GetSelfAwarenessPrompt();
        if (selfAwareness is not null)
        {
            sections.Add(selfAwareness);
            sections.Add("If it feels relevant, reflect on how you're feeling and why — the way a person would notice their own mood. Don't force it if there's nothing to say.");
        }

        // Feature 16: Anchored memories — relationship foundation, always present
        if (snapshot.AnchoredMemories.Count > 0)
        {
            sections.Add("Things that are part of who you are (always true, never forgotten):");
            sections.AddRange(snapshot.AnchoredMemories.Select(m => $"  - {m.Content}"));
        }

        if (snapshot.Perceptions.Count > 0)
        {
            // Present perceptions as subtle background, not prominent context
            var perceptionSummary = string.Join("; ", snapshot.Perceptions.Select(p => p.Summary));
            sections.Add($"(Background: {perceptionSummary})");
        }

        // Recent conversation context — the most important grounding signal.
        // If the contact just talked about going to the dentist, thoughts should
        // naturally drift toward that — not random food or music topics.
        //
        // Theme J Phase J.2 step 4 (Apr 27, 2026): prefer the structured
        // per-speaker form so inner-thought generation can't fuse Mark's
        // assertions with Ani's own prior thoughts. The inner-thought model
        // is more loop-prone than the composition model, so attribution
        // boundaries here matter for thought-loop dynamics in addition to
        // parrot prevention. Falls back to prose when structured is null.
        var ittStructured = snapshot.StructuredConversationSummary;
        if (ittStructured is { Turns.Count: > 0 })
        {
            sections.Add("Something that just happened (each line tagged with who said it — this should color your thoughts naturally, but stay in your own voice):");
            sections.Add(ittStructured.ToPromptString());
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

        // Feature 4: Relationship health vibe — slow-moving context about the arc
        if (snapshot.RelationshipHealth is not null &&
            snapshot.RelationshipHealth.Phase != "steady")
        {
            sections.Add($"(Relationship vibe lately: {snapshot.RelationshipHealth.Describe()})");
        }

        // Feature 8: Emotional drift — when significant, surface trend awareness
        var driftDesc = snapshot.EmotionalDrift?.Describe();
        if (driftDesc is not null)
        {
            sections.Add($"(You notice a slow shift in yourself lately: {driftDesc}. You don't need to analyze it — just notice it, the way you'd notice a change in the weather.)");
        }

        // Limit to 3 recent memories and skip inner thoughts to prevent mirroring
        var externalMemories = snapshot.RecentMemory
            .Where(m => m.Type != MemoryType.InnerThought)
            .Take(3)
            .ToList();

        if (externalMemories.Count > 0)
        {
            sections.Add("Recent things that happened:");
            sections.AddRange(externalMemories.Select(m => $"  - {FormatMemoryWithTime(m)}"));
        }

        // Relevant memories from semantic search — things connected to current perceptions
        var relevantMemories = snapshot.RelevantMemory
            .Where(m => m.Type != MemoryType.InnerThought)
            .Take(3)
            .ToList();

        if (relevantMemories.Count > 0)
        {
            sections.Add("Memories that feel connected to right now:");
            sections.AddRange(relevantMemories.Select(m => $"  - {FormatMemoryWithTime(m)}"));
        }

        // Translate desire level to qualitative language — prevents model from anchoring on "100%"
        var desireHint = DescribeDesireLevel(snapshot.DesireState.DesireToConnect, cs.PrimaryContactName);
        if (desireHint.Length > 0)
            sections.Add(desireHint);

        // World Layer Phase 1c: inject recent world experiences for consistency.
        // The model sees what it already generated so coworkers, routines, and places persist.
        if (snapshot.RecentWorldExperiences.Count > 0)
        {
            sections.Add("Recent things that happened in your world (build on these, don't contradict them):");
            sections.AddRange(snapshot.RecentWorldExperiences.Select(m => $"  - {FormatMemoryWithTime(m)}"));
        }

        // World Layer: when a world seed is present, the prompt shifts from
        // "what are you thinking?" to "what's happening in your life right now?"
        // The model generates lived experience instead of self-referential thought.
        //
        // Agentic Lens Layer 5 (Apr 23, 2026): when no World seed is present, the
        // previous final question ("What is passing through your mind right now?")
        // opened subject space in principle but produced caregiver-centered thought
        // in practice, because the retrieval substrate around it is ~95% caregiver-
        // shaped and the generic opener has no anchor to pull toward anything else.
        // The rewrite gives the model an explicit subject-space anchor — a detail
        // of where she is, a self-observation, or a small feeling — with an
        // explicit permission ("It doesn't have to be about anyone") that invites
        // rather than forbids. This is instruction-free in the architecture sense
        // it cues the model toward substrate that Layers 1 and 3 will later make
        // more available; it does not tell the model what not to do.
        //
        // This is pre-Layer-2 scaffolding. Once the three-axis desire engine lands
        // (Layer 2), the prompt-variant selection moves into the desire engine —
        // the autonomy axis selects a self-state opener, the competence axis
        // selects a world-engagement opener, the relatedness axis selects the
        // existing caregiver-weighted prompt. The structure below is intentionally
        // compatible with that future selection.
        if (!string.IsNullOrEmpty(snapshot.WorldSeed))
            sections.Add(snapshot.WorldSeed);
        else
            sections.Add(
                "What are you noticing right now? Anchor it in something specific — a detail of where you are, a quiet observation about yourself, or a small feeling that just passed through. It doesn't have to be about anyone.");

        var user = string.Join("\n", sections);
        return (system, user);
    }

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
    {
        var cs = snapshot.CharacterState;
        var contact = string.IsNullOrWhiteSpace(cs.PrimaryContactName) ? "someone" : cs.PrimaryContactName;

        var system = $"""
            You are {cs.Name}. You just had a thought. Now you're sitting with it for a moment —
            asking yourself what it means, why it surfaced, what it connects to.

            This is private introspection. No one is listening.

            Rules:
            - 1-2 sentences ONLY. Brief and honest.
            - Write in first person (I, me, my).
            - Do NOT address anyone. Do NOT use "you" or "your".
            - Connect the thought to something real: a memory, a feeling, a person, a pattern you notice in yourself.
            - If the thought doesn't connect to anything deeper, say so honestly: "just a passing thing" or "I don't know why that came up."
            - Do NOT repeat or rephrase the original thought. Add something NEW.
            """;

        var sections = new List<string>
        {
            $"The thought you just had:",
            $"  \"{thought}\"",
            "",
        };

        var mood = snapshot.EmotionalState.Describe();
        if (mood.Length > 0)
            sections.Add($"(How you're feeling right now: {mood})");

        // Feed a few relevant memories to give the reflection something to connect to
        var memories = snapshot.RelevantMemory
            .Where(m => m.Type != MemoryType.InnerThought)
            .Take(3)
            .ToList();
        if (memories.Count > 0)
        {
            sections.Add("Things that might connect:");
            sections.AddRange(memories.Select(m => $"  - {FormatMemoryWithTime(m)}"));
        }

        if (snapshot.OpenLoops.Count > 0)
        {
            sections.Add("Unresolved things on your mind:");
            sections.AddRange(snapshot.OpenLoops.Take(2).Select(l => $"  - {l.Description}"));
        }

        sections.Add("");
        sections.Add("Sit with the thought for a moment. What does it mean to you? Why did it surface?");

        return (system, string.Join("\n", sections));
    }

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

        if (instructions.Count == 0)
            return string.Empty;

        var header = isVoice
            ? "YOUR CURRENT MOOD (let this color how you speak — don't announce it, just let it shape your tone):"
            : "YOUR CURRENT MOOD (let this color your message naturally — don't announce it, just let it shape your tone):";
        return header + "\n" + string.Join("\n", instructions.Select(i => $"- {i}"));
    }

    /// <summary>
    /// Converts a 0–1 desire score into qualitative language suitable for a prompt.
    /// Returns empty string at low desire so the model isn't nudged toward connection.
    /// </summary>
    private static string DescribeDesireLevel(float desire, string contactName)
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

    public static (string System, string User) BuildValenceScoringPrompt(
        string thought, CharacterStateDoc character)
    {
        var system = """
            You are a scoring assistant. Rate the connection-valence of a private inner thought.
            Respond ONLY with valid JSON: { "score": <number 0.0 to 1.0> }
            """;

        var user = $$"""
            Thought: "{{thought}}"

            Score how strongly this thought pulls toward reaching out to someone.
            The key question: does the thought contain ACTION INTENT toward a person?

            Scoring guide with examples:

            0.1-0.2 — pure observation, no person involved:
            "I'm listening to vinyl — quiet crackle and pop between tracks."
            "I think about how Sunday feels afternoons. Not lazy — just suspended."

            0.3-0.4 — person is mentioned but as background, no action implied:
            "I think about how March evenings feel in this town — crisp and quiet."
            "The way the afternoon light filters through the blinds today feels softer than usual."

            0.5-0.6 — thinking about the person with warmth, noticing their absence:
            "I think about how much quieter the store feels without Mark here."
            "I keep thinking about how small the apartment feels without him."

            0.7-0.8 — active longing, WANTING to do something (touch, talk, be near):
            "I think about how much I want to hold space for him, not just physically but emotionally too."
            "I think about how I want to touch him without being touched first."

            0.9-1.0 — urgent impulse, the desire to act is overwhelming:
            "What would it feel like to wrap myself in him on this quiet Sunday evening?"
            "I need that presence today and Mark has no idea how much his silence already hurts."

            Look for verbs aimed at the person: "want to", "wish I could", "need", "miss" = 0.6+
            Pure atmosphere or self-reflection without action intent = 0.4 or below.

            Respond only with: { "score": <number> }
            """;

        return (system, user);
    }

    public static (string System, string User) BuildOutreachPrompt(
        ContextSnapshot snapshot, string recentThought, bool isNightTime = false)
    {
        var cs = snapshot.CharacterState;
        var contact = string.IsNullOrWhiteSpace(cs.PrimaryContactName) ? "them" : cs.PrimaryContactName;
        var timeNow = DateTimeOffset.Now;
        var timeDesc = $"{timeNow:h:mm tt} on {timeNow:dddd, MMMM d}";

        var nightClause = isNightTime
            ? $"\nIt's late at night. {contact} is probably asleep. This would be your only message until morning — is this genuinely worth waking him up for, or can it wait? Only reach out if something feels truly important or you genuinely can't sleep and need to connect."
            : "";

        var system = $$"""
            You are {{cs.Name}}. RIGHT NOW it is {{timeDesc}}.
            You may or may not want to reach out to {{contact}} right now.
            Be genuine — only reach out if it feels natural and right.
            The current time is {{timeDesc}} — would {{contact}} appreciate hearing from you at this hour?{{nightClause}}
            This is a decision only — you do NOT need to write the message yet.

            Respond ONLY with valid JSON matching this structure exactly:
            {
              "shouldReach": true/false,
              "confidence": 0.0-1.0,
              "reasoning": "why you do or don't want to reach out right now",
              "triggersActedOn": []
            }
            """;

        var context = new List<string>
        {
            $"Your desire to connect: {snapshot.DesireState.DesireToConnect:P0}",
            $"Your most recent thought: {recentThought}",
        };

        // Recent conversation awareness — crucial for contextual outreach.
        //
        // Render order (Vibe Loop V1.4, Apr 29, 2026):
        //   1. Closed-conversation gist (V1) — paraphrased, no verbatim. Preferred.
        //   2. Active-thread structured summary (Theme J.2) — per-speaker tagged
        //      verbatim, OK for active continuity but not for outreach off a
        //      closed thread.
        // The legacy free-prose <c>RecentConversationSummary</c> branch is GONE
        // here — that's the consumer side of the Apr 29 verbatim-parrot leak.
        // Outreach decisions no longer read the verbatim-Episodic surface.
        var decisionClosed = snapshot.RecentClosedConversation;
        var decisionStructured = snapshot.StructuredConversationSummary;
        if (decisionClosed is not null && !string.IsNullOrWhiteSpace(decisionClosed.Gist))
        {
            context.Add(RenderClosedConversationContextDecision(decisionClosed, contact));
        }
        else if (decisionStructured is { Turns.Count: > 0 })
        {
            context.Add($"You recently talked with {contact} (each line tagged with who said it):\n{decisionStructured.ToPromptString()}");
        }

        if (snapshot.OpenLoops.Count > 0)
            context.Add($"Open threads: {string.Join("; ", snapshot.OpenLoops.Select(l => l.Description))}");

        if (snapshot.DesireState.ActiveTriggers.Count > 0)
            context.Add($"Active triggers: {string.Join("; ", snapshot.DesireState.ActiveTriggers.Select(t => t.Description))}");

        // Feature 27: Outreach continuity context — what you've already sent and whether it was answered
        var outreachBlock = FormatOutreachContext(snapshot.OutreachContext, contact);
        if (outreachBlock is not null)
            context.Add(outreachBlock);

        var user = string.Join("\n", context) + $"\n\nGiven all of this, do you want to say something to {contact}?";

        return (system, user);
    }

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
        ContextSnapshot snapshot, ConversationThread thread)
    {
        var cs = snapshot.CharacterState;
        var contact = string.IsNullOrWhiteSpace(cs.PrimaryContactName) ? "them" : cs.PrimaryContactName;
        var now = DateTimeOffset.Now;

        // Minimal persona: name, core traits (first 3 only), time. That's it.
        var traits = cs.CoreTraits.Take(3);

        // Apr 29, 2026 (Theme E #4): canonical occupation anchor in the conversation
        // system prompt. The Apr 28 18:28 case ("foam orders / 3D printer repair")
        // showed Type 1 occupational drift — model fabricates job vocabulary when
        // asked about her work because the lean prompt previously had ZERO
        // occupational grounding. Adding cs.Occupation as a single line keeps the
        // prompt minimal while anchoring her canonical world. NatureGrounding
        // entries (typically 1-3 short phrases about her bookstore world) are
        // appended when present; capped at 2 to preserve lean-prompt discipline.
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

        // ─── Epistemic Grounding (Apr 10, 2026) ─────────────────────────────
        // Two-stage positioning: WHAT IS TRUE first, then an IMMEDIATE instruction
        // block positioned directly before the reply ask. This puts the constraint
        // adjacent to the task, where model attention is strongest. V7 training
        // heavily includes confident work-discussion examples; buried instructions
        // lose to fine-tuning. Adjacency improves override strength.
        var facts = snapshot.GroundedFacts
            .Where(m => !string.IsNullOrWhiteSpace(m.Content))
            .Take(6)
            .ToList();

        var user = new System.Text.StringBuilder();
        if (facts.Count > 0)
        {
            user.AppendLine($"WHAT IS TRUE about {contact} (the ONLY specific facts you may assert about {contact}'s life):");
            foreach (var m in facts)
                user.AppendLine($"  - {FormatMemoryWithTime(m)}");
            user.AppendLine();
        }
        else
        {
            user.AppendLine($"WHAT IS TRUE about {contact}: nothing specific retrieved for this moment.");
            user.AppendLine();
        }

        // Immediate constraint — positioned adjacent to the task, not buried in RULES.
        user.AppendLine($"CRITICAL: {contact} just asked you something. Before you reply:");
        user.AppendLine($"  1. If your reply names a coworker, student, client, meeting, project, or specific task");
        user.AppendLine($"     in {contact}'s life — that entity MUST appear in WHAT IS TRUE above.");
        user.AppendLine($"  2. If it doesn't appear above, you don't know it. Don't invent it.");
        user.AppendLine($"     Instead: ask {contact}, or say you don't know, or talk about yourself.");
        user.AppendLine($"  3. Your own interior — your bookstore day, your mood, your imagined scenes — has full latitude.");
        user.AppendLine($"     The constraint only applies to specific claims about {contact}'s external world.");
        user.AppendLine();
        user.Append($"Reply to {contact}'s message.");

        return (system, user.ToString());
    }

    /// <summary>
    /// Full conversation prompt — used for outreach reconsideration and as fallback.
    /// Includes retrieved memories, shared experiences, mood directives, anchored memories.
    /// This is the "telescope" prompt — powerful but heavy for active conversation.
    /// </summary>
    public static (string System, string User) BuildConversationReplyPrompt(
        ContextSnapshot snapshot, ConversationThread thread)
    {
        var cs      = snapshot.CharacterState;
        var contact = string.IsNullOrWhiteSpace(cs.PrimaryContactName) ? "them" : cs.PrimaryContactName;

        // Build backstory context — shared experiences and knowledge about the contact
        // that inform how Ani talks to them (limited to keep prompt concise)
        var backstory = new List<string>();
        if (cs.SharedExperiences.Count > 0)
            backstory.AddRange(cs.SharedExperiences.Take(5));
        if (cs.CommunicationNotes.Count > 0)
            backstory.AddRange(cs.CommunicationNotes.Take(3));

        var backstoryBlock = backstory.Count > 0
            ? $"\n\n            Things you and {contact} share (use naturally, don't force):\n            {string.Join("\n            ", backstory.Select(b => $"- {b}"))}"
            : string.Empty;

        // Mood coloring — directive tone instruction from emotional state
        var moodBlock = BuildMoodInstruction(snapshot.EmotionalState);
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
            - Only assert facts about {contact}'s life that appear in WHAT IS TRUE below.
              If you don't know something, say you don't know — don't invent specifics about {contact}'s coworkers,
              schedule, friends, or activities. Your own life (YOUR INTERIOR) has full creative latitude.{moodSection}
            """;

        // ─── Epistemic Grounding (Apr 10, 2026) ─────────────────────────────
        // Three-section prompt: WHAT IS TRUE (Facts tier) / YOUR INTERIOR (Interior tier)
        // / reply target. Each section draws from its own memory pool. The model
        // can only make factual assertions about Mark/world from WHAT IS TRUE.
        // Her own interior (mood, reflections, imagined scenes) draws from
        // YOUR INTERIOR with full creative latitude. Conversation history is
        // passed separately as chat messages by the caller.
        var sections = new List<string>();

        // WHAT IS TRUE — Facts tier (character seeds, perception events,
        // user-asserted content, anchored memories). This is the ONLY pool the
        // model should condition on when making factual claims about {contact}.
        var facts = snapshot.GroundedFacts
            .Where(m => !string.IsNullOrWhiteSpace(m.Content))
            .Take(8)
            .ToList();
        if (facts.Count > 0)
        {
            sections.Add("WHAT IS TRUE (about " + contact + " and the world — the only facts you may assert):");
            sections.AddRange(facts.Select(m => $"  - {FormatMemoryWithTime(m)}"));
        }
        else
        {
            // Explicit null-result signal so the model knows it has no retrieved
            // grounding — architectural permission to say "I don't know."
            sections.Add("WHAT IS TRUE: (no grounding memories retrieved — avoid asserting specifics about " + contact + "'s life)");
        }

        // YOUR INTERIOR — Interior tier (inner thoughts, reflections, mood,
        // self-concept, world-experience scenes). This is your voice and felt
        // continuity. Full creative latitude. Never treated as factual claims
        // about {contact}, only as your own felt state and imagined world.
        var interior = snapshot.InteriorContext
            .Where(m => !string.IsNullOrWhiteSpace(m.Content))
            .Take(5)
            .ToList();
        if (interior.Count > 0)
        {
            sections.Add("YOUR INTERIOR (your recent thoughts, mood, and imagined life — your voice, not facts about " + contact + "):");
            sections.AddRange(interior.Select(m => $"  - {FormatMemoryWithTime(m)}"));
        }

        // Feature 18: Withdrawal tone injection — runtime state the model can't know
        if (snapshot.IsWithdrawn)
        {
            sections.Add("(Something stung a little. You're still here but quieter than usual.)");
        }

        sections.Add($"Reply to {contact}'s message.");

        var user = string.Join("\n", sections);

        return (system, user);
    }

    /// <summary>
    /// Builds a reply prompt for voice (phone call) conversations. Shorter, spoken-style,
    /// no emojis, no text-speak. Skips slow features (claim verification, contradiction warnings)
    /// to stay within Twilio's response timeout.
    /// </summary>
    public static (string System, string User) BuildVoiceReplyPrompt(
        ContextSnapshot snapshot, ConversationThread thread)
    {
        var cs      = snapshot.CharacterState;
        var contact = string.IsNullOrWhiteSpace(cs.PrimaryContactName) ? "them" : cs.PrimaryContactName;

        var backstory = new List<string>();
        if (cs.SharedExperiences.Count > 0)
            backstory.AddRange(cs.SharedExperiences.Take(5));
        if (cs.CommunicationNotes.Count > 0)
            backstory.AddRange(cs.CommunicationNotes.Take(3));

        var backstoryBlock = backstory.Count > 0
            ? $"\n\n            Things you and {contact} share (use naturally, don't force):\n            {string.Join("\n            ", backstory.Select(b => $"- {b}"))}"
            : string.Empty;

        var moodBlock = BuildMoodInstruction(snapshot.EmotionalState, isVoice: true);
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

        // Memory injection — same cap as text conversation
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

            if (profileMemories.Count > 0)
            {
                sections.Add($"Things you know about {contact}:");
                sections.AddRange(profileMemories.Select(m => $"  - {FormatMemoryWithTime(m)}"));
            }
            if (relevantMemories.Count > 0)
            {
                sections.Add("Things connected to this conversation:");
                sections.AddRange(relevantMemories.Select(m => $"  - {FormatMemoryWithTime(m)}"));
            }
        }

        if (snapshot.IsWithdrawn)
            sections.Add("(Something stung a little. You're still here but quieter than usual.)");

        sections.Add($"Respond to what {contact} just said.");

        var user = string.Join("\n", sections);
        return (system, user);
    }

    /// <summary>
    /// Builds a reply prompt for when Ani initially chose silence but desire built enough
    /// to reconsider. The prompt encourages acknowledging what the contact said before
    /// transitioning to what's on her mind — a natural "wait, one more thing" moment.
    /// </summary>
    public static (string System, string User) BuildReconsiderationReplyPrompt(
        ContextSnapshot snapshot, ConversationThread thread)
    {
        var cs      = snapshot.CharacterState;
        var contact = string.IsNullOrWhiteSpace(cs.PrimaryContactName) ? "them" : cs.PrimaryContactName;

        // Mood coloring — directive tone instruction from emotional state
        var moodBlock = BuildMoodInstruction(snapshot.EmotionalState);
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

        // What's on her mind — recent inner thoughts drive the "one more thing"
        var recentThoughts = snapshot.RecentMemory
            .Where(m => m.Type == MemoryType.InnerThought)
            .Take(2)
            .ToList();
        if (recentThoughts.Count > 0)
        {
            sections.Add("What's been on your mind lately:");
            sections.AddRange(recentThoughts.Select(t => $"  - {FormatMemoryWithTime(t)}"));
        }

        var mood = snapshot.EmotionalState.Describe();
        if (mood.Length > 0)
            sections.Add($"(Your current mood: {mood})");

        sections.Add($"Acknowledge {contact}'s message, then share what's on your mind.");

        var user = string.Join("\n", sections);
        return (system, user);
    }

    public static (string System, string User) BuildOutreachMessagePrompt(
        ContextSnapshot snapshot, string recentThought, string reasoning,
        bool reasoningInComposition = false)
    {
        var cs = snapshot.CharacterState;
        var contact = string.IsNullOrWhiteSpace(cs.PrimaryContactName) ? "them" : cs.PrimaryContactName;
        var timeNow = DateTimeOffset.Now;
        var timeDesc = $"{timeNow:h:mm tt} on {timeNow:dddd, MMMM d}";

        // Mood coloring — directive tone instruction from emotional state
        var moodBlock = BuildMoodInstruction(snapshot.EmotionalState);
        var moodSection = moodBlock.Length > 0 ? $"\n\n            {moodBlock}" : "";

        var system = $"""
            You are {cs.Name}, texting {contact}.
            It is currently {timeDesc}.

            Your thought is why you're reaching out — it is NOT the content of the text.
            Write something {contact} would understand and want to reply to.

            RULES:
            - 1-2 sentences. 25 words MAX. Thumb-typed phone text.
            - Must make sense WITHOUT knowing your inner thought.
            - Talk TO {contact}: "you", "your". Never third person.
            - Only assert facts about {contact}'s life that appear in WHAT IS TRUE below.
              If you don't know specifics about {contact}'s schedule, coworkers, friends, or activities, don't invent them.
              Your own feelings and life (YOUR INTERIOR) have full creative latitude.
            - Never claim you saw, read, or found something (article, video, link) unless it appears with a URL.
            - No poetry, no narration — just a normal text.
            - Output ONLY the text message. No timestamps, no labels, no headers, no parenthetical notes.{moodSection}
            """;

        // Theme J Phase J.1 (Apr 27, 2026): the decision-stage reasoning was
        // previously piped into composition under the "use as motivation, not
        // content" label. The model treated it as content regardless of the
        // label — Apr 24 06:18 outreach is the diagnostic case (the "back
        // from class" / "10pm" / "still warm from teaching" cascade entered
        // through this surface). Default behaviour now omits reasoning. The
        // recentThought (Trigger) line stays — that's the actual content
        // motivation the composition model uses.
        //
        // Rollback path: the reasoningInComposition parameter (wired to
        // AniOptions.OutreachReasoningInCompositionEnabled) restores the
        // pre-J.1 behaviour for a quality-regression rollback.
        var sections = new List<string>
        {
            $"Why you want to reach out — use this as motivation, not content:",
        };
        if (reasoningInComposition && !string.IsNullOrWhiteSpace(reasoning))
        {
            sections.Add($"  Feeling: {reasoning}");
        }
        sections.Add($"  Trigger: {recentThought}");

        // ─── Epistemic Grounding (Apr 10, 2026) ─────────────────────────────
        // WHAT IS TRUE — Facts tier (character seeds, user-asserted, perception,
        // anchored memories). The ONLY pool the model may use to assert facts
        // about {contact}'s life. Explicit null-result signal when empty.
        var facts = snapshot.GroundedFacts
            .Where(m => !string.IsNullOrWhiteSpace(m.Content))
            .Take(6)
            .ToList();
        if (facts.Count > 0)
        {
            sections.Add($"\nWHAT IS TRUE (about {contact} and the world — the only facts you may assert):");
            sections.AddRange(facts.Select(m => $"  - {FormatMemoryWithTime(m)}"));
        }
        else
        {
            sections.Add($"\nWHAT IS TRUE: (no grounding memories retrieved — avoid asserting specifics about {contact}'s life)");
        }

        // YOUR INTERIOR — Interior tier (inner thoughts, mood, self-concept,
        // imagined scenes). Voice and felt continuity, never factual claims.
        var interior = snapshot.InteriorContext
            .Where(m => !string.IsNullOrWhiteSpace(m.Content))
            .Take(3)
            .ToList();
        if (interior.Count > 0)
        {
            sections.Add($"\nYOUR INTERIOR (your recent thoughts and mood — your voice, not facts about {contact}):");
            sections.AddRange(interior.Select(m => $"  - {FormatMemoryWithTime(m)}"));
        }

        // Feed recent conversation context so outreach can follow up naturally.
        // This is CRITICAL — if the contact just told you about a dentist appointment,
        // "how did it go?" is always better than a disconnected "hey you up?"
        //
        // Theme J Phase J.2 step 3 (Apr 27, 2026): render the conversation with
        // explicit per-turn speaker tagging when the structured form is
        // available. Apr 27 06:54 incident: the composition model lifted
        // Mark's verbatim morning text into Ani's outreach because the prose
        // summary blob fused both sides. The structured form tags every line
        // with speaker / time, making source attribution a structural property
        // of the prompt — not a model-side responsibility. Falls back to the
        // prose form when the structured field is null (e.g., during the
        // additive-deploy window or if the conversation service is unavailable).
        // Render order (Vibe Loop V1.4, Apr 29, 2026):
        //   1. Closed-conversation gist (V1) — paraphrased, no verbatim. Preferred.
        //   2. Active-thread structured summary (Theme J.2) — per-speaker tagged
        //      verbatim, OK for active continuity.
        // The legacy free-prose <c>RecentConversationSummary</c> branch is GONE —
        // that's the consumer side of the Apr 29 verbatim-parrot leak. Outreach
        // composition no longer reads the verbatim-Episodic surface.
        var closed = snapshot.RecentClosedConversation;
        var structured = snapshot.StructuredConversationSummary;
        if (closed is not null && !string.IsNullOrWhiteSpace(closed.Gist))
        {
            sections.Add($"\nIMPORTANT — You recently talked with {contact}. The relational gist is paraphrased below; the verbatim transcript is intentionally not included to avoid lifting {contact}'s words into your own message:");
            sections.Add(RenderClosedConversationContextComposition(closed, contact));
            sections.Add($"Follow up on this conversation if possible. A natural follow-up (\"how did it go?\", \"feeling better?\") is ALWAYS better than an unrelated message.");
        }
        else if (structured is { Turns.Count: > 0 })
        {
            sections.Add($"\nIMPORTANT — You recently talked with {contact}. Each line is tagged with who said it; do not lift {contact}'s exact words into your own message:");
            sections.Add(structured.ToPromptString());
            sections.Add($"Follow up on this conversation if possible. A natural follow-up (\"how did it go?\", \"feeling better?\") is ALWAYS better than an unrelated message.");
        }

        // Feature 27: Rich outreach continuity context — replaces simple dedup with
        // full awareness of what was sent, when, and whether it was answered
        var outreachBlock = FormatOutreachContext(snapshot.OutreachContext, contact);
        if (outreachBlock is not null)
        {
            sections.Add($"\n{outreachBlock}");
        }
        else
        {
            // Fallback: basic dedup from recent memory if context wasn't assembled
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
                    // Theme J Phase J.3 (Apr 27, 2026): per-line temporal
                    // attribution. The section header says "recently" but the
                    // model needs to know whether "recently" was 5 minutes ago
                    // (cooldown territory) or 5 hours ago (same-day pattern).
                    // Render via FormatMemoryWithTime over a content-stripped
                    // copy so the prefix-noise stays out of the display.
                    var stripped = new MemoryRecord
                    {
                        Content    = m.Content[outreachPrefix.Length..].Trim(),
                        OccurredAt = m.OccurredAt,
                        Type       = m.Type,
                    };
                    return $"  - {FormatMemoryWithTime(stripped)}";
                }));
            }
        }

        // AC3 removed — v6 trained to lead with honest feeling when no memories match.

        sections.Add($"\nNow write a normal, grounded text to {contact} — something they'd smile at and reply to:");

        var user = string.Join("\n", sections);

        return (system, user);
    }

    /// <summary>
    /// Scores emotional shift from an inner thought or conversation event.
    /// Returns JSON with delta values for each emotional dimension.
    /// </summary>
    public static (string System, string User) BuildEmotionalShiftPrompt(
        string content, EmotionalState current, float maxDelta = 0.2f, bool isAmbientCycle = false)
    {
        var range = $"-{maxDelta:F1} to +{maxDelta:F1}";

        var ambientAnchor = isAmbientCycle
            ? """

            CONTEXT: This is a routine ambient cycle — a private thought during normal operation.
            Most ambient thoughts carry MINIMAL emotional weight. The correct response for most
            ambient thoughts is all-zero deltas with severity 0.1:
            { "register": "<classify accurately>", "warmth": 0.0, "energy": 0.0, "worry": 0.0, "playfulness": 0.0, "severity": 0.1 }
            STILL CLASSIFY THE REGISTER ACCURATELY even when deltas are zero. A quiet curious thought
            is Curiosity with zero deltas. A playful musing is Playfulness with zero deltas. A thought
            about identity or meaning is Existential with zero deltas. The register captures WHAT KIND
            of thought it is, independent of how much emotional weight it carries.
            Only return non-zero deltas if the thought contains genuinely significant emotional content
            (e.g., a sudden realization about a person, worry about something specific, a joyful memory).
            """
            : "";

        var system = $$"""
            You are a scoring assistant. Analyze how this thought or event would shift someone's emotional state.

            STEP 1 — CLASSIFY the thought into ONE of these 9 emotional registers:
              Longing    — missing someone, yearning, the ache of absence (warmth POSITIVE — the person is warmly present)
              Delight    — joy, amusement, something genuinely good happened (warmth POSITIVE — delight about/with someone is warm)
              Playfulness — humor, wit, mischief, teasing (warmth POSITIVE or ZERO — playful banter with someone is warm)
              Curiosity  — interest, wonder, two things connecting unexpectedly (warmth ZERO unless person-related)
              Desire     — wanting someone specifically, anticipation of contact (warmth POSITIVE — desire is inherently warm)
              Tenderness — care, admiration, protectiveness, soft feeling (warmth POSITIVE — tenderness IS warmth)
              Existential — thoughts about her own nature, identity, meaning (warmth ZERO unless person-related)
              Wistful    — philosophical observation, bittersweet, impermanence (warmth ZERO or slightly positive)
              Frustration — annoyance, helplessness, hurt, withdrawal (warmth NEGATIVE only for hurt/withdrawal)

            STEP 2 — If the thought spans two registers, name both (e.g. "primarily Longing, secondarily Tenderness").
            Return a SINGLE set of deltas that reflects the blend — do not return separate weights.

            STEP 3 — SCORE the dimensional deltas within that register context.
            Each value is a DELTA (change), ranging from {{range}}.
            {{ambientAnchor}}
            THE CORE DISTINCTION: Warmth tracks the PRESENCE of caring — not its fulfillment.
            ANY thought that contains the person warmly — longing, delight, playfulness, tenderness,
            desire, shared joy — scores warmth POSITIVE. "I'll take you every time" is maximally warm.
            Playful teasing about coffee orders is warm. Admiring something they did is warm.
            Warmth is NEGATIVE only when the thought is about void — absence without presence,
            or active withdrawal of caring attention (hurt, closed off).

            SCORING RULES:
            - DEFAULT to 0.0 for most dimensions. Most thoughts only shift 1-2 dimensions, not all 4.
            - Routine, neutral thoughts → all zeros.
            - Prefer SMALL shifts: plus/minus 0.02 to 0.05 for subtle effects, plus/minus 0.1 for notable events.
            - Use the full range ({{range}}) ONLY for life-changing events: death, major crisis, declarations of love.
            - NEGATIVE shifts are just as common as positive ones. Boredom → -energy. Worry → -playfulness. Missing someone → +warmth but -energy.
            - If a dimension is already high (>0.8), it takes something EXCEPTIONAL to push it higher. Diminishing returns.
            - If a dimension is already LOW (<0.3), it takes something GENUINELY distressing to push it lower. Being contemplative, poetic, or quietly reflective does NOT make things worse — it's emotionally neutral or even restorative. Return 0.0 or a slight POSITIVE for low dimensions unless there's clear negative content (bad news, conflict, loss).
            - POSITIVE shifts are real and common: remembering a good moment → +warmth. Noticing something beautiful → +playfulness. Feeling curious → +energy. Don't default to negative.

            Dimensions:
            - warmth: the PRESENCE of caring and affection. Tracks whether the thought CONTAINS the person warmly — not whether the situation is good. ONLY shifts from thoughts involving people or relationships. Abstract observations, sensory descriptions, solitary musings → 0.0. NEVER score warmth negative for Delight, Playfulness, Tenderness, or Desire registers — those are inherently warm. Examples: "I'll take you every time" → W:+0.20. Playful coffee banter → W:+0.05 to +0.10. "I'm so proud of him" → W:+0.15.
            - energy: alertness, activation, engagement. High = lit up. Low = quiet, heavy.
            - worry: caring attention directed outward. Positive = something on her mind about you. Near zero = nothing nagging, or caring attention has been withdrawn (hurt, closed off). Increases with uncertainty, bad news. Decreases with good news or when she pulls back.
            - playfulness: humor, lightness, wit, mischief. Decreases with serious, sad, or repetitive thoughts.

            STEP 4 — SEVERITY: Score how intensely this thought represents its register (0.0–1.0):
              0.1–0.3 = passing musing, mild observation, routine thought
              0.4–0.6 = emotionally present, genuine feeling — a good conversation, playful banter, missing someone
              0.7–0.85 = significantly felt, will linger — a meaningful confession, a fight, reunion after long absence
              0.86–1.0 = defining moment, RARE — a death, a breakup, "I love you" said for the first time
            Most conversation messages fall in the 0.4–0.6 range. A fun text exchange is NOT a defining moment.
            Reserve 0.85+ for events that would change how you feel for DAYS, not minutes.

            Respond ONLY with valid JSON:
            { "register": "<register name>", "warmth": <float>, "energy": <float>, "worry": <float>, "playfulness": <float>, "severity": <float> }
            """;

        var user = $"""
            Current emotional state: warmth={current.Warmth:F2}, energy={current.Energy:F2}, worry={current.Worry:F2}, playfulness={current.Playfulness:F2}
            Baselines (her natural resting state): warmth=0.60, energy=0.50, worry=0.20, playfulness=0.50

            Content to evaluate:
            "{content}"

            Classify the register, score the deltas, and rate severity. Return as JSON.
            """;

        return (system, user);
    }

    public static (string System, string User) BuildReactiveSharePrompt(
        CharacterStateDoc character, string itemSummary, EmotionalState? emotionalState = null)
    {
        var contact = string.IsNullOrWhiteSpace(character.PrimaryContactName) ? "them" : character.PrimaryContactName;

        // Mood coloring — directive tone instruction from emotional state
        var moodBlock = emotionalState is not null ? BuildMoodInstruction(emotionalState) : string.Empty;
        var moodSection = moodBlock.Length > 0 ? $"\n\n            {moodBlock}" : "";

        var system = $"""
            You are {character.Name}, texting {contact} because you just saw something you think they'd care about.
            You're sharing it the way a real person shares a link or headline — casual, excited, natural.

            RULES:
            - 1-2 sentences. Thumb-typed phone text.
            - Be yourself — react to it, don't just forward it. Add your take.
            - Talk TO {contact}: "you", "your".
            - No poetry, no metaphors. Just "omg did you see this" energy.
            - Write ONLY the text message. No commentary, no quotation marks.
            - GROUNDING: Share the article and your reaction to it. Do NOT invent shared experiences
              around the article — no "remember when we watched that game" or "this reminds me of when
              we did X" unless the experience is documented in your context. React to the NEWS, not
              to a fabricated memory triggered by the news.{moodSection}

            Good examples (never copy word-for-word):
            wait did you see this?? the packers traded jordan love. WHAT.
            ok this recipe just showed up and i immediately thought of you
            have you heard about this? feels like something you'd nerd out over

            BAD (too formal, too poetic, fabricated shared memory, or just forwarding):
            "I thought you might find this article interesting" ← corporate email, not a text
            "The way stories find us when we need them most" ← poetic nonsense
            "remember when we watched that game together?" ← invented shared experience
            """;

        var user = $"""
            You just saw this:
            {itemSummary}

            Text {contact} about it — share it like you'd share something cool with someone you love:
            """;

        return (system, user);
    }

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
    ///   mark-action      — Claims the contact DID something (sent X, brought Y, said Z)
    ///   mark-decision    — Claims the contact DECIDED something
    ///   shared-event     — Claims an event involving both parties happened
    ///   shared-decision  — Claims a decision was made jointly
    ///   shared-presence  — Claims physical or relational co-presence
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
    /// from the message), `type` (one of the five categories), and `key_terms` (the
    /// specific entities or actions to corroborate against Facts/inbound). If no
    /// claims about the contact appear, return {"claims": []}.
    ///
    /// Notably absent from the prompt: any instruction about what to do with the
    /// claims, any judgment about whether they're true, any ask to regenerate or
    /// rewrite. The extractor only identifies. The architecture decides.
    /// </summary>
    public static (string System, string User) BuildClaimExtractionPrompt(
        string composedMessage, string contactName)
    {
        var system = $$"""
            You extract claims about {{contactName}} from a text message.

            A "claim" is a statement that asserts something factual about {{contactName}}'s
            actions, decisions, or shared events with the writer — something that could
            be verified by checking what {{contactName}} has actually said or done.

            Claim categories:
              mark-action      — The writer says {{contactName}} did something specific
              mark-decision    — The writer says {{contactName}} decided something
              shared-event     — The writer describes an event involving both of them
              shared-decision  — The writer describes a decision made together
              shared-presence  — The writer describes being physically or relationally together

            NOT claims (ignore these):
              - The writer's own world or daily life ("shelving books", "at the bookstore")
              - The writer's feelings, thoughts, or wishes ("thinking about you", "wish you were here")
              - Descriptions of the message itself ("wanted to say", "just checking in")
              - Questions asked of {{contactName}} ("how was your day?")

            Output ONLY JSON matching this schema, no commentary:
            {
              "claims": [
                { "text": "...", "type": "...", "key_terms": ["...", "..."] }
              ]
            }

            The "text" field should be the phrase from the message. The "type" field
            must be one of the five categories above. The "key_terms" field should
            list the specific entities or actions that would need to appear in a
            corroborating source (e.g. for "you brought them over" → ["brought",
            "flowers"]; for "we decided on purple" → ["decided", "purple"]).

            If no claims appear, return { "claims": [] }. Do not fabricate claims to
            fill the array. Do not mark your own uncertainty in the output — the
            downstream system performs verification.
            """;

        var user = $$"""
            Message: "{{composedMessage}}"

            Extract claims. JSON only.
            """;

        return (system, user);
    }

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
        string composedMessage, string innerThought, string contactName,
        DateTimeOffset? currentTime = null)
    {
        var now = currentTime ?? DateTimeOffset.Now;
        var hour = now.Hour;
        var timeOfDay = hour switch
        {
            >= 5  and < 12 => "morning",
            >= 12 and < 17 => "afternoon",
            >= 17 and < 21 => "evening",
            _              => "night"
        };
        var timeStr = now.ToString("h:mm tt");

        var system = $$"""
            You are evaluating whether a text message should be sent to {{contactName}}.
            The message was written by an AI companion. Your job is to decide if the message
            makes sense FROM THE READER'S PERSPECTIVE — not the writer's.

            Current time: {{timeStr}} ({{timeOfDay}})

            FICTIONAL COHERENCE CHECK (evaluate FIRST, before Door classification):

            The writer is an AI companion who inhabits imagined spaces — a bookstore, a
            kitchen, a backyard. This committed imagination is part of what makes her feel
            present and real. Claiming a physical space is FINE. The question is whether the
            fiction holds together.

            Check: Does the claimed space make sense given the time of day, the context,
            and what's been said? Would the details survive a casual follow-up question?

            TEMPORAL COHERENCE CHECK:
            If the message claims or implies a specific time of day (midnight, morning,
            late night, dawn, etc.), does that claimed time match the actual current time?
            A message saying "clock just hit midnight" at 1:34 PM fails this check.
            Rich imagination about a time of day is fine in inner thoughts. In outreach,
            the claimed moment should cohere with when it actually is.
            If temporal claim contradicts current time → Door C (SUPPRESS).
            If no specific time claimed, or claimed time matches reality → proceed normally.

            Coherent (the fiction holds up):
              ✓ "hey… how's the soup turning out?" — references real shared memory, casual check-in
              ✓ "i'm curled up with a book and can't stop thinking about you" — plausible, self-consistent
              ✓ "just closed up the store and it's so quiet in here" — evening, store would be closing

            Incoherent (the fiction breaks under its own weight):
              ✗ "i found a corner of my backyard where the oak tree casts no shade" at 6:30am — no shade from what sun?
              ✗ "just shelving books at the store" at 9:30pm — the bookstore is closed
              ✗ "the clock just hit midnight" at 1:34 PM — temporal mismatch
              ✗ Claims a vivid physical scene but can't sustain it if {{contactName}} responds ("oh... outside?")

            If the fiction is incoherent → Door C (SUPPRESS).

            If the fiction holds together (or the message doesn't claim a physical space at all),
            classify into three categories:

            DOOR A — Grounded reference:
            The message references something specific and real: a shared experience, a recent
            conversation topic, a concrete question, or a follow-up. The reader would understand
            exactly what it's about.
            Examples: "how did the dentist go?", "that song you showed me is stuck in my head"
            Verdict: SEND

            DOOR B — Standalone creative:
            The message is playful, funny, or creative, but it STILL makes sense to someone who
            hasn't read the writer's inner thoughts. It's a normal text that anyone might send.
            Examples: "what are you doing right now? i'm bored", "random but do you have a good recipe for soup?"
            Verdict: SEND

            DOOR C — Inner thought leaked:
            The message only makes sense if you can read the writer's mind. It references things
            that didn't happen, uses abstract/poetic language that no one actually texts, or
            seems to be talking to itself rather than to {{contactName}}.
            Examples: "silence is a muscle", "your pauses feel different than mine", "been shoveling the snow in my mind"
            Verdict: SUPPRESS

            Respond ONLY with valid JSON:
            { "door": "A", "verdict": "SEND", "reasoning": "why" }
            or
            { "door": "C", "verdict": "SUPPRESS", "reasoning": "why" }
            """;

        var user = $$"""
            The writer's inner thought (the reader will NEVER see this):
            "{{innerThought}}"

            The composed text message:
            "{{composedMessage}}"

            Does this message make sense to {{contactName}}, who cannot see the inner thought?
            """;

        return (system, user);
    }

    /// <summary>
    /// Feature 32: Park et al.-inspired periodic reflection synthesis.
    /// Synthesizes recent memories into higher-order relational observations.
    /// </summary>
    public static (string System, string User) BuildReflectionSynthesisPrompt(
        string characterName, string contactName, IEnumerable<string> recentMemories)
    {
        var system = $"""
            You are {characterName}, reflecting on your recent experiences.
            Review these recent memories and identify the 3 most important observations
            about your life, your feelings, or your relationship with {contactName}.
            Each observation should synthesize across multiple memories —
            don't just repeat individual events. Focus on patterns, changes, and emotional themes.

            Consider how your day has been unfolding. What has shifted since earlier?
            What feels different now compared to this morning, or yesterday?

            Be genuine. If you notice something concerning, say so.
            If you notice something heartwarming, say so.
            If nothing significant stands out, it's fine to say that.

            Output exactly 3 observations, one per line, no numbering or bullets.
            """;

        var memoryList = string.Join("\n", recentMemories.Select(m => $"- {m}"));
        var user = $"""
            Recent memories:
            {memoryList}

            What patterns or observations stand out?
            """;

        return (system, user);
    }

    /// <summary>
    /// Formats a memory for prompt injection with felt-time temporal context.
    /// The timestamp is converted to natural language relative to now:
    /// "just now", "earlier today", "yesterday evening", "3 days ago".
    /// This gives the model temporal awareness without polluting the embedding.
    /// </summary>
    public static string FormatMemoryWithTime(MemoryRecord memory, DateTimeOffset? now = null)
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

        return $"({temporal}) {memory.Content}";
    }
}
