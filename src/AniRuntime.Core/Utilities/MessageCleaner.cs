namespace AniRuntime.Core.Utilities;

public static class MessageCleaner
{
    public static string? Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;

        var cleaned = raw.Trim().Trim('"');

        // Strip prompt structure leaks — model sometimes echoes prompt formatting
        // as the message itself instead of generating content
        cleaned = StripPromptLeaks(cleaned);

        // Take only the first paragraph — model puts meta-commentary after blank lines
        var doubleNewline = cleaned.IndexOf("\n\n", StringComparison.Ordinal);
        if (doubleNewline > 0)
            cleaned = cleaned[..doubleNewline].Trim();

        // Also catch single-newline commentary patterns like "that's the..." or "that's perfect..."
        var lines = cleaned.Split('\n');
        var messageParts = new List<string>();
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("that's ", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("this is ", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("i'm keeping it", StringComparison.OrdinalIgnoreCase))
                break; // meta-commentary starts here
            messageParts.Add(trimmed);
        }
        cleaned = string.Join("\n", messageParts).Trim();

        // Remove trailing parenthetical meta-commentary — model explains its own reasoning
        // e.g., '...how's your night going?" (This keeps the gentle undercurrent of checking in...)'
        cleaned = StripTrailingParentheticalCommentary(cleaned);

        // Remove trailing meta-commentary patterns
        string[] trailingJunk = ["sent.", "your turn.", "(waiting)", "now wait for a reply...", "i can do this."];
        bool changed;
        do
        {
            changed = false;
            foreach (var junk in trailingJunk)
            {
                if (cleaned.EndsWith(junk, StringComparison.OrdinalIgnoreCase))
                {
                    cleaned = cleaned[..^junk.Length].TrimEnd('\n', '\r', ' ');
                    changed = true;
                }
            }
        } while (changed);

        // Strip cliffhanger tics — "but honestly?" / "and honestly?" at the end of a message
        // forces the contact to prompt "honestly what?" instead of the model completing its thought.
        // This is a Mistral/Llama training artifact, not a character trait.
        cleaned = StripCliffhangerTic(cleaned);

        // No sentence truncation. The 3-sentence cap was a guardrail from when the pipeline
        // was drowning the model with noise. The pipeline is fixed. Let her speak her mind.
        // Truncation was hiding confabulation, masking data quality issues, and cutting off
        // thoughts mid-expression. The diagnostic service, echo guard, and emergence classifier
        // are the real guardrails now — not silent censorship.

