using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Singular surface (per Theme J shared-boundary principle) for classifying
/// the emotional register of a piece of content. Retires the N-places drift
/// documented on 2026-08-12 where THREE prompts across TWO models were
/// independently producing the same-schema register field:
/// <list type="number">
///   <item><c>InnerThoughtMetadataPromptCommand</c> — qwen3:14b via InnerThoughtPhase.</item>
///   <item><c>EmotionalShiftPromptCommand</c> — ani-v7-conversation via EmotionalProcessor.</item>
///   <item><c>BuildRegisterPrompt</c> inline in <c>AniRuntime.Eval/Program.cs --backfill-register</c>.</item>
/// </list>
///
/// <para>
/// All producers must route through this interface. Both composite prompts
/// (EmotionalShift + InnerThoughtMetadata) now take register as an INPUT
/// (already-classified) rather than emit it as output — so deltas/severity
/// and valence/importance/anchor continue to be produced by their respective
/// composite calls, but the register itself has ONE definition, ONE model,
/// and ONE prompt for the whole system.
/// </para>
///
/// <para>
/// Motivating diagnostic (2026-08-12): arbiter analysis of 90 records where
/// qwen3 said Tenderness and emollama said sadness/neutral. Only 25/90
/// (27.8%) read as genuine Tenderness to a third Claude arbiter; 46% were
/// actually Longing. The load-bearing discriminator: does the message
/// <c>hold</c> someone (Tenderness) or <c>reach for</c> someone (Longing)?
/// Baked into the canonical prompt in <c>QwenRegisterClassifier</c>.
/// </para>
/// </summary>
public interface IRegisterClassifier
{
    /// <summary>
    /// Classify the dominant emotional register expressed in <paramref name="content"/>.
    /// Returns one of the ten register-family strings that map cleanly to
    /// <see cref="RegisterFamily"/> via
    /// <see cref="ImpactCategoryDefaults.ToRegisterFamily(string)"/>.
    /// </summary>
    /// <remarks>
    /// Failure contract: any transport / parse / timeout error is caught and
    /// returned as <c>"Unclassified"</c> with a WARN log. Callers treat
    /// Unclassified as fail-open — the composite call still proceeds, the
    /// downstream retrieval / diversification slots skip unclassified rows.
    /// </remarks>
    Task<string> ClassifyAsync(string content, CancellationToken ct);
}
