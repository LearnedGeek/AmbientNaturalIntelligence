namespace AniRuntime.Core;

/// <summary>
/// All timing and behavioural tuning lives here.
/// Bound from appsettings.json "Ani" section.
/// Adjust these values to tune Ani's presence without touching code.
/// </summary>
public class AniOptions
{
    // Timing — cognitive cycle
    public double DesireLambdaMinutes    { get; set; } = 8.0;
    public double ThinkTargetProbability { get; set; } = 0.70;
    public double MinWakeMinutes         { get; set; } = 2.0;
    public double MaxWakeMinutes         { get; set; } = 45.0;

    // Outreach gating
    public double CooldownMinutes        { get; set; } = 20.0;
    public double MinOutreachGapMinutes  { get; set; } = 60.0;
    public int    MaxOutreachPerDay      { get; set; } = 4;

    // Outreach threshold — randomized between Floor and Floor+Range each cycle
    public double OutreachThresholdFloor { get; set; } = 0.55;
    public double OutreachThresholdRange { get; set; } = 0.30;

    // Desire drift — per-cycle accumulation rate and cap
    public double DriftPerHour           { get; set; } = 0.08;
    public double DriftCapPerCycle       { get; set; } = 0.4;

    // Trigger weight multiplier — how much a trigger raises desire
    public double TriggerDesireMultiplier { get; set; } = 0.15;

    // Valence threshold — thoughts above this add a spontaneous trigger
    public double ValenceTriggerThreshold { get; set; } = 0.6;

    // Storage paths (relative to service working directory)
    public string CharacterStatePath     { get; set; } = "data/character-state.json";
    public string MemoryDbPath           { get; set; } = "data/ani-memory.db";
}

public class OllamaOptions
{
    public string BaseUrl              { get; set; } = "http://localhost:11434";
    public string ChatModel            { get; set; } = "llama3.2";
    public string? InnerMonologueModel { get; set; }
    public string EmbedModel           { get; set; } = "nomic-embed-text";

    public string ResolvedInnerMonologueModel => InnerMonologueModel ?? ChatModel;
}

public class TwilioOptions
{
    public string AccountSid  { get; set; } = string.Empty;
    public string AuthToken   { get; set; } = string.Empty;
    public string FromNumber  { get; set; } = string.Empty;
    public string ToNumber    { get; set; } = string.Empty;
}

public class RssOptions
{
    public bool          Enabled         { get; set; } = true;
    public List<RssFeed> Feeds           { get; set; } = new();
    public int           MaxItemsPerFeed { get; set; } = 3;
}

public class RssFeed
{
    public string Name { get; set; } = string.Empty;
    public string Url  { get; set; } = string.Empty;
}
