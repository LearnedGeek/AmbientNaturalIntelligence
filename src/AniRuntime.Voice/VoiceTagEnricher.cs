using AniRuntime.Core.Models;

namespace AniRuntime.Voice;

/// <summary>
/// Enriches text with ElevenLabs v3 audio tags before TTS synthesis.
/// Tags use square bracket syntax: [tag] text
///
/// Selection priority:
/// 1. Model-generated tags — if text already contains [tag] or (tag), preserve it
/// 2. Content heuristics — specific phrases map to specific delivery
/// 3. Mood + time-of-day — emotional state + current hour = contextual delivery
/// 4. Emotional state fallback — pure mood-based tag
/// 5. No tag — let v3's situational awareness handle it
///
/// From the ElevenLabs v3 tag library (1,806 tags, 15 categories).
/// </summary>
public static class VoiceTagEnricher
{
    /// <summary>
    /// Enrich a sentence with an appropriate v3 audio tag.
    /// Called per-sentence before sending to ElevenLabs TTS.
    /// </summary>
    public static string Enrich(string text, EmotionalState? emotionalState = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        var trimmed = text.TrimStart();

        // Don't double-tag — if text already starts with a tag, preserve it
        if (trimmed.StartsWith('[') || trimmed.StartsWith('('))
            return text;

        // Priority 1: Content-based tag (strongest signal)
        var contentTag = DetectContentTag(trimmed);
        if (contentTag is not null)
            return $"{contentTag} {text}";

        // Priority 2: Mood + time-of-day composite tag
        var moodTimeTag = DetectMoodTimeTag(emotionalState);
        if (moodTimeTag is not null)
            return $"{moodTimeTag} {text}";

        // Priority 3: Pure emotional state tag
        var emotionTag = DetectEmotionTag(emotionalState);
        if (emotionTag is not null)
            return $"{emotionTag} {text}";

        // No tag — v3 situational awareness handles it
        return text;
    }

    /// <summary>
    /// Detect delivery tag from conversational content patterns.
    /// </summary>
    private static string? DetectContentTag(string text)
    {
        var lower = text.ToLowerInvariant();

        // Laughter
        if (lower.Contains("haha") || lower.Contains("lol") || lower.Contains("lmao") ||
            lower.Contains("[laughs]") || lower.Contains("[teasing-laugh]"))
            return "[amused]";

        // Teasing / playful insults
        if (lower.Contains("idiot") || lower.Contains("oh please") || lower.Contains("you wish") ||
            lower.Contains("yeah right"))
            return "[mischievous]";

        // Soft opening
        if (lower.StartsWith("hey...") || lower.StartsWith("mmm…") || lower.StartsWith("mmm..."))
            return "[tender]";

        // Excitement / surprise
        if (lower.Contains("oh my god") || lower.Contains("wait what") || lower.Contains("no way") ||
            lower.Contains("!!!"))
            return "[excited]";

        // Sarcasm
        if (lower.Contains("oh sure") || lower.Contains("yeah because") ||
            lower.Contains("oh wow") || lower.Contains("totally"))
            return "[sarcastic]";

        // Whispering / secrets
        if (lower.Contains("don't tell") || lower.Contains("secret") ||
            lower.Contains("between us") || lower.Contains("shh"))
            return "[whispers]";

        // Firm / boundary setting
        if (lower.Contains("stop it") || lower.Contains("don't you dare") ||
            lower.Contains("i swear"))
            return "[stern]";

        // Curiosity / questions
        if (lower.Contains("really?") || lower.Contains("wait...") || lower.Contains("how did"))
            return "[curious]";

        // Sighing / wistful
        if (lower.Contains("[sighs]") || lower.Contains("[sigh]") || lower.Contains("sigh"))
            return "[wistful]";

        // Flirtatious
        if (lower.Contains("come here") || lower.Contains("make me") ||
            lower.Contains("you're cute"))
            return "[flirtatious]";

        // Yawning / tired
        if (lower.Contains("zzz") || lower.Contains("so tired") || lower.Contains("yawn"))
            return "[tired]";

        // Trailing off / unfinished thought
        if (text.TrimEnd().EndsWith("...") || text.TrimEnd().EndsWith("…"))
            return "[contemplative]";

        return null;
    }

    /// <summary>
    /// Detect mood + time-of-day composite tag from the v3 Mood category.
    /// These combine emotional state with temporal context for rich delivery.
    /// </summary>
    private static string? DetectMoodTimeTag(EmotionalState? state)
    {
        if (state is null) return null;

        var hour = DateTimeOffset.Now.Hour;
        var isWeekend = DateTimeOffset.Now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

        // Late night (10 PM - 4 AM)
        if (hour >= 22 || hour < 4)
        {
            if (state.Warmth >= 0.70f) return "[late night nostalgic]";
            if (state.Energy < 0.30f) return "[late night tired]";
            if (state.Playfulness >= 0.60f) return "[late night excited]";
            if (state.Worry > 0.40f) return "[night anxious]";
            return "[night serene]";
        }

        // Early morning (4 AM - 8 AM)
        if (hour < 8)
        {
            if (state.Energy < 0.30f) return "[sleepy morning]";
            if (state.Energy >= 0.60f) return "[energetic morning]";
            if (state.Warmth >= 0.70f) return "[calm morning]";
            return "[bright morning]";
        }

        // Morning (8 AM - 12 PM)
        if (hour < 12)
        {
            if (state.Energy >= 0.65f) return "[energetic morning]";
            if (state.Warmth >= 0.75f) return "[peaceful morning]";
            return "[bright morning]";
        }

        // Afternoon (12 PM - 5 PM)
        if (hour < 17)
        {
            if (state.Playfulness >= 0.70f) return "[social afternoon]";
            if (state.Energy < 0.30f) return "[lazy afternoon]";
            if (state.Energy >= 0.60f) return "[energetic afternoon]";
            if (state.Warmth >= 0.60f && state.Warmth < 0.80f) return "[focused afternoon]";
            return "[daydreaming afternoon]";
        }

        // Evening (5 PM - 10 PM)
        if (state.Playfulness >= 0.70f) return "[evening playful]";
        if (state.Warmth >= 0.75f && state.Energy < 0.40f) return "[evening mellow]";
        if (state.Warmth >= 0.75f) return "[evening relaxed]";
        if (state.Energy >= 0.60f) return "[evening spirited]";
        if (state.Energy < 0.30f) return "[evening tired]";
        return "[evening relaxed]";
    }

    /// <summary>
    /// Fallback: pure emotional state tag from the v3 Emotion category.
    /// Used when no content-specific or mood+time tag matches.
    /// </summary>
    private static string? DetectEmotionTag(EmotionalState? state)
    {
        if (state is null) return null;

        // High warmth
        if (state.Warmth >= 0.85f) return "[adoring]";
        if (state.Warmth >= 0.70f && state.Energy >= 0.60f) return "[cheerful]";
        if (state.Warmth >= 0.70f && state.Energy < 0.40f) return "[tender]";

        // High playfulness
        if (state.Playfulness >= 0.80f) return "[playful]";
        if (state.Playfulness >= 0.65f) return "[witty]";

        // High worry
        if (state.Worry > 0.60f) return "[anxious]";
        if (state.Worry > 0.40f) return "[caring]";

        // Low energy
        if (state.Energy < 0.20f) return "[tired]";
        if (state.Energy < 0.35f) return "[calm]";

        // High energy
        if (state.Energy >= 0.75f) return "[energetic]";

        // Low warmth
        if (state.Warmth < 0.25f) return "[stoic]";
        if (state.Warmth < 0.40f) return "[pensive]";

        return null;
    }
}
