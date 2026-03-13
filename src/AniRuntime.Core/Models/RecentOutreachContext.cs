namespace AniRuntime.Core.Models;

/// <summary>
/// Feature 27: Outreach continuity context assembled once per cycle.
/// Injected into outreach decision and composition prompts so Ani
/// knows what she's already sent and whether it was answered.
/// </summary>
public class RecentOutreachContext
{
    public List<OutreachRecord> RecentMessages { get; set; } = new();
    public int UnansweredCount { get; set; }
    public TimeSpan? TimeSinceLastSend { get; set; }
    public TimeSpan? TimeSinceLastContactReply { get; set; }
}

public class OutreachRecord
{
    public string Message { get; set; } = string.Empty;
    public DateTimeOffset SentAt { get; set; }
    public bool WasAnswered { get; set; }
}
