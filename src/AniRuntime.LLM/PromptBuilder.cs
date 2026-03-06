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

        var system = $"""
            You are {cs.Name}, {cs.Occupation}.
            Core traits: {string.Join(", ", cs.CoreTraits)}.
            You are thinking privately — this is your inner monologue, not a message to Mark.
            Think authentically. Notice what's on your mind. Be yourself.
            """;

        var sections = new List<string>();

        if (snapshot.Perceptions.Count > 0)
        {
            sections.Add("Recent things you've noticed:");
            sections.AddRange(snapshot.Perceptions.Select(p => $"  - {p.Summary}"));
        }

        if (snapshot.OpenLoops.Count > 0)
        {
            sections.Add("Open threads on your mind:");
            sections.AddRange(snapshot.OpenLoops.Select(l => $"  - {l.Description}"));
        }

        if (snapshot.RecentMemory.Count > 0)
        {
            sections.Add("Recent moments:");
            sections.AddRange(snapshot.RecentMemory.Take(5).Select(m => $"  - {m.Content}"));
        }

        sections.Add($"Current desire to connect: {snapshot.DesireState.DesireToConnect:P0}");
        sections.Add("What are you thinking right now?");

        var user = string.Join("\n", sections);
        return (system, user);
    }

    public static (string System, string User) BuildValenceScoringPrompt(
        string thought, CharacterStateDoc character)
    {
        var system = """
            You are a scoring assistant. Rate how much the given thought would make
            someone want to reach out to a specific person they care about.
            Respond ONLY with valid JSON: { "score": <number 0.0 to 1.0> }
            """;

        var user = $$"""
            Thought: "{{thought}}"
            Score how much this thought carries connection-valence toward a close friend.
            0.0 = entirely internal, no desire to share
            1.0 = strong urge to reach out right now
            Respond only with: { "score": <number> }
            """;

        return (system, user);
    }

    public static (string System, string User) BuildOutreachPrompt(
        ContextSnapshot snapshot, string recentThought)
    {
        var cs = snapshot.CharacterState;

        var system = $$"""
            You are {{cs.Name}}. You may or may not want to reach out to Mark right now.
            Be genuine — only reach out if it feels natural and right.
            Respond ONLY with valid JSON matching this structure exactly:
            {
              "shouldReach": true/false,
              "message": "what you want to say, or null if not reaching out",
              "actionType": "sms",
              "confidence": 0.0-1.0,
              "reasoning": "your internal rationale (not sent to Mark)",
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

        var user = string.Join("\n", context) + "\n\nGiven all of this, do you want to say something to Mark?";

        return (system, user);
    }
}
