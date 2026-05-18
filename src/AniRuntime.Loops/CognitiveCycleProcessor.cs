using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;

namespace AniRuntime.Loops;

/// <summary>
/// Cognitive-cycle facade. Forwards <see cref="RunAsync"/> to the
/// <see cref="ICognitiveCyclePipeline"/> implementation and preserves the
/// static legacy helpers (<c>DetectCareGivingIntent</c>, etc.) that the
/// existing test suite reaches via this type name.
///
/// <para>
/// Orchestrator SOLID refactor §5.5 (2026-05-18) — the 20-dep body lives
/// on <see cref="CognitiveCyclePipeline"/>; this type is a 1-dep facade.
/// AniHeartbeatService continues to take <c>CognitiveCycleProcessor</c>
/// so its ctor doesn't change (the §5.x plan keeps it as the public
/// entry point); the heavy lifting is now behind the interface.
/// </para>
/// </summary>
public class CognitiveCycleProcessor
{
    private readonly ICognitiveCyclePipeline _pipeline;

    public CognitiveCycleProcessor(ICognitiveCyclePipeline pipeline)
    {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
    }

    public Task RunAsync(CancellationToken ct) => _pipeline.RunAsync(ct);

    // ── Static legacy facades for backward compatibility ────────────────────
    // Tests reference CognitiveCycleProcessor.StaticMethod() — these delegate
    // to ConversationFeatureDetector / OutreachPhase where the logic lives.

    internal static bool DetectCareGivingIntent(string message)
        => ConversationFeatureDetector.DetectCareGivingIntent(message);

    internal static bool DetectHurtIntent(string message)
        => ConversationFeatureDetector.DetectHurtIntent(message);

    internal static List<EmotionalContribution> BuildLexicalAnchorContributions(
        string message, CharacterStateDoc charState)
        => ConversationFeatureDetector.BuildLexicalAnchorContributions(message, charState);

    internal static bool EndsWithDirectQuestion(string message)
        => ConversationFeatureDetector.EndsWithDirectQuestion(message);

    internal static bool ContainsMemoryReferencingLanguage(string message)
        => ConversationFeatureDetector.ContainsMemoryReferencingLanguage(message);

    internal static bool ContainsThirdPersonReference(string message, string contactName)
        => OutreachPhase.ContainsThirdPersonReference(message, contactName);
}
