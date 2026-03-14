namespace AniRuntime.Dashboard.Dtos;

public class MemoryRecordDto
{
    public Guid Id { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public float Importance { get; set; }
    public float RelationalValence { get; set; }
    public string? SourceName { get; set; }
    public bool IsAnchored { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? OccurredAt { get; set; }
}
