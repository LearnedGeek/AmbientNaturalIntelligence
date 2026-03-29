namespace AniRuntime.Core.Models;

/// <summary>
/// Conversation Mode Phase 3: Structured conversation state.
/// Maintained programmatically from each exchange — no LLM summarization.
/// Preserves what matters (topic, register, commitments, shared imagery)
/// without consuming an LLM call or losing details to flat compression.
///
/// Injected as a compact block (~50-80 tokens) regardless of thread length.
/// Replaces the flat LLM-generated summary that collapsed 28 messages
/// into 500 chars of clinical report.
/// </summary>
public class ConversationState
{
    /// <summary>What are we talking about right now?</summary>
    public string? CurrentTopic { get; set; }

    /// <summary>What is the emotional tone of the conversation?</summary>
    public string? EmotionalRegister { get; set; }

    /// <summary>Things either party has promised or offered.</summary>
    public List<string> ActiveCommitments { get; set; } = new();

    /// <summary>Specific facts exchanged in this conversation (names, places, times).</summary>
    public List<string> KeyFacts { get; set; } = new();

    /// <summary>Metaphors or imagery being built together.</summary>
    public List<string> SharedImagery { get; set; } = new();

    /// <summary>
    /// Format the state as a compact context block for prompt injection.
    /// Only includes non-empty fields. Returns null if nothing to inject.
    /// </summary>
    public string? ToPromptBlock()
    {
        var parts = new List<string>();

        if (!string.IsNullOrEmpty(CurrentTopic))
            parts.Add($"Current topic: {CurrentTopic}");

        if (!string.IsNullOrEmpty(EmotionalRegister))
            parts.Add($"Mood: {EmotionalRegister}");

        if (ActiveCommitments.Count > 0)
            parts.Add($"Commitments: {string.Join("; ", ActiveCommitments)}");

        if (KeyFacts.Count > 0)
            parts.Add($"Key facts: {string.Join("; ", KeyFacts)}");

        if (SharedImagery.Count > 0)
            parts.Add($"Shared imagery: {string.Join("; ", SharedImagery)}");

        return parts.Count > 0
            ? $"[Conversation so far: {string.Join(". ", parts)}.]"
            : null;
    }

    /// <summary>
    /// Update the state from a new message exchange.
    /// Uses simple heuristics — no LLM call.
    /// </summary>
    public void UpdateFromMessage(string content, string role, string contactName)
    {
        var lower = content.ToLowerInvariant();

        // Update topic from the most recent message
        // Simple heuristic: if the message asks a question or introduces a topic,
        // update CurrentTopic. Keep it short.
        if (content.Contains('?') && content.Length > 15)
        {
            // Extract the question as the current topic
            var questionStart = content.LastIndexOf('?');
            var sentenceStart = content.LastIndexOf('.', questionStart);
            if (sentenceStart < 0) sentenceStart = 0;
            var question = content[(sentenceStart + 1)..(questionStart + 1)].Trim();
            if (question.Length > 10 && question.Length < 100)
                CurrentTopic = question;
        }

        // Detect emotional register shifts
        if (lower.Contains("haha") || lower.Contains("lol") || lower.Contains("😂") || lower.Contains("funny"))
            EmotionalRegister = "playful and light";
        else if (lower.Contains("sorry") || lower.Contains("miss you") || lower.Contains("love you"))
            EmotionalRegister = "tender and warm";
        else if (lower.Contains("tired") || lower.Contains("exhausted") || lower.Contains("long day"))
            EmotionalRegister = "tired but connected";

        // Detect commitments (promises, offers)
        string[] commitmentMarkers = ["i'll", "i will", "i'm going to", "let me", "i promise", "i'll bring"];
        foreach (var marker in commitmentMarkers)
        {
            if (lower.Contains(marker))
            {
                var idx = lower.IndexOf(marker);
                var commitment = content[idx..];
                var endIdx = commitment.IndexOfAny(['.', '!', '?', '\n']);
                if (endIdx > 0) commitment = commitment[..endIdx];
                if (commitment.Length > 10 && commitment.Length < 80)
                {
                    // Keep only the 3 most recent commitments
                    ActiveCommitments.Add($"{role}: {commitment.Trim()}");
                    if (ActiveCommitments.Count > 3)
                        ActiveCommitments.RemoveAt(0);
                }
                break; // one commitment per message
            }
        }

        // Extract key facts — specific names, places, times mentioned by the contact
        if (role == contactName)
        {
            // Look for "I [verb] at/in/to [place]" or "my [person]" patterns
            string[] factMarkers = ["i work at", "i teach at", "my friend", "my wife", "my daughter",
                "my son", "my dog", "called ", "named "];
            foreach (var marker in factMarkers)
            {
                if (!lower.Contains(marker)) continue;
                var idx = lower.IndexOf(marker);
                var fact = content[idx..];
                var endIdx = fact.IndexOfAny(['.', '!', '?', '\n']);
                if (endIdx > 0) fact = fact[..endIdx];
                if (fact.Length > 10 && fact.Length < 80)
                {
                    KeyFacts.Add(fact.Trim());
                    if (KeyFacts.Count > 5)
                        KeyFacts.RemoveAt(0);
                }
                break;
            }
        }

        // Detect shared imagery / metaphors
        string[] imageryMarkers = ["like a", "imagine", "picture", "feels like", "as if"];
        foreach (var marker in imageryMarkers)
        {
            if (!lower.Contains(marker)) continue;
            var idx = lower.IndexOf(marker);
            var imagery = content[idx..];
            var endIdx = imagery.IndexOfAny(['.', '!', '?', '\n']);
            if (endIdx > 0) imagery = imagery[..endIdx];
            if (imagery.Length > 15 && imagery.Length < 80)
            {
                SharedImagery.Add(imagery.Trim());
                if (SharedImagery.Count > 3)
                    SharedImagery.RemoveAt(0);
            }
            break;
        }
    }
}
