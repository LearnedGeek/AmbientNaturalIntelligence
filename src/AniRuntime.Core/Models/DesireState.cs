namespace AniRuntime.Core.Models;

public record DesireState
{
    public float          DesireToConnect      { get; set; }        // 0.0 – 1.0, builds over time
    public float          OutreachThreshold    { get; set; }        // randomised each evaluation
    public bool           CooldownActive       { get; set; }
    public DateTimeOffset LastOutreach         { get; set; }
    public DateTimeOffset LastInnerThought     { get; set; }
    public DateTimeOffset LastMarkContact      { get; set; }
    public List<DesireTrigger> ActiveTriggers  { get; set; } = new();
    public float          CircadianModifier    { get; set; } = 1.0f;
}

public class DesireTrigger
{
    public TriggerType    Type        { get; set; }
    public float          Weight      { get; set; }
    public string         Description { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt   { get; set; } = DateTimeOffset.UtcNow;
}

public enum TriggerType
{
    TemporalDrift,
    OpenLoop,
    AssociativeFire,
    EmotionalResidue,
    SpontaneousThought,
    ContextualMoment,
    IntegrationEvent
}