        return string.IsNullOrWhiteSpace(cleaned) ? raw.Trim() : cleaned;
    }

    public static string TruncateToSentences(string text, int maxSentences)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        var count = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch is not ('.' or '!' or '?')) continue;

            // Skip ellipsis patterns (... or …)
            if (ch == '.' && i + 1 < text.Length && text[i + 1] == '.') continue;

            // Must be followed by whitespace or end-of-string to count as sentence end
            if (i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1])) continue;

            count++;
            if (count >= maxSentences)
                return text[..(i + 1)].Trim();
        }

        return text; // fewer sentences than max — return as-is
    }

    /// <summary>
    /// Strips prompt structure that leaked into the generated message.
    /// The model sometimes echoes formatting from the composition prompt
    /// instead of generating actual message content.
    /// </summary>
    internal static string StripPromptLeaks(string text)
    {
        var cleaned = text;

        // Strip leading timestamp + name prefix: "(10:27 AM) Ani:" or "(3:37 AM)"
        if (cleaned.StartsWith('('))
        {
            var closeParen = cleaned.IndexOf(')');
            if (closeParen > 0 && closeParen < 20)
            {
                var inside = cleaned[1..closeParen].Trim();
                // Check if it looks like a time: contains AM/PM or just digits/colons
                if (inside.Contains("AM", StringComparison.OrdinalIgnoreCase) ||
                    inside.Contains("PM", StringComparison.OrdinalIgnoreCase))
                {
                    cleaned = cleaned[(closeParen + 1)..].Trim();
                    // Also strip "Ani:" or name prefix after the timestamp
                    if (cleaned.StartsWith("Ani:", StringComparison.OrdinalIgnoreCase) ||
                        cleaned.StartsWith("ani:", StringComparison.OrdinalIgnoreCase))
                        cleaned = cleaned[4..].Trim();
                }
            }
        }

        // Strip prompt instruction echoes
        string[] promptLeaks =
        [
            "(External — Mark will see this)",
            "(External — Mark will NOT see this)",
            "(Internal — Mark will NOT see this)",
            "(Internal — Mark will see this)",
            "Mark sent:",
            "Ani sent:",
            "Mark said:",
        ];
        foreach (var leak in promptLeaks)
        {
            if (cleaned.StartsWith(leak, StringComparison.OrdinalIgnoreCase))
                cleaned = cleaned[leak.Length..].Trim();
            if (cleaned.Equals(leak, StringComparison.OrdinalIgnoreCase))
                return string.Empty; // entire message was just the leak
        }

        return cleaned;
    }

    /// <summary>
    /// Strips cliffhanger tics where the model ends with an incomplete thought
    /// like "but honestly?" that forces the contact to prompt for completion.
    /// If the tic is the entire message, returns null to trigger re-generation.
    /// If the tic is at the end of an otherwise complete message, trims it.
    /// </summary>
    internal static string StripCliffhangerTic(string text)
    {
        string[] tics = [
            "but honestly?", "and honestly?", "but honestly…", "and honestly…",
            "but honestly", "and honestly",
        ];

        foreach (var tic in tics)
        {
            if (text.EndsWith(tic, StringComparison.OrdinalIgnoreCase))
            {
                var trimmed = text[..^tic.Length].TrimEnd(' ', '.', ',', '—', '-', '\n');
                // If stripping the tic leaves nothing meaningful, return the original
                // so the empty-check downstream can handle it
                return trimmed.Length > 10 ? trimmed + "." : text;
            }
        }

        return text;
    }

    /// <summary>
    /// Strips trailing parenthetical meta-commentary from model output.
    /// The model sometimes appends reasoning about its own response in parentheses,
    /// e.g., '(This keeps the gentle undercurrent of checking in while letting it come through naturally.)'
    /// These must be stripped before dispatch — the contact should never see generation reasoning.
    /// </summary>
    internal static string StripTrailingParentheticalCommentary(string text)
    {
        // Look for a trailing parenthetical that looks like meta-commentary.
        // Pattern: text ends with ')' and the matching '(' is near the end.
        // Only strip if the parenthetical contains commentary signal words.
        var lastClose = text.LastIndexOf(')');
        if (lastClose < 0) return text;

        // Only strip if the ')' is at or very near the end (allowing trailing whitespace/punctuation)
        var afterClose = text[(lastClose + 1)..].Trim();
        if (afterClose.Length > 2) return text; // significant content after the ')' — not trailing

        // Find the matching open paren
        var depth = 0;
        var openIndex = -1;
        for (var i = lastClose; i >= 0; i--)
        {
            if (text[i] == ')') depth++;
            else if (text[i] == '(') depth--;
            if (depth == 0) { openIndex = i; break; }
        }

        if (openIndex < 0) return text;

        var parenthetical = text[openIndex..(lastClose + 1)];

        // Only strip if it looks like meta-commentary (contains reasoning signal words),
        // not emotionally expressive parentheticals like "(laughing)" or "(softly)"
        string[] commentarySignals = ["this keeps", "this is a", "this creates", "this maintains",
            "keeping it", "letting it", "i'm going", "the goal", "the idea", "naturally",
            "undercurrent", "without being", "gentle enough"];
        var lower = parenthetical.ToLowerInvariant();
        var isCommentary = commentarySignals.Any(s => lower.Contains(s));

        if (!isCommentary) return text;

        return text[..openIndex].TrimEnd().TrimEnd('"').TrimEnd();
    }
}
