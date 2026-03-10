namespace AniRuntime.Core.Models;

public class PerceptionEvent
{
    public string             SourceName    { get; set; } = string.Empty;
    public PerceptionCategory Category      { get; set; }
    public string             Summary       { get; set; } = string.Empty;
    public float              ContactRelevance { get; set; }
    public DateTimeOffset     OccurredAt    { get; set; } = DateTimeOffset.UtcNow;
    public Dictionary<string, object> Metadata { get; set; } = new();
}

public enum PerceptionCategory
{
    Environment,
    Calendar,
    Content,
    Communication,
    Social
}
