using AniRuntime.Core.Interfaces;

namespace AniRuntime.Core;

/// <summary>
/// Maps channel IDs to IReplyChannel implementations.
/// All registered IReplyChannel instances are injected via DI.
/// Falls back to the first registered channel (typically SMS) as default.
/// </summary>
public class ReplyChannelResolver : IReplyChannelResolver
{
    private readonly Dictionary<string, IReplyChannel> _channels;
    public IReplyChannel Default { get; }

    public ReplyChannelResolver(IEnumerable<IReplyChannel> channels)
    {
        _channels = channels.ToDictionary(c => c.ChannelId, StringComparer.OrdinalIgnoreCase);
        Default = _channels.Values.First();
    }

    public IReplyChannel Resolve(string channelId)
    {
        return _channels.TryGetValue(channelId, out var channel) ? channel : Default;
    }
}
