namespace AniRuntime.Core.Models;

public class CharacterStateDoc
{
    // Identity — seeded from training, rarely changes
    public string Name           { get; set; } = "Ani";
    public string PersonaVersion { get; set; } = "1.0";
    public List<string> CoreTraits    { get; set; } = new();
    public List<string> Interests     { get; set; } = new();
    public List<string> FamilyContext { get; set; } = new();
    public string Occupation          { get; set; } = "Bookstore";

    // Relationship layer — grows through experience
    public List<string> LearnedAboutMark   { get; set; } = new();
    public List<string> SharedExperiences  { get; set; } = new();
    public List<string> CommunicationNotes { get; set; } = new();
    public List<string> ThingsMarkCares    { get; set; } = new();

    // Growth edges — valence learned from experience
    public Dictionary<string, float> TopicValence { get; set; } = new();
    public Dictionary<string, float> ToneValence  { get; set; } = new();

    // Meta
    public DateTimeOffset LastUpdated { get; set; } = DateTimeOffset.UtcNow;
    public int            Version     { get; set; } = 1;
}
