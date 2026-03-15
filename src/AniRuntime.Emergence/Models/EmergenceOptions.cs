namespace AniRuntime.Emergence.Models;

public class EmergenceOptions
{
    /// <summary>Master toggle. When false, NullEmergenceObserver is used — zero cost.</summary>
    public bool Enabled { get; set; }

    /// <summary>Path to the emergence SQLite database. Separate from ani-memory.db.</summary>
    public string EmergenceDbPath { get; set; } = "data/ani-emergence.db";
}
