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

    /// <summary>
    /// May 1, 2026 — true when this dispatch is administrative metadata
    /// (admin-tag confirmation, test-mode reply, etc.), not relational
    /// outreach. The dispatch pipeline skips enrichment paths that are
    /// inappropriate for meta-dispatches: voice/audio synthesis, image
    /// selection, etc. Admin-tag confirmations should be plain SMS so
    /// they don't accidentally render the substrate as a voice message
    /// to Mark when he's just trying to flag something for review.
    /// </summary>
    public bool IsAdminMeta { get; set; }
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
