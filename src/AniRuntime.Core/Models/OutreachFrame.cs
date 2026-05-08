namespace AniRuntime.Core.Models;

/// <summary>
/// Theme N (May 7-8, 2026) — the source-frame an outreach composes against.
/// Mirrors Paper 2 §6.14 layer 2 Frame Detection but for Ani-initiated
/// outreach rather than inbound replies.
///
/// Each frame has a corresponding tier (provenance) the substrate-anchor is
/// drawn from, and a corresponding honest framing the composition prompt
/// constrains the model to use:
///
/// <list type="table">
/// <listheader>
///   <term>Frame</term><term>Provenance tier</term><term>Honest framing</term>
/// </listheader>
/// <item>
///   <term><see cref="Shared"/></term>
///   <term>Facts (Mark-asserted)</term>
///   <term>"remember when we..." / "that thing you said about..."</term>
/// </item>
/// <item>
///   <term><see cref="AniDomain"/></term>
///   <term>Facts (character-seed canonical)</term>
///   <term>"the bookstore is..." / "sarah came in today..."</term>
/// </item>
/// <item>
///   <term><see cref="AniInterior"/></term>
///   <term>Interior</term>
///   <term>"i was just thinking about..." / "i had this thought that..."</term>
/// </item>
/// <item>
///   <term><see cref="WorldPerception"/></term>
///   <term>Facts (rss/weather/perception)</term>
///   <term>"i saw this article..." / "the snow is..."</term>
/// </item>
/// <item>
///   <term><see cref="None"/></term>
///   <term>—</term>
///   <term>Suppress outreach (no clean substrate to anchor against)</term>
/// </item>
/// </list>
/// </summary>
public enum OutreachFrameType
{
    /// <summary>No frame selected — substrate too thin to ground an outreach honestly. Producer should suppress.</summary>
    None = 0,

    /// <summary>Anchored in real prior Mark-asserted shared experience (Facts tier).</summary>
    Shared,

    /// <summary>Anchored in Ani's canonical world (character-seed, World Layer; Facts tier).</summary>
    AniDomain,

    /// <summary>Anchored in Ani's interior content (inner thoughts, reflections; Interior tier). Honestly framed as interior.</summary>
    AniInterior,

    /// <summary>Anchored in a real perception event (RSS, weather, calendar; Facts tier).</summary>
    WorldPerception,
}

/// <summary>
/// Theme N — result of <see cref="Interfaces.IOutreachFrameSelector"/> selection.
/// Carries the chosen frame, the substrate item to anchor composition against,
/// and a confidence score for telemetry.
///
/// When <see cref="FrameType"/> is <see cref="OutreachFrameType.None"/>,
/// <see cref="Anchor"/> is the empty string and <see cref="Confidence"/> is 0 —
/// the producer (OutreachPhase) suppresses dispatch.
/// </summary>
/// <param name="FrameType">The selected source-frame.</param>
/// <param name="Anchor">A short prose excerpt of the substrate item the model anchors against. Empty when <see cref="FrameType"/> is <see cref="OutreachFrameType.None"/>.</param>
/// <param name="Confidence">0-1 confidence score for telemetry. 0 when <see cref="FrameType"/> is <see cref="OutreachFrameType.None"/>.</param>
public sealed record OutreachFrame(
    OutreachFrameType FrameType,
    string            Anchor,
    float             Confidence)
{
    /// <summary>The canonical "no frame; suppress outreach" result.</summary>
    public static OutreachFrame None { get; } = new(OutreachFrameType.None, string.Empty, 0f);
}
