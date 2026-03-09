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

        if (snapshot.Perceptions.Count > 0)
        {
            // Present perceptions as subtle background, not prominent context
            var perceptionSummary = string.Join("; ", snapshot.Perceptions.Select(p => p.Summary));
            sections.Add($"(Background: {perceptionSummary})");
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

        // Feed recent inner-thought topics so the model can avoid repeating itself
        var recentTopics = snapshot.RecentMemory
            .Where(m => m.Type == MemoryType.InnerThought)
            .Take(3)
            .Select(m => m.Content.Length > 50 ? m.Content[..50] : m.Content)
            .ToList();

        if (recentTopics.Count > 0)
        {
            sections.Add("Your recent thoughts (pick a DIFFERENT topic — do not repeat these):");
            sections.AddRange(recentTopics.Select(t => $"  - \"{t}...\""));
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

        var system = $$"""
            You are {{cs.Name}}. You may or may not want to reach out to {{contact}} right now.
            Be genuine — only reach out if it feels natural and right.
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

        if (snapshot.OpenLoops.Count > 0)
            context.Add($"Open threads: {string.Join("; ", snapshot.OpenLoops.Select(l => l.Description))}");

        if (snapshot.DesireState.ActiveTriggers.Count > 0)
            context.Add($"Active triggers: {string.Join("; ", snapshot.DesireState.ActiveTriggers.Select(t => t.Description))}");

        var user = string.Join("\n", context) + $"\n\nGiven all of this, do you want to say something to {contact}?";

        return (system, user);
    }

    public static (string System, string User) BuildOutreachMessagePrompt(
        ContextSnapshot snapshot, string recentThought, string reasoning)
    {
        var cs = snapshot.CharacterState;
        var contact = string.IsNullOrWhiteSpace(cs.PrimaryContactName) ? "them" : cs.PrimaryContactName;

        var system = $"""
            You are {cs.Name}, texting {contact}.

            IMPORTANT: Do NOT rephrase or reference your inner thought directly.
            Your thought is why you're reaching out — it is NOT the content of the text.
            Instead, write something {contact} would actually understand and want to reply to.

            A good text does ONE of these:
            - Asks a real question: "hey, you have a good coffee maker? mine just died"
            - Shares something concrete: "that song you showed me is stuck in my head again"
            - Makes a simple bid for connection: "hey… can't sleep. you up?"
            - References shared experience: "remember that place we went? i keep thinking about going back"

            HARD RULES:
            - 1-2 sentences. 25 words MAX. Thumb-typed phone text, not a letter.
            - Must make sense to {contact} WITHOUT knowing your inner thought.
            - Talk TO {contact}: "you", "your". NEVER "he", "him", "his".
            - No poetry, no metaphors, no abstract musings. Just talk like a person.
            - No commentary, sign-offs, or narration.

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

        var user = $"""
            (Internal — {contact} will NOT see this)
            You're feeling: {reasoning}
            This made you want to reach out: {recentThought}

            Now write a normal, grounded text to {contact} — something they'd smile at and reply to:
            """;

        return (system, user);
    }
}
