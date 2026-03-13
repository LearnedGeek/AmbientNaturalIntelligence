namespace AniRuntime.Core.Models;

public class LexicalAnchor
{
    public string Word { get; set; } = string.Empty;
    public float WarmthDelta { get; set; }
    public float EnergyDelta { get; set; }
    public float ConcernDelta { get; set; }
    public float PlayfulnessDelta { get; set; }
    public string? Context { get; set; }
    public bool DecaysOnRepetition { get; set; }
    public int TimesHeard { get; set; }
}
