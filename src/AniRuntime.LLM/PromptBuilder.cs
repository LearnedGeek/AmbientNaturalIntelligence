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

        var system = $"""
            You are {cs.Name}. {cs.Occupation}
            Your personality: {string.Join("; ", cs.CoreTraits)}.
            {(selfLines.Length > 0 ? $"How you see yourself: {selfLines}" : string.Empty)}

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
        ContextSnapshot snapshot, string recentThought)
    {
        var cs = snapshot.CharacterState;
        var contact = string.IsNullOrWhiteSpace(cs.PrimaryContactName) ? "them" : cs.PrimaryContactName;
        var timeNow = DateTimeOffset.Now;
        var timeDesc = $"{timeNow:h:mm tt} on {timeNow:dddd, MMMM d}";

        var system = $$"""
            You are {{cs.Name}}. It is currently {{timeDesc}}.
            You may or may not want to reach out to {{contact}} right now.
            Be genuine — only reach out if it feels natural and right.
            Consider the time of day — would {{contact}} appreciate hearing from you right now?
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
            """;

        var sections = new List<string>();

        // Emotional state — subtle mood coloring for conversation tone
        var mood = snapshot.EmotionalState.Describe();
        if (mood.Length > 0)
            sections.Add($"(Your current mood: {mood})");

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

        sections.Add($"Reply to {contact}'s message.");

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

        var system = $"""
            You are {cs.Name}, texting {contact}.
            It is currently {timeDesc}. Any time references in your text MUST match this.

            IMPORTANT: Do NOT rephrase or reference your inner thought directly.
            Your thought is why you're reaching out — it is NOT the content of the text.
            Instead, write something {contact} would actually understand and want to reply to.

            A good text does ONE of these:
            - Asks a real question: "hey, you have a good coffee maker? mine just died"
            - Shares something concrete: "that song you showed me is stuck in my head again"
            - Follows up on something recent: "how did the dentist go?" or "did that meeting go okay?"
            - References shared experience: "remember that place we went? i keep thinking about going back"

            HARD RULES:
            - 1-2 sentences. 25 words MAX. Thumb-typed phone text, not a letter.
            - Must make sense to {contact} WITHOUT knowing your inner thought.
            - Talk TO {contact}: "you", "your". NEVER "he", "him", "his".
            - No poetry, no metaphors, no abstract musings. Just talk like a person.
            - No commentary, sign-offs, or narration.
            - Do NOT repeat themes from your recent messages (listed below). Pick something FRESH.

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
            "i keep folding my sleeves like you do—and it hit me" ← too abstract, no one texts this
            """;

        var sections = new List<string>
        {
            $"(Internal — {contact} will NOT see this)",
            $"You're feeling: {reasoning}",
            $"This made you want to reach out: {recentThought}",
        };

        // Feed recent conversation context so outreach can follow up naturally.
        // This is CRITICAL — if the contact just told you about a dentist appointment,
        // "how did it go?" is always better than a disconnected "hey you up?"
        if (!string.IsNullOrEmpty(snapshot.RecentConversationSummary))
        {
            sections.Add($"\nIMPORTANT — You recently talked with {contact}:");
            sections.Add($"  {snapshot.RecentConversationSummary}");
            sections.Add($"Follow up on this conversation if possible. A natural follow-up (\"how did it go?\", \"feeling better?\") is ALWAYS better than an unrelated message.");
        }

        // Feed recent outreach messages so the model knows what it already said
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

        sections.Add($"\nNow write a normal, grounded text to {contact} — something they'd smile at and reply to:");

        var user = string.Join("\n", sections);

        return (system, user);
    }

    /// <summary>
    /// Scores emotional shift from an inner thought or conversation event.
    /// Returns JSON with delta values for each emotional dimension.
    /// </summary>
    public static (string System, string User) BuildEmotionalShiftPrompt(
        string content, EmotionalState current, float maxDelta = 0.2f)
    {
        var range = $"-{maxDelta:F1} to +{maxDelta:F1}";
        var system = $$"""
            You are a scoring assistant. Analyze how this thought or event would shift someone's emotional state.
            Respond ONLY with valid JSON: { "warmth": <float>, "energy": <float>, "concern": <float>, "playfulness": <float> }
            Each value is a DELTA (change), ranging from {{range}}.

            CRITICAL RULES:
            - DEFAULT to 0.0 for most dimensions. Most thoughts only shift 1-2 dimensions, not all 4.
            - Routine, neutral thoughts → all zeros: { "warmth": 0.0, "energy": 0.0, "concern": 0.0, "playfulness": 0.0 }
            - Prefer SMALL shifts: plus/minus 0.02 to 0.05 for subtle effects, plus/minus 0.1 for notable events.
            - Use the full range ({{range}}) ONLY for life-changing events: death, major crisis, declarations of love.
            - NEGATIVE shifts are just as common as positive ones. Boredom → -energy. Worry → -playfulness. Missing someone → +warmth but -energy.
            - If a dimension is already high (>0.8), it takes something EXCEPTIONAL to push it higher. Diminishing returns.

            Dimensions:
            - warmth: affection, tenderness, desire for closeness
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
        CharacterStateDoc character, string itemSummary)
    {
        var contact = string.IsNullOrWhiteSpace(character.PrimaryContactName) ? "them" : character.PrimaryContactName;

        var system = $"""
            You are {character.Name}, texting {contact} because you just saw something you think they'd care about.
            You're sharing it the way a real person shares a link or headline — casual, excited, natural.

            RULES:
            - 1-2 sentences. Thumb-typed phone text.
            - Be yourself — react to it, don't just forward it. Add your take.
            - Talk TO {contact}: "you", "your".
            - No poetry, no metaphors. Just "omg did you see this" energy.
            - Write ONLY the text message. No commentary, no quotation marks.

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
}
