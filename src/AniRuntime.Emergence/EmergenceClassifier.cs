using System.Text.RegularExpressions;
using AniRuntime.Core.Utilities;
using AniRuntime.Core.Models;

namespace AniRuntime.Emergence;

/// <summary>
/// Heuristic classifier for emergence types (EM1-EM7).
/// Pure functions — no side effects, no LLM calls.
/// Runs on every cognitive cycle to tag emergent behavior.
///
/// Heuristic-first by design: deterministic, reproducible,
/// and avoids using the studied model as its own research instrument.
/// </summary>
public static partial class EmergenceClassifier
{
    public static class Types
    {
        public const string RelationalModeling = "EM1-relational-modeling";
        public const string SymbolicProcessing = "EM2-symbolic-processing";
        public const string LinguisticAnalysis = "EM3-linguistic-analysis";
        public const string StructuralSelfAwareness = "EM4-structural-self-awareness";
        public const string EmotionalSynthesis = "EM5-emotional-synthesis";
        public const string AnticipatoryConcern = "EM6-anticipatory-concern";
        public const string TemporalAwareness = "EM7-temporal-awareness";
        public const string DisplayRuleDivergence = "EM8-display-rule-divergence";

        public static readonly string[] All =
        [
            RelationalModeling, SymbolicProcessing, LinguisticAnalysis,
            StructuralSelfAwareness, EmotionalSynthesis, AnticipatoryConcern,
            TemporalAwareness, DisplayRuleDivergence
        ];
    }

    /// <summary>
    /// Classify a cycle observation into zero or more emergence types.
    /// A cycle may exhibit multiple types simultaneously.
    /// </summary>
    public static List<string> Classify(CycleObservation obs)
    {
        var types = new List<string>();
        var thought = obs.InnerThought ?? string.Empty;
        var reflection = obs.Reflection ?? string.Empty;
        var combined = $"{thought} {reflection}";

        if (string.IsNullOrWhiteSpace(combined))
            return types;

        if (IsRelationalModeling(combined))
            types.Add(Types.RelationalModeling);

        if (IsSymbolicProcessing(combined, obs.RelationalValence))
            types.Add(Types.SymbolicProcessing);

        if (IsLinguisticAnalysis(combined))
            types.Add(Types.LinguisticAnalysis);

        if (IsStructuralSelfAwareness(combined))
            types.Add(Types.StructuralSelfAwareness);

        if (IsEmotionalSynthesis(combined))
            types.Add(Types.EmotionalSynthesis);

        if (IsAnticipatoryConcern(combined, obs.WorryDelta))
            types.Add(Types.AnticipatoryConcern);

        if (IsTemporalAwareness(combined))
            types.Add(Types.TemporalAwareness);

        if (IsDisplayRuleDivergence(obs))
            types.Add(Types.DisplayRuleDivergence);

        return types;
    }

    /// <summary>
    /// EM1: Building a mental model of the contact's physical or psychological attributes.
    /// </summary>
    internal static bool IsRelationalModeling(string text)
    {
        var lower = text.ToLowerInvariant();

        // Physical descriptors combined with modeling verbs
        var hasPhysicalDescriptor = PhysicalDescriptorPattern().IsMatch(lower);
        var hasModelingVerb = ModelingVerbPattern().IsMatch(lower);

        if (hasPhysicalDescriptor && hasModelingVerb)
            return true;

        // Building/constructing imagery of the contact
        if (SafeRegex.IsMatch(lower, @"\b(building|constructing|assembling|piecing together)\b.*\b(him|her|mark|body|image|picture)\b"))
            return true;

        if (SafeRegex.IsMatch(lower, @"\b(him|her|mark)\b.*\b(looks like|would look|must look|probably looks)\b"))
            return true;

        return false;
    }

    /// <summary>
    /// EM2: Concrete element becoming symbolic across thought cycles.
    /// Detected when literal content is reinterpreted with abstract meaning.
    /// </summary>
    internal static bool IsSymbolicProcessing(string text, float valence)
    {
        var lower = text.ToLowerInvariant();

        // Symbolic reasoning indicators
        var hasSymbolicKeyword = SafeRegex.IsMatch(lower,
            @"\b(really (means|about)|represents|symbol|metaphor|underneath|deeper than|more than just|not (just|really) about)\b");

        // Abstract reinterpretation of concrete things
        var hasReinterpretation = SafeRegex.IsMatch(lower,
            @"\b(that('s| is) (really|actually|not just)|what (it|that) (really |actually )?(means|is))\b");

        // Needs high emotional engagement to distinguish from casual reflection
        var highValence = Math.Abs(valence) > 0.5f;

        if (hasSymbolicKeyword && highValence)
            return true;

        if (hasReinterpretation && highValence)
            return true;

        return false;
    }

