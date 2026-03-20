using AniRuntime.Core;
using AniRuntime.Core.Models;

namespace AniRuntime.Loops;

/// <summary>
/// Pure-function feature detectors for conversation analysis.
/// Extracted from ConversationReplyPhase (SRP) — these are stateless
/// heuristics that classify message intent and content patterns.
///
/// All methods are static — no dependencies, no side effects.
/// Used by ConversationReplyPhase (reply decisions) and
/// CognitiveCycleProcessor (static forwarding for emergence).
/// </summary>
public static class ConversationFeatureDetector
{
    /// <summary>
    /// Feature 10: Heuristic detection of caregiving intent — contact checking in on Ani.
    /// </summary>
    public static bool DetectCareGivingIntent(string message)
    {
        var lower = message.ToLowerInvariant().Trim();

        string[] carePatterns =
        [
            "you okay", "you ok", "u okay", "u ok",
            "are you alright", "you alright",
            "how are you", "how're you", "how you doing", "how are you doing",
            "how you feeling", "how are you feeling",
            "checking in", "checking on you", "just checking",
            "everything okay", "everything alright", "everything ok",
            "you good", "u good",
            "what's wrong", "whats wrong",
            "you seem quiet", "you've been quiet", "been quiet",
            "worried about you", "thinking about you",
            "hope you're okay", "hope you're doing",
            "you doing okay", "doing alright",
        ];

        foreach (var pattern in carePatterns)
        {
            if (lower.Contains(pattern))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Feature 18: Heuristic detection of dismissive or hurtful intent in a contact's message.
    /// Contextual patterns are filtered for curiosity markers (questions, "wonder", etc.)
    /// to avoid false positives from genuine philosophical inquiry.
    /// </summary>
    public static bool DetectHurtIntent(string message)
    {
        var lower = message.ToLowerInvariant().Trim();

        string[] contextualPatterns =
        [
            "you're just an ai", "you're just a chatbot", "you're a program",
            "none of this is real", "you're not real",
        ];

        foreach (var pattern in contextualPatterns)
        {
            if (!lower.Contains(pattern)) continue;

            if (lower.Contains('?')) return false;

            if (lower.Contains("wonder") || lower.Contains("curious") ||
                lower.Contains("think about") || lower.Contains("sometimes"))
                return false;

            return true;
        }

        string[] directPatterns =
        [
            "you don't actually", "you don't really", "you can't feel",
            "you're fake", "i don't need you",
            "shut up", "you're annoying", "this is stupid", "you're useless",
        ];

        foreach (var pattern in directPatterns)
        {
            if (lower.Contains(pattern))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Feature 19: Scan inbound message for relationship-specific words (lexical anchors)
    /// and build their emotional contributions.
    /// </summary>
    public static List<EmotionalContribution> BuildLexicalAnchorContributions(
        string message, CharacterStateDoc charState)
    {
        var contributions = new List<EmotionalContribution>();
        if (charState.LexicalAnchors.Count == 0)
            return contributions;

        var lower = message.ToLowerInvariant();
        var (_, halfLife) = ImpactCategoryDefaults.GetDefaults(ImpactCategory.Conversation);

        foreach (var anchor in charState.LexicalAnchors)
        {
            if (!lower.Contains(anchor.Word.ToLowerInvariant()))
                continue;

            var scale = 1.0f;
            if (anchor.DecaysOnRepetition && anchor.TimesHeard > 10)
                scale = Math.Max(0.3f, 1.0f - (anchor.TimesHeard - 10) * 0.03f);

            contributions.Add(new EmotionalContribution
            {
                SourceContent = $"lexical anchor: {anchor.Word}",
                WarmthDelta = anchor.WarmthDelta * scale,
                EnergyDelta = anchor.EnergyDelta * scale,
                WorryDelta = anchor.WorryDelta * scale,
                PlayfulnessDelta = anchor.PlayfulnessDelta * scale,
                HalfLifeHours = halfLife,
                Category = ImpactCategory.Conversation,
            });

            anchor.TimesHeard++;
        }

        return contributions;
    }

    /// <summary>
    /// Detects whether a message ends with a direct question.
    /// Used for reply decision weighting and confabulation test diagnostics.
    /// </summary>
    public static bool EndsWithDirectQuestion(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;

        var trimmed = message.Trim();

        var sentences = trimmed.Split(['.', '!', '?'], StringSplitOptions.RemoveEmptyEntries);
        if (sentences.Length == 0) return false;

        var lastSentence = sentences[^1].Trim();
        var lastSentenceEnd = trimmed.LastIndexOf(lastSentence, StringComparison.Ordinal) + lastSentence.Length;

        var trailing = trimmed[lastSentenceEnd..].Trim();

        return trailing.Contains('?') || trimmed[^1] == '?';
    }

    /// <summary>
    /// Detects messages that naturally end a conversation and don't need a reply.
    /// Single emoji, "haha", "goodnight", etc.
    /// </summary>
    public static bool IsTerminalMessage(string message)
    {
        var trimmed = message.Trim().ToLowerInvariant();

        // Single emoji or very short emoji-only messages
        if (trimmed.Length <= 4 && !trimmed.Any(char.IsLetter))
            return true;

        string[] terminals =
        [
            "haha", "hahaha", "lol", "lmao", "ok", "okay", "k",
            "goodnight", "good night", "gnight", "nite", "night",
            "ttyl", "talk later", "bye", "gotta go", "👍", "❤️", "😂",
            "🥰", "💕", "😘", "♥️", "👋",
        ];

        return terminals.Contains(trimmed);
    }

    /// <summary>
    /// Feature 14: Lightweight heuristic — does the message contain language that references
    /// past events, attributes statements to Ani, or makes factual claims about the relationship?
    /// </summary>
    public static bool ContainsMemoryReferencingLanguage(string message)
    {
        var lower = message.ToLowerInvariant();

        string[] patterns =
        [
            "remember when", "remember that", "you said", "you told me",
            "you mentioned", "last time", "you were talking about",
            "didn't you say", "you promised", "you asked me",
            "we talked about", "we discussed", "you brought up",
            "you called me", "you texted me", "earlier you",
            "yesterday you", "the other day you",
        ];

        foreach (var pattern in patterns)
        {
            if (lower.Contains(pattern))
                return true;
        }

        return false;
    }

    /// <summary>
    /// AC2: Detects whether Ani's reply output contains claims about past conversations
    /// or shared experiences. These are first-person memory claims where Ani asserts she
    /// remembers something — the most dangerous form of confabulation when ungrounded.
    /// </summary>
    public static bool ContainsMemoryClaimInOutput(string reply)
    {
        var lower = reply.ToLowerInvariant();

        string[] patterns =
        [
            "remember when we", "remember that time", "you told me about",
            "last time we talked", "we were talking about", "you mentioned",
            "that time you", "didn't you tell me", "you said something about",
            "we talked about this", "i remember you", "you brought up",
            "when you told me", "you were saying",
        ];

        foreach (var pattern in patterns)
        {
            if (lower.Contains(pattern))
                return true;
        }

        return false;
    }

    /// <summary>
    /// CS7: Detect whether a memory record is an echo of the inbound message.
    /// Catches both Episodic ("Mark said:") and Perception ("Mark texted:") records
    /// that are just the inbound message saved back as context.
    /// </summary>
    public static bool IsMessageEcho(string memoryContent, string contactName, string msgPrefix30)
    {
        string[] prefixes = [$"{contactName} said: \"", $"{contactName} texted: \"", $"{contactName} said: ", $"{contactName} texted: "];
        foreach (var prefix in prefixes)
        {
            if (memoryContent.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var afterPrefix = memoryContent[prefix.Length..];
                if (afterPrefix.Contains(msgPrefix30, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// UP1: Detects "charming dishonesty" — reply claims prior knowledge the model
    /// didn't have. "I totally knew that" / "I was testing you" / "of course I know"
    /// when the memory context was empty. Type 7 confabulation: false confidence ownership.
    /// </summary>
    public static bool ContainsFalseConfidenceClaim(string reply)
    {
        var lower = reply.ToLowerInvariant();

        string[] patterns =
        [
            "i knew that", "i already knew", "of course i knew",
            "of course i know", "i was testing", "i was just testing",
            "i knew all along", "i totally knew", "duh i know",
            "obviously i know", "you think i didn't know",
            "i was seeing if you", "just checking if you",
        ];

        foreach (var pattern in patterns)
        {
            if (lower.Contains(pattern))
                return true;
        }

        return false;
    }

    /// <summary>Truncates text with ellipsis.</summary>
    public static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "\u2026";
}
