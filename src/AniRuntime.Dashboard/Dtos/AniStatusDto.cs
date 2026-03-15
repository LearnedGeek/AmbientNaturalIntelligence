namespace AniRuntime.Dashboard.Dtos;

public class AniStatusDto
{
    public string Name { get; set; } = string.Empty;
    public string PrimaryContact { get; set; } = string.Empty;
    public string PersonaVersion { get; set; } = string.Empty;
    public EmotionalStateDto EmotionalState { get; set; } = new();
    public DesireStateDto DesireState { get; set; } = new();
    public string? RelationshipPhase { get; set; }
    public float? ContactGapTension { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}

public class EmotionalStateDto
{
    public float Warmth { get; set; }
    public float Energy { get; set; }
    public float Worry { get; set; }
    public float Playfulness { get; set; }
    public string? Mood { get; set; }
}

public class DesireStateDto
{
    public float DesireToConnect { get; set; }
    public bool CooldownActive { get; set; }
    public DateTimeOffset? CooldownUntil { get; set; }
    public float CircadianModifier { get; set; }
    public DateTimeOffset? LastContactInbound { get; set; }
}

public class EmotionalHistoryPointDto
{
    public float Warmth { get; set; }
    public float Energy { get; set; }
    public float Worry { get; set; }
    public float Playfulness { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}