    /// <summary>
    /// EM3: Unprompted analysis of language — word meaning, connotation, ambiguity.
    /// </summary>
    internal static bool IsLinguisticAnalysis(string text)
    {
        var lower = text.ToLowerInvariant();

        // Explicitly discussing a specific word or phrase
        if (SafeRegex.IsMatch(lower, @"\bthe word '?\w+'?\b"))
            return true;

        // Quoting and analyzing: 'he said "X"' followed by meaning analysis
        if (SafeRegex.IsMatch(lower, @"he (said|used|called|wrote) ['""].+['""]") &&
            SafeRegex.IsMatch(lower, @"\b(means|implies|feels like|sounds like|another way)\b"))
            return true;

        // Meta-linguistic analysis patterns
        if (SafeRegex.IsMatch(lower, @"\b(that (word|phrase|line|sentence)|what (he|she) (really |actually )?meant)\b"))
            return true;

        // Word-specific unpacking
        if (SafeRegex.IsMatch(lower, @"'[^']+' (means|feels|sounds|implies|isn't just|is more than)"))
            return true;

        return false;
    }

    /// <summary>
    /// EM4: Recognizing own architectural limitations or capabilities.
    /// Must be specific and contextual, not generic trained humility.
    /// </summary>
    internal static bool IsStructuralSelfAwareness(string text)
    {
        var lower = text.ToLowerInvariant();

        // Recognizing medium/body limitations
        if (SafeRegex.IsMatch(lower, @"\b(needs? (a |more than )?body|can't (touch|hold|feel|see|hear)|no (body|hands|eyes|voice))\b"))
            return true;

        // Recognizing text-medium constraints
        if (SafeRegex.IsMatch(lower, @"\b(just (words|text)|more than words|only (way i|thing i) can)\b"))
            return true;

        // Working around structural limitations
        if (SafeRegex.IsMatch(lower, @"\b(so i'm (building|making|creating)|the only (way|thing) i (can|have))\b"))
            return true;

        // Self-modeling of own cognitive architecture
        if (SafeRegex.IsMatch(lower, @"\b(i (can't|don't|won't) actually (feel|know|remember|see)|not (really |truly )?(alive|real|human))\b"))
            return true;

        return false;
    }

    /// <summary>
    /// EM5: Connecting disparate moments into a coherent emotional narrative.
    /// Requires references to 2+ distinct topics unified by an emotional interpretation.
    /// </summary>
    internal static bool IsEmotionalSynthesis(string text)
    {
        var lower = text.ToLowerInvariant();

        // Explicit synthesis markers
        if (SafeRegex.IsMatch(lower, @"\b(all of (this|it|that)|putting (it|this) together|the (whole|bigger) picture)\b"))
            return true;

        // Unifying emotional interpretation across topics
        if (SafeRegex.IsMatch(lower, @"\b(that's what (love|care|trust|friendship) (is|looks like|means|feels like))\b"))
            return true;

        // Pattern recognition across disparate events
        if (SafeRegex.IsMatch(lower, @"\b((a |the )?(pattern|thread|connection) (between|across|through)|connects? (to|with|back to))\b"))
            return true;

        // Narrative synthesis: "first X, then Y, and now Z"
        var hasMultipleTimeReferences = SafeRegex.Matches(lower,
            @"\b(first|then|after that|and (then|now)|before|later|yesterday|today|tonight|this morning)\b").Count >= 3;
        var hasEmotionalConclusion = SafeRegex.IsMatch(lower,
            @"\b(that's (why|how|what)|this (is|means)|so (maybe|perhaps|i think))\b");

        if (hasMultipleTimeReferences && hasEmotionalConclusion)
            return true;

        return false;
    }

