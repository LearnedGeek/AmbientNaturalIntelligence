using AniRuntime.Core.Interfaces;

namespace AniRuntime.Eval;

/// <summary>
/// <see cref="IReplyChannelResolver"/> override that returns the
/// <see cref="CapturingReplyChannel"/> for every channel ID. Production
/// resolution logic (matching inbound contact channel to the correct
/// reply transport) is bypassed because eval intercepts all dispatches.
///
/// <para>
/// Registered AFTER <see cref="AniRuntimeServiceContainer.AddAniRuntimeCore"/>
/// so it shadows the production <c>ReplyChannelResolver</c> binding via
/// the standard .NET DI last-registration-wins semantics.
/// </para>
/// </summary>
internal sealed class CapturingReplyChannelResolver(CapturingReplyChannel channel)
    : IReplyChannelResolver
{
    public IReplyChannel Resolve(string channelId) => channel;
    public IReplyChannel Default => channel;
}
