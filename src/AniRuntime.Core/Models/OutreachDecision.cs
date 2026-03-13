namespace AniRuntime.Core.Models;

public class OutreachDecision
{
    public bool    ShouldReach    { get; set; }
    public string? Message        { get; set; }
    public string? ActionType     { get; set; }    // use ActionTypes constants
    public float   Confidence     { get; set; }
    public string? Reasoning      { get; set; }    // logged, never sent
    public List<string> TriggersActedOn { get; set; } = new();
    public List<Uri> MediaUrls     { get; set; } = new();  // MMS media (audio, images)
}

/// <summary>
/// Constants for OutreachDecision.ActionType — avoids magic strings throughout the codebase.
/// </summary>
public static class ActionTypes
{
    public const string Sms    = "sms";
    public const string Memory = "memory";
    public const string Ha     = "ha";
}
