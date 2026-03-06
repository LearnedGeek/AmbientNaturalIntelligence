namespace AniRuntime.Core.Models;

public class OpenLoop
{
    public Guid            Id             { get; set; } = Guid.NewGuid();
    public string          Description    { get; set; } = string.Empty;
    public string          Context        { get; set; } = string.Empty;
    public float           Urgency        { get; set; }
    public bool            IsResolved     { get; set; }
    public DateTimeOffset  CreatedAt      { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ResolvedAt     { get; set; }
    public DateTimeOffset? FollowUpAfter  { get; set; }
}