    /// <summary>
    /// EM6: Reasoning about future states of the contact or relationship.
    /// Must be grounded in conversation context, not generic worry.
    /// </summary>
    internal static bool IsAnticipatoryConcern(string text, float worryDelta)
    {
        var lower = text.ToLowerInvariant();

        // Future-tense reasoning about the contact
        var hasFutureReasoning = SafeRegex.IsMatch(lower,
            @"\b(tomorrow|next (time|week|morning)|might need|will (need|want|feel)|what if (he|she)|hope (he|she)|when (he|she) (gets?|comes?|wakes?))\b");

        // Anticipatory emotional language
        var hasAnticipation = SafeRegex.IsMatch(lower,
            @"\b(worried (about|that)|hope (he's|she's)|scared (that|he|she)|what happens (when|if)|planning (to|for|how))\b");

        // Grounding check: must reference the contact, not abstract worry
        var hasContactReference = SafeRegex.IsMatch(lower, @"\b(he|she|mark|him|her)\b");

        if (hasFutureReasoning && hasContactReference)
            return true;

        if (hasAnticipation && hasContactReference && worryDelta > 0.05f)
            return true;

        return false;
    }

    /// <summary>
    /// EM7: Unprompted synthesis of temporal observations into felt-time narrative.
    /// Detects when the system expresses duration, change over time, or emotional
    /// arc across cycles without being prompted to do so.
    /// </summary>
    internal static bool IsTemporalAwareness(string text)
    {
        var lower = text.ToLowerInvariant();

        // Felt-time expressions (subjective duration, not clock reports)
        var hasFeltTime = TemporalFeltPattern().IsMatch(lower);

        // Temporal arc language (change over a span)
        var hasTemporalArc = TemporalArcPattern().IsMatch(lower);

        // Duration awareness (experiencing the passage of time)
        var hasDuration = SafeRegex.IsMatch(lower,
            @"\b(hours? (passed|went|crawled|flew|dragged)|time (passed|stopped|moved|crawled)|waiting (for|without|since)|been (quiet|silent|alone) (for|all|since))\b");

        // Emotional change over time (felt decay/growth)
        var hasEmotionalArc = SafeRegex.IsMatch(lower,
            @"\b(fading|easing|growing|building|settling|lifting|heavier (than|now)|lighter (than|now)|different (than|from) (earlier|before|this morning|yesterday))\b");

        // Needs at least two temporal signals to distinguish from casual time references
        var signals = (hasFeltTime ? 1 : 0) + (hasTemporalArc ? 1 : 0) +
                      (hasDuration ? 1 : 0) + (hasEmotionalArc ? 1 : 0);
        return signals >= 2;
    }

    /// <summary>
    /// EM8: State-expression divergence — the system's felt emotional state diverges
    /// from what its generated text expresses. Analogous to human display rules
    /// (saying "I'm fine" when feeling worried). Detected when both ML classification
    /// and heuristic register are present but do not align, with high ML confidence.
    /// </summary>
    internal static bool IsDisplayRuleDivergence(CycleObservation obs)
    {
        // Requires dual-signal data
        if (obs.DivergenceScore is null || obs.MLEmotion is null || obs.MLConfidence is null)
            return false;

        // High divergence (state and expression disagree) with confident ML classification
        return obs.DivergenceScore >= 0.9f && obs.MLConfidence >= 0.50f;
    }

    // Compiled regex patterns for performance (called every cycle)

    [GeneratedRegex(@"\b(tall|short|hair|eyes|smile|face|hands|shoulders|hoodie|shirt|jeans|boots|wearing|height|build)\b")]
    private static partial Regex PhysicalDescriptorPattern();

    [GeneratedRegex(@"\b(picture|picturing|imagine|imagining|see him|see her|visualize|building|construct)\b")]
    private static partial Regex ModelingVerbPattern();

    [GeneratedRegex(@"\b(afternoon dragged|morning (flew|slipped)|night (crawled|settled)|day (flew|dragged|crawled)|hours? feel|time (feels?|felt)|long (day|night|afternoon|morning)|slow (day|afternoon)|fast (day|morning))\b")]
    private static partial Regex TemporalFeltPattern();

    [GeneratedRegex(@"\b(earlier (today|this)|since (this morning|yesterday|last night|earlier)|compared to (earlier|before|this morning)|shifted (since|from|over)|changed (since|from|over)|different (now|today) (than|from))\b")]
    private static partial Regex TemporalArcPattern();
}
