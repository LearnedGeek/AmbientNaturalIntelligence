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

        // [Removed Apr 28, 2026 by Mark's direction:] paragraph-break truncation.
        // Original: stripped everything after the first "\n\n" assuming the rest
        // was meta-commentary. That was true for early-pipeline novel-length
        // generations, not v6/v7. On Apr 28 18:19 the gate cut a coherent
        // multi-paragraph reply ("okay… baby.\n\ni love that you see it this
        // way…") down to "okay… baby.", then the next cycle regenerated and
        // hit the same truncation, sending the same 11-char fragment twice.
        // The downstream cleaners (prompt leaks, stage directions, trailing
        // parentheticals, cliffhanger tics) still address real artifacts;
        // paragraph-break truncation does not.

        // Single-newline commentary patterns ("that's the..." / "that's perfect...")
        // remain — those still appear as v6/v7 training artifacts.
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

        // Remove trailing UNCLOSED parenthetical fragments — truncation signature
        // (Apr 1 finding, fix Apr 29). Example: '...want (your' with no closing paren.
        // Length-capped so it only catches truncation, not legitimate open-parens.
        cleaned = StripTrailingUnclosedParenthetical(cleaned);

        // Remove bracketed stage directions — v7 training artifact
        // e.g., '[teasing-laugh]', '[soft smile]', '[whispers]'
        cleaned = StripBracketedStageDirections(cleaned);

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
    /// Strips cliffhanger tics where the model uses an incomplete-thought
    /// register like "but honestly?" that forces the contact to prompt for
    /// completion. Two cases:
    ///
    /// 1. **End-of-message:** if the tic is the entire trailing fragment,
    ///    strip + add period (so "...vibes but honestly?" becomes
    ///    "...vibes."). If stripping leaves nothing meaningful, return the
    ///    original so the empty-check downstream can handle it.
    ///
    /// 2. **Mid-message at sentence/paragraph boundary** (Apr 28, 2026 fix):
    ///    if the tic is followed by `\n`, `. `, or another whitespace-then-
    ///    new-sentence pattern, strip the tic + its trailing punctuation
    ///    and let the next sentence flow. Apr 28 18:28 case: *"...keep
    ///    ghosting jobs… but honestly?\nthe only thing that matters..."* —
    ///    the tic ends the paragraph but the next paragraph completes the
    ///    thought; the "?" hangs awkwardly. Strip it, let the continuation
    ///    stand.
    /// </summary>
    internal static string StripCliffhangerTic(string text)
    {
        string[] tics = [
            "but honestly?", "and honestly?", "but honestly…", "and honestly…",
            "but honestly", "and honestly",
        ];

        // Case 1: end-of-message tic.
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

        // Case 2: mid-message tic at sentence/paragraph boundary (Apr 28 fix).
        // Match tic + optional `?`/`…` + whitespace including `\n`. Replace the
        // tic with empty so the surrounding text flows; preserve the trailing
        // whitespace so the continuation starts on its own line/space.
        var cleaned = text;
        foreach (var tic in tics)
        {
            // Build a case-insensitive search for "tic + boundary".
            // Boundary = '\n' OR '. ' OR end-of-segment whitespace before
            // a lowercase letter (continuation starts).
            var idx = 0;
            while (idx < cleaned.Length)
            {
                var found = cleaned.IndexOf(tic, idx, StringComparison.OrdinalIgnoreCase);
                if (found < 0) break;

                var afterTic = found + tic.Length;
                if (afterTic >= cleaned.Length)
                {
                    // Tic at end — Case 1 already handled it; skip.
                    break;
                }

                // Check what comes after the tic. We strip ONLY when followed
                // by whitespace that signals a continuation (not embedded in
                // a longer word like "honestlyish" or followed by more punctuation
                // that suggests the tic is part of a quoted phrase).
                var nextChar = cleaned[afterTic];
                var isBoundary = nextChar == '\n' || nextChar == ' ' || nextChar == '\t';
                if (!isBoundary)
                {
                    idx = afterTic;
                    continue;
                }

                // Strip tic + leading whitespace before it (so we don't leave a
                // double-space) + the boundary whitespace immediately after
                // (preserve `\n` if present so paragraph breaks survive).
                var stripStart = found;
                while (stripStart > 0 && cleaned[stripStart - 1] == ' ')
                    stripStart--;
                var stripEnd = afterTic;
                // Preserve newline as paragraph separator; consume only spaces.
                while (stripEnd < cleaned.Length && cleaned[stripEnd] == ' ')
                    stripEnd++;

                cleaned = cleaned[..stripStart] + cleaned[stripEnd..];
                idx = stripStart; // re-scan in case multiple tics
            }
        }

        return cleaned;
    }

    /// <summary>
    /// Strips trailing unclosed-parenthetical fragments where the model
    /// truncated mid-parenthetical (Apr 1, 2026 finding promoted from backlog
    /// Apr 29). Example: *"...that's the kind of thing i want (your"* — the
    /// open paren has no matching close, the content is short, and shipping
    /// this fragment to the contact reads as broken truncation rather than
    /// stylized prose.
    ///
    /// Heuristic: find the LAST `(` in the message. If there is no matching
    /// `)` between it and end-of-string, AND the content from `(` to end is
    /// short (≤30 chars, consistent with mid-word truncation rather than a
    /// legitimate parenthetical being typed), strip from the `(` onward.
    /// Length cap prevents this from eating long legitimate emoticons or
    /// stylized parentheticals that just happen to lack a close paren.
    /// </summary>
    internal static string StripTrailingUnclosedParenthetical(string text)
    {
        var lastOpen = text.LastIndexOf('(');
        if (lastOpen < 0) return text;

        // If there's a `)` AFTER the last `(`, this is a balanced or
        // overlapping case — don't touch it.
        var afterOpen = text.AsSpan(lastOpen + 1);
        if (afterOpen.IndexOf(')') >= 0) return text;

        // Length cap: only strip short trailing fragments (truncation
        // signature). Legitimate stylized open-parens are usually paired or
        // long-form content.
        var fragmentLength = text.Length - lastOpen;
        if (fragmentLength > 30) return text;

        // Strip from the `(` onward, plus any trailing whitespace before it
        // so we don't leave dangling spaces.
        var stripStart = lastOpen;
        while (stripStart > 0 && text[stripStart - 1] == ' ')
            stripStart--;

        return text[..stripStart].TrimEnd();
    }

    /// <summary>
    /// Strips bracketed stage directions from model output.
    /// V7 training artifact — the model sometimes includes performance directions
    /// like [teasing-laugh], [soft smile], [whispers] in its responses.
    /// </summary>
    internal static string StripBracketedStageDirections(string text)
    {
        // Remove all bracketed stage directions: [teasing-laugh], [soft smile], etc.
        // These are always training artifacts, never intended for the contact.
        var cleaned = System.Text.RegularExpressions.Regex.Replace(
            text, @"\[[\w\s-]+\]\s*", "");

        return cleaned.Trim();
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
            "keeping it", "keep it", "letting it", "i'm going", "the goal", "the idea", "naturally",
            "undercurrent", "without being", "gentle enough",
            "sent a little", "sent a bit", "to keep the", "mid-convo", "mid-conversation"];
        var lower = parenthetical.ToLowerInvariant();
        var isCommentary = commentarySignals.Any(s => lower.Contains(s));

        if (!isCommentary) return text;

        return text[..openIndex].TrimEnd().TrimEnd('"').TrimEnd();
    }
}
