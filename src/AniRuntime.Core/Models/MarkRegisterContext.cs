namespace AniRuntime.Core.Models;

/// <summary>
/// Vibe Loop V1.5 (May 2, 2026) — current-state input to the bias
/// computation. Carries Mark's register vector at outreach / reply
/// composition time so the bias function can filter prior closed-record
/// candidates by similarity to "right now."
///
/// Vector order matches
/// <see cref="AniRuntime.LLM.ClosedConversationSummarizer.Registers"/>
/// (Tenderness, Longing, Playfulness, Curiosity, Desire, Existential,
/// Wistful, Frustration, Delight). Length must be 9; an all-zero vector
/// is permitted (treated as "no current register signal" — bias function
/// returns no surfaced candidates because cosine similarity with any
/// non-zero record is undefined / zero).
///
/// <see cref="AsOf"/> is the moment to compute decay against — usually
/// <c>DateTimeOffset.UtcNow</c>, but injectable for deterministic tests.
/// </summary>
public sealed record MarkRegisterContext(
    float[]            MarkRegister,
    DateTimeOffset     AsOf);
