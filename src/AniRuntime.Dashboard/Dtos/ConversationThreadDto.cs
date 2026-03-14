namespace AniRuntime.Dashboard.Dtos;

public class ConversationThreadDto
{
    public Guid Id { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public int MessageCount { get; set; }
    public List<ConversationMessageDto> Messages { get; set; } = new();
}

public class ConversationMessageDto
{
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTimeOffset SentAt { get; set; }
}
