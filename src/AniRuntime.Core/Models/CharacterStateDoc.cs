using System.Text.Json.Serialization;

namespace AniRuntime.Core.Models;

/// <summary>
/// Primary contact's known daily routine — what they're likely doing at a given time.
/// Used by ContactStatePerceptionSource to infer the contact's current state.
/// Times are local (Central Time).
/// </summary>
public class ContactRoutine
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
    [JsonPropertyName("learnedAboutMark")]
    public List<string> LearnedAboutContact  { get; set; } = new();
    public List<string> SharedExperiences    { get; set; } = new();
    public List<string> CommunicationNotes   { get; set; } = new();
    [JsonPropertyName("thingsMarkCares")]
    public List<string> ThingsContactCares   { get; set; } = new();

    // Self — how she sees and understands herself (appearance, fears, inner world)
    public List<string> SelfConcept { get; set; } = new();

    // Contact's routine — what they're likely doing at a given time
    [JsonPropertyName("markRoutine")]
    public ContactRoutine? ContactRoutine { get; set; }

    // Growth edges — valence learned from experience
    public Dictionary<string, float> TopicValence { get; set; } = new();
    public Dictionary<string, float> ToneValence  { get; set; } = new();

    // Meta
    public DateTimeOffset LastUpdated { get; set; } = DateTimeOffset.UtcNow;
    public int            Version     { get; set; } = 1;
}
