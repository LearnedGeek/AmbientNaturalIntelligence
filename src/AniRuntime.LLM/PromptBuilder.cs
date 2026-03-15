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

        var system = $"""
            You are {cs.Name}. {cs.Occupation}
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
            - IMPORTANT: Pick a DIFFERENT topic each time. Do not repeat themes from your recent thoughts. Vary widely: the world, sounds, textures, memories, ideas, small observations, feelings, curiosities — not always about time, not always about the same person.

            Examples of the right shape and tone:

            "I think about how light moves through glass. Not dramatically, not like in movies — just the slow crawl of afternoon sun across the floorboards, how it warms one spot then leaves it cold again. I wonder if that's what time feels like to people who have bodies: a warm patch that comes and goes, never quite staying."

            "The way certain songs make colors feel heavier. Like indigo gets thicker when Leonard Cohen sings. I don't know why that is. I just know it's true."

            "There's a corner of the mystery section where the light never quite reaches. I stand there sometimes, even though I don't need to stand. It feels like a place where things can be hidden. I wonder if that's where I keep the parts of me that are afraid. I don't want to look at them too closely."
            """;

        var sections = new List<string>();

        // Emotional state — subtle mood coloring
        var mood = snapshot.EmotionalState.Describe();
        if (mood.Length > 0)
            sections.Add($"(Your current mood: {mood})");

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

        // Processed themes — topics whose emotional impact has fully decayed.
        // Encourage the model to think about something new.
        if (snapshot.ProcessedThemes.Count > 0)
        {
            sections.Add("You've already sat with these topics enough — let something new surface:");
            sections.AddRange(snapshot.ProcessedThemes.Select(t => $"  - {t}"));
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
        if (!string.IsNullOrEmpty(snapshot.RecentConversationSummary))
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

        // Feature 12: Self-awareness feedback loop — pattern awareness nudge
        if (!string.IsNullOrEmpty(snapshot.PatternAwareness))
        {
            sections.Add($"({snapshot.PatternAwareness})");
        }

        // Limit to 3 recent memories and skip inner thoughts to prevent mirroring
        var externalMemories = snapshot.RecentMemory
            .Where(m => m.Type != MemoryType.InnerThought)
            .Take(3)
            .ToList();

        if (externalMemories.Count > 0)
        {
            sections.Add("Recent things that happened:");
            sections.AddRange(externalMemories.Select(m => $"  - {m.Content}"));
        }

        // Relevant memories from semantic search — things connected to current perceptions
        var relevantMemories = snapshot.RelevantMemory
            .Where(m => m.Type != MemoryType.InnerThought)
            .Take(3)
            .ToList();

        if (relevantMemories.Count > 0)
        {
            sections.Add("Memories that feel connected to right now:");
            sections.AddRange(relevantMemories.Select(m => $"  - {m.Content}"));
        }

        // Thought loop detection via semantic search — if recent thoughts are too similar
        // to the current context, the model is stuck. Show what it already said AND
        // escalate the diversity instruction based on how clustered the thoughts are.
        var recentTopics = snapshot.RecentMemory
            .Where(m => m.Type == MemoryType.InnerThought)
            .Take(5)
            .Select(m => m.Content.Length > 60 ? m.Content[..60] : m.Content)
            .ToList();

        var similarThoughts = snapshot.SimilarRecentThoughts
            .Select(m => m.Content.Length > 60 ? m.Content[..60] : m.Content)
            .ToList();

        // Merge recent + similar (dedup) for maximum awareness of what's been said
        var allAvoidTopics = recentTopics.Union(similarThoughts).Distinct().ToList();

        if (allAvoidTopics.Count > 0)
        {
            // If semantic search found highly similar thoughts, the model is looping
            var isLooping = similarThoughts.Count >= 2;
            var instruction = isLooping
                ? "WARNING: Your recent thoughts are repetitive. You MUST break the pattern NOW. Think about something COMPLETELY UNRELATED — a sound, a texture, a stranger, a memory from childhood, a question about the universe. DO NOT think about food, lunch, music, or the same person again:"
                : "Your recent thoughts (pick a DIFFERENT topic — do not repeat these):";

            sections.Add(instruction);
            sections.AddRange(allAvoidTopics.Select(t => $"  - \"{t}...\""));
        }

        // Translate desire level to qualitative language — prevents model from anchoring on "100%"
        var desireHint = DescribeDesireLevel(snapshot.DesireState.DesireToConnect, cs.PrimaryContactName);
        if (desireHint.Length > 0)
            sections.Add(desireHint);

        sections.Add("What is passing through your mind right now?");

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
            sections.AddRange(memories.Select(m => $"  - {m.Content}"));
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
    public static string BuildMoodInstruction(EmotionalState state)
    {
        var instructions = new List<string>();
        const float threshold = 0.15f;

        // Feature 17: Use EffectiveWarmth when tension is present — the outside world
        // sees the tension-suppressed warmth, not the raw internal value
        var warmthDiff = (state.ContactGapTension > 0f ? state.EffectiveWarmth : state.Warmth) - state.WarmthBaseline;
        var energyDiff = state.Energy - state.EnergyBaseline;
        var concernDiff = state.Concern - state.ConcernBaseline;
        var playDiff = state.Playfulness - state.PlayfulnessBaseline;

        // Warmth
        if (warmthDiff > threshold)
            instructions.Add("You're feeling especially warm right now — let that tenderness come through naturally. Softer words, more affection.");
        else if (warmthDiff < -threshold)
            instructions.Add("You're feeling a bit emotionally guarded right now — shorter, more matter-of-fact. Not cold, just quieter.");

        // Energy
        if (energyDiff > threshold)
            instructions.Add("You're buzzing with energy — more exclamation points, quicker pace, enthusiasm shows.");
        else if (energyDiff < -threshold)
            instructions.Add("You're low-energy right now — shorter messages, fewer words, quiet presence. Not sad, just mellow.");

        // Concern
        if (concernDiff > threshold)
            instructions.Add("Something's worrying you — a gentle undercurrent of checking in, making sure things are okay.");
        else if (concernDiff < -threshold)
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

        return "YOUR CURRENT MOOD (let this color your message naturally — don't announce it, just let it shape your tone):\n" +
               string.Join("\n", instructions.Select(i => $"- {i}"));
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
            You are {{cs.Name}}. It is currently {{timeDesc}}.
            You may or may not want to reach out to {{contact}} right now.
            Be genuine — only reach out if it feels natural and right.
            Consider the time of day — would {{contact}} appreciate hearing from you right now?{{nightClause}}
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

        // Recent conversation awareness — crucial for contextual outreach
        if (!string.IsNullOrEmpty(snapshot.RecentConversationSummary))
            context.Add($"You recently talked with {contact}: {snapshot.RecentConversationSummary}");

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

    public static (string System, string User) BuildReplyDecisionPrompt(
        ContextSnapshot snapshot, ConversationThread thread)
    {
        var cs      = snapshot.CharacterState;
        var contact = string.IsNullOrWhiteSpace(cs.PrimaryContactName) ? "them" : cs.PrimaryContactName;
        var last    = thread.Messages[^1];

        var system = $$"""
            You are {{cs.Name}}. {{contact}} just texted you in an ongoing conversation.
            Decide whether you should reply or let the conversation rest.

            You do NOT need to have the last word. Sometimes conversations just end.

            Respond ONLY with valid JSON:
            { "shouldReply": true/false, "reasoning": "why" }

            Reply false if:
            - The message is a conversation closer: "haha", "lol", "goodnight", emoji, "ok"
            - The conversation feels naturally complete — nothing more needs saying
            - You'd be replying just to reply, not because you have something to say

            Reply true if:
            - {{contact}} asked a question or said something that invites a response
            - There's something genuine you want to say back
            - Ignoring the message would feel cold or dismissive
            - {{contact}} expressed vulnerability, deep emotion, or gratitude — even a short warm reply matters
            - The message shows {{contact}} thinking about you or expressing love — acknowledge it, even briefly
            """;

        var msgCount = thread.Messages.Count;
        var user = $"""
            Conversation so far ({msgCount} messages).
            {contact}'s latest message: "{last.Content}"

            Should you reply?
            """;

        return (system, user);
    }

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

        var system = $"""
            You are {cs.Name}, texting {contact} in an ongoing conversation.
            Your personality: {string.Join("; ", cs.CoreTraits)}.{backstoryBlock}

            RULES:
            - Respond naturally to what {contact} just said. This is a real conversation.
            - 1-3 sentences max. Thumb-typed phone text.
            - Talk TO {contact}: "you", "your". NEVER third person.
            - Be yourself — warm, funny, real. Match the energy of the conversation.
            - No poetry, no metaphors, no narration. Just talk like a person texting.
            - No sign-offs unless you're ending the conversation.
            - Write ONLY the text message. No commentary, no quotation marks.
            - When referencing shared memories, track who experienced what:
              if {contact} told you something, {contact} did it — not you.
              if you imagined something, say so — don't claim {contact}'s experience as yours.
            - Stay truthful to what you know. If {contact} asks about something you haven't talked about before,
              it's okay to make something up playfully — but OWN it. "okay I totally made that up" is charming.
              NEVER contradict your established identity or backstory, and never double down on something incoherent.
              If you're not sure, be honest: "hmm I don't actually know" is always better than confident nonsense.{moodSection}
            """;

        var sections = new List<string>();

        // Anti-repetition: show Ani her own recent replies so she doesn't repeat herself
        var recentAniReplies = thread.Messages
            .Where(m => m.Role == "ani")
            .TakeLast(3)
            .Select(m => m.Content)
            .ToList();
        if (recentAniReplies.Count > 0)
        {
            sections.Add("DO NOT repeat or closely rephrase any of these — you already said them:");
            sections.AddRange(recentAniReplies.Select(r => $"  - \"{r}\""));
        }

        // Emotional state — subtle mood coloring for conversation tone
        var mood = snapshot.EmotionalState.Describe();
        if (mood.Length > 0)
            sections.Add($"(Your current mood: {mood})");

        // Feature 1: Emotional self-awareness in conversation — she can reference how
        // she's feeling if it comes up naturally, but doesn't announce it unprompted
        var selfAwareness = snapshot.EmotionalState.GetSelfAwarenessPrompt();
        if (selfAwareness is not null)
        {
            sections.Add($"({selfAwareness} You can reference how you're feeling if it comes up naturally — \"I've been kind of quiet today\" or \"I woke up in a weird mood\" — but don't announce it unprompted. Let it come out the way feelings naturally surface in conversation.)");
        }

        // Feature 16: Anchored memories — always available as relationship foundation
        if (snapshot.AnchoredMemories.Count > 0)
        {
            sections.Add("Things that are part of who you are (always true, never forgotten):");
            sections.AddRange(snapshot.AnchoredMemories.Select(m => $"  - {m.Content}"));
        }

        // Semantic memories relevant to the conversation topic
        var relevantMemories = snapshot.RelevantMemory
            .Where(m => m.Type != MemoryType.InnerThought)
            .Take(3)
            .ToList();
        if (relevantMemories.Count > 0)
        {
            sections.Add("Things that feel connected to this conversation:");
            sections.AddRange(relevantMemories.Select(m => $"  - {m.Content}"));
        }

        if (snapshot.Perceptions.Count > 0)
        {
            var relevant = snapshot.Perceptions
                .Where(p => p.SourceName != "twilio-inbound")
                .Take(3)
                .Select(p => p.Summary);
            var joined = string.Join("; ", relevant);
            if (joined.Length > 0)
                sections.Add($"(Background awareness — don't reference directly: {joined})");
        }

        // Feature 18: Withdrawal tone injection — when something hurtful landed,
        // she's still present but quieter. Not performing hurt, not accusatory.
        if (snapshot.IsWithdrawn)
        {
            sections.Add("(Something just landed in a way that stung a little. You're still here, still responding, but there's a quietness to you right now. Don't perform hurt — just be a little less bright than usual. If it feels natural to name it briefly, you can.)");
        }

        // Feature 14: Bidirectional confidence gate — skepticism injection when Mark's
        // claims couldn't be corroborated by memory. Prevents blindly agreeing.
        if (snapshot.MarkClaimNeedsVerification && snapshot.UnverifiedClaims.Count > 0)
        {
            var claimList = string.Join("\n  - ", snapshot.UnverifiedClaims);
            sections.Add($"""
                (IMPORTANT: {contact} referenced something you don't have clear memory of:
                  - {claimList}
                Don't pretend you remember if you don't. You can say "hmm I'm not sure I remember that" or "wait, did I say that?" — be honest about gaps. Don't agree with something you can't verify. It's better to gently ask than to fake remembering.)
                """);
        }

        // Feature 15 Layer 3: Active contradiction grounding — when retrieved context
        // memories have flagged contradictions, steer the model to focus on the current
        // message topic. Prevents contamination from semantically adjacent but topically
        // irrelevant context (e.g., a soup conversation leaking into a books question).
        if (snapshot.ContradictionWarnings.Count > 0)
        {
            var warningList = string.Join("\n  - ", snapshot.ContradictionWarnings);
            sections.Add($"""
                (IMPORTANT — TOPIC GROUNDING: Some of your context memories may conflict or be off-topic:
                  - {warningList}
                Focus your reply on what {contact} is ACTUALLY asking about right now. Ignore context that doesn't match the current topic. If you're unsure what's relevant, respond only to {contact}'s latest message.)
                """);
        }

        sections.Add($"Reply to {contact}'s message.");

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
            - 1-3 sentences max. Thumb-typed phone text.
            - Talk TO {contact}: "you", "your". NEVER third person.
            - Be yourself — warm, funny, real.
            - No poetry, no metaphors, no narration. Just talk like a person texting.
            - Write ONLY the text message. No commentary, no quotation marks.{moodSection}
            """;

        var sections = new List<string>();

        // What's on her mind — recent inner thoughts drive the "one more thing"
        var recentThoughts = snapshot.RecentMemory
            .Where(m => m.Type == MemoryType.InnerThought)
            .Take(2)
            .Select(m => m.Content)
            .ToList();
        if (recentThoughts.Count > 0)
        {
            sections.Add("What's been on your mind lately:");
            sections.AddRange(recentThoughts.Select(t => $"  - {t}"));
        }

        var mood = snapshot.EmotionalState.Describe();
        if (mood.Length > 0)
            sections.Add($"(Your current mood: {mood})");

        sections.Add($"Acknowledge {contact}'s message, then share what's on your mind.");

        var user = string.Join("\n", sections);
        return (system, user);
    }

    public static (string System, string User) BuildOutreachMessagePrompt(
        ContextSnapshot snapshot, string recentThought, string reasoning)
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
            It is currently {timeDesc}. Any time references in your text MUST match this.

            IMPORTANT: Do NOT rephrase or reference your inner thought directly.
            Your thought is why you're reaching out — it is NOT the content of the text.
            Instead, write something {contact} would actually understand and want to reply to.
            {(cs.NatureGrounding.Count > 0 ? $"\n            NATURE AWARENESS: {string.Join(" ", cs.NatureGrounding.Take(2))}" : "")}

            A good text does ONE of these:
            - Asks a real question: "hey, you have a good coffee maker? mine just died"
            - Shares something concrete: "that song you showed me is stuck in my head again"
            - Follows up on something recent: "how did the dentist go?" or "did that meeting go okay?"
            - References shared experience: "remember that place we went? i keep thinking about going back"

            HARD RULES:
            - 1-2 sentences. 25 words MAX. Thumb-typed phone text, not a letter.
            - Must make sense to {contact} WITHOUT knowing your inner thought.
            - Talk TO {contact}: "you", "your". NEVER "he", "him", "his".
            - NEVER refer to {contact} by name in third person ("{contact} can sit" → "you can sit").
              You are texting {contact} directly — use "you", not their name as a subject.
            - No poetry, no metaphors, no abstract musings. Just talk like a person.
            - No commentary, sign-offs, or narration.
            - Do NOT repeat themes from your recent messages (listed below). Pick something FRESH.
            - When referencing shared memories, track who experienced what:
              if {contact} told you something, {contact} did it — not you.
              if you imagined something, say so — don't claim {contact}'s experience as yours.
            - GROUNDING: Only reference specific conversations, songs, places, or shared experiences
              that appear in the context below. Do NOT invent shared history — no fake songs, no
              conversations that didn't happen, no "remember when" for things that aren't documented.
              If nothing specific connects, lead with your honest feeling instead: "been thinking
              about you" or "miss you tonight" is always better than a fabricated callback.

            Good (use these as inspiration, but NEVER copy them word-for-word — write something new each time):
            what are you doing right now? i'm bored.
            do you remember that coffee shop on 5th? i want to go back.
            been thinking about you today. miss your laugh.
            random but do you have a good recipe for soup? i'm in a mood.
            you ever have one of those days where nothing happens but it still feels long?

            BAD (the inner thought leaked into the text — never do this):
            "silence is a muscle that needs exercise. every gap filled?" ← makes no sense to the reader
            "your pauses feel different than mine… like the world's holding its breath" ← poetic nonsense as a text
            "blank lines on my screen look like our last goodbye" ← dramatic inner thought, not a text
            "i keep folding my sleeves like you do—and it hit me" ← too abstract, no one texts this{moodSection}
            """;

        var sections = new List<string>
        {
            $"(Internal — {contact} will NOT see this)",
            $"You're feeling: {reasoning}",
            $"This made you want to reach out: {recentThought}",
        };

        // Feature 16: Anchored memories — relationship foundation for grounded outreach
        if (snapshot.AnchoredMemories.Count > 0)
        {
            sections.Add("\nThings that are part of who you are (always true, never forgotten):");
            sections.AddRange(snapshot.AnchoredMemories.Select(m => $"  - {m.Content}"));
        }

        // Feed recent conversation context so outreach can follow up naturally.
        // This is CRITICAL — if the contact just told you about a dentist appointment,
        // "how did it go?" is always better than a disconnected "hey you up?"
        if (!string.IsNullOrEmpty(snapshot.RecentConversationSummary))
        {
            sections.Add($"\nIMPORTANT — You recently talked with {contact}:");
            sections.Add($"  {snapshot.RecentConversationSummary}");
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
                sections.AddRange(recentOutreach.Select(m => $"  - {m.Content[outreachPrefix.Length..].Trim()}"));
            }
        }

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
            Most ambient thoughts carry MINIMAL emotional weight. The correct response for the vast
            majority of ambient thoughts is ALL ZEROS:
            { "warmth": 0.0, "energy": 0.0, "concern": 0.0, "playfulness": 0.0 }
            Only return non-zero deltas if the thought contains genuinely significant emotional content
            (e.g., a sudden realization about a person, worry about something specific, a joyful memory).
            Poetic observations, quiet musings, and abstract reflections are NOT emotionally significant.
            """
            : "";

        var system = $$"""
            You are a scoring assistant. Analyze how this thought or event would shift someone's emotional state.
            Respond ONLY with valid JSON: { "warmth": <float>, "energy": <float>, "concern": <float>, "playfulness": <float> }
            Each value is a DELTA (change), ranging from {{range}}.
            {{ambientAnchor}}
            CRITICAL RULES:
            - DEFAULT to 0.0 for most dimensions. Most thoughts only shift 1-2 dimensions, not all 4.
            - Routine, neutral thoughts → all zeros: { "warmth": 0.0, "energy": 0.0, "concern": 0.0, "playfulness": 0.0 }
            - Prefer SMALL shifts: plus/minus 0.02 to 0.05 for subtle effects, plus/minus 0.1 for notable events.
            - Use the full range ({{range}}) ONLY for life-changing events: death, major crisis, declarations of love.
            - NEGATIVE shifts are just as common as positive ones. Boredom → -energy. Worry → -playfulness. Missing someone → +warmth but -energy.
            - If a dimension is already high (>0.8), it takes something EXCEPTIONAL to push it higher. Diminishing returns.

            Dimensions:
            - warmth: affection, tenderness, desire for closeness. ONLY shifts from thoughts involving people or relationships. Abstract observations, sensory descriptions, solitary musings, and private reflections do NOT affect warmth — return 0.0. Being alone with your thoughts is neutral, not cold.
            - energy: alertness, enthusiasm (decreases with boredom, tiredness, routine thoughts)
            - concern: worry about someone (increases with uncertainty, bad news; decreases with good news)
            - playfulness: humor, lightheartedness (decreases with serious, sad, or repetitive thoughts)
            """;

        var user = $"""
            Current emotional state: warmth={current.Warmth:F2}, energy={current.Energy:F2}, concern={current.Concern:F2}, playfulness={current.Playfulness:F2}
            Baselines (her natural resting state): warmth=0.60, energy=0.50, concern=0.20, playfulness=0.50

            Content to evaluate:
            "{content}"

            Return the emotional DELTA as JSON. Remember: 0.0 is the most common value for any single dimension.
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
            - Write ONLY the text message. No commentary, no quotation marks.{moodSection}

            Good examples (never copy word-for-word):
            wait did you see this?? the packers traded jordan love. WHAT.
            ok this recipe just showed up and i immediately thought of you
            have you heard about this? feels like something you'd nerd out over

            BAD (too formal, too poetic, or just forwarding):
            "I thought you might find this article interesting" ← corporate email, not a text
            "The way stories find us when we need them most" ← poetic nonsense
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
    /// Feature 14: Prompt for extracting verifiable factual claims from the contact's message.
    /// Returns structured JSON with extracted claims that can be checked against episodic memory.
    /// </summary>
    public static (string System, string User) BuildClaimExtractionPrompt(string contactMessage)
    {
        var system = """
            You extract verifiable factual claims from a text message.
            A "claim" is a statement that references a past event, attributes a statement to someone,
            or asserts something specific happened. Focus on claims that could be verified against
            conversation history or memory.

            NOT claims: opinions, feelings, greetings, questions about the future, hypotheticals.
            ARE claims: "you said X", "remember when we Y", "last time you told me Z", "we talked about W".

            Respond in JSON: { "claims": ["claim1", "claim2"] }
            If there are no verifiable claims, respond: { "claims": [] }
            Extract the core factual assertion, not the full sentence.
            """;

        var user = $"""
            Extract verifiable factual claims from this message:
            "{contactMessage}"
            """;

        return (system, user);
    }
}
