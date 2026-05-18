namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Runs one scheduled cognitive cycle end-to-end: emotional state →
/// perception → conversation check → reactive share → context + inner
/// thought → emotional shift → reflection → desire update → outreach.
///
/// <para>
/// Extracted from <c>CognitiveCycleProcessor</c> in §5.5 SOLID refactor
/// (2026-05-18). The processor type is preserved as a facade so tests
/// that reach its legacy <c>static</c> helpers
/// (<c>DetectCareGivingIntent</c>, <c>BuildLexicalAnchorContributions</c>,
/// etc.) still compile.
/// </para>
/// </summary>
public interface ICognitiveCyclePipeline
{
    Task RunAsync(CancellationToken ct);
}
