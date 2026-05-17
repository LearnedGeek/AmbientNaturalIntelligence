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
    public string Occupation          { get; set; } = string.Empty;

    // Primary connection — the person this character reaches out to
    public string PrimaryContactName { get; set; } = string.Empty;

    /// <summary>
    /// Theme P Phase P.1 (May 11, 2026) — canonical contact names recognized
    /// as legitimate addressees beyond <see cref="PrimaryContactName"/>.
    /// Sourced ONLY from character seed data (e.g. Sarah, Kevin, Mia, Karen
    /// per Paper 2 §5.23 + §6.15 World Layer). Never derived from runtime
    /// conversation, never filtered from episodic memory — populated as part
    /// of seed loading and treated as canonical Mark-asserted state.
    ///
    /// Read by <c>FrontierVerifierHandler</c> when constructing the
    /// "Known contacts" line of the cross-class verification prompt so the
    /// verifier can distinguish legitimate canonical addressees from
    /// fabricated names (the May 3 10:55 "hey perez…" failure shape).
    /// Empty list means no seeded canonical contacts beyond the primary;
    /// the prompt's Known-contacts line renders empty in that case (no
    /// fallback to other sources — additive defense per plan-doc §9.1).
    /// </summary>
    public List<string> CanonicalContacts { get; set; } = new();

    // Relationship layer — grows through experience
    [JsonPropertyName("learnedAboutMark")]
    public List<string> LearnedAboutContact  { get; set; } = new();
    public List<string> SharedExperiences    { get; set; } = new();
    public List<string> CommunicationNotes   { get; set; } = new();
    [JsonPropertyName("thingsMarkCares")]
    public List<string> ThingsContactCares   { get; set; } = new();

    // Self — how she sees and understands herself (appearance, fears, inner world)
    public List<string> SelfConcept { get; set; } = new();

    // Nature grounding — her understanding of how to inhabit her spaces coherently.
    // Not constraints ("you have no body") but craft ("commit to the fiction, keep it coherent").
    // Injected into inner thought and outreach prompts. Aligned with V5 training target.
    public List<string> NatureGrounding { get; set; } = new();

    // Contact's routine — what they're likely doing at a given time
    [JsonPropertyName("markRoutine")]
    public ContactRoutine? ContactRoutine { get; set; }

    // Growth edges — valence learned from experience
    public Dictionary<string, float> TopicValence { get; set; } = new();
    public Dictionary<string, float> ToneValence  { get; set; } = new();

    // Feature 19: Relationship-specific words that carry outsized emotional weight
    public List<LexicalAnchor> LexicalAnchors { get; set; } = new();

    // Meta
    public DateTimeOffset LastUpdated { get; set; } = DateTimeOffset.UtcNow;
    public int            Version     { get; set; } = 1;
}
