namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Resolves the correct IReplyChannel for a given channel ID.
/// Registered channel implementations are discovered via DI.
/// </summary>
public interface IReplyChannelResolver
{
    IReplyChannel Resolve(string channelId);
    IReplyChannel Default { get; }
}
