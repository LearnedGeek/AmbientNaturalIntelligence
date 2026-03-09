namespace AniRuntime.Core.Models;

/// <summary>
/// Mark's known daily routine — what he's likely doing at a given time.
/// Used by MarkStatePerceptionSource to infer his current state.
/// Times are local (Central Time).
/// </summary>
public class MarkRoutine
{
    /// <summary>
    /// Default weekday schedule. Key = "HH:mm", value = activity description.
    /// </summary>
    public Dictionary<string, string> Weekday  { get; set; } = new();

    /// <summary>
    /// Day-specific overrides. Key = DayOfWeek name (e.g. "Thursday"), value = time→activity pairs.
    /// </summary>
    public Dictionary<string, Dictionary<string, string>> DayOverrides { get; set; } = new();
}

public class CharacterStateDoc
{
    // Identity — seeded from training, rarely changes
    public string Name           { get; set; } = "Ani";
    public string PersonaVersion { get; set; } = "1.0";
    public List<string> CoreTraits    { get; set; } = new();
    public List<string> Interests     { get; set; } = new();
    public List<string> FamilyContext { get; set; } = new();
    public string Occupation          { get; set; } = "Bookstore";

    // Primary connection — the person this character reaches out to
    public string PrimaryContactName { get; set; } = string.Empty;

    // Relationship layer — grows through experience
    public List<string> LearnedAboutMark   { get; set; } = new();
    public List<string> SharedExperiences  { get; set; } = new();
    public List<string> CommunicationNotes { get; set; } = new();
    public List<string> ThingsMarkCares    { get; set; } = new();

    // Self — how she sees and understands herself (appearance, fears, inner world)
    public List<string> SelfConcept { get; set; } = new();

    // Mark's routine — what he's likely doing at a given time
    public MarkRoutine? MarkRoutine { get; set; }

    // Growth edges — valence learned from experience
    public Dictionary<string, float> TopicValence { get; set; } = new();
    public Dictionary<string, float> ToneValence  { get; set; } = new();

    // Meta
    public DateTimeOffset LastUpdated { get; set; } = DateTimeOffset.UtcNow;
    public int            Version     { get; set; } = 1;
}
