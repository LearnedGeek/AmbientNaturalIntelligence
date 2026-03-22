using AniRuntime.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace AniRuntime.Dashboard;

/// <summary>
/// Reply channel for dashboard chat. No delivery action needed —
/// the Blazor UI polls the conversation thread directly for replies.
/// Persistence is handled by ConversationReplyPhase regardless of channel.
/// </summary>
public class DashboardReplyChannel : IReplyChannel
{
    private readonly ILogger<DashboardReplyChannel> _log;

    public string ChannelId => "dashboard";

    public DashboardReplyChannel(ILogger<DashboardReplyChannel> log)
    {
        _log = log;
    }

    public Task SendReplyAsync(string message, CancellationToken ct = default)
    {
        _log.LogInformation("Dashboard reply (no SMS): {Reply}",
            message.Length > 80 ? message[..80] : message);
        return Task.CompletedTask;
    }
}
