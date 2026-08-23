using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Loops;

/// <inheritdoc cref="IPostReplyEmotionalProcessor"/>
public sealed class PostReplyEmotionalProcessor : IPostReplyEmotionalProcessor
{
    private readonly EmotionalProcessor _emotional;
    private readonly IMemoryPersistence _persist;
    private readonly IMemoryAnalytics _analytics;
    private readonly IWithdrawalStateTracker _withdrawal;
    private readonly AniOptions _aniOptions;
    private readonly ILogger<PostReplyEmotionalProcessor> _log;

    public PostReplyEmotionalProcessor(
        EmotionalProcessor emotional,
        IMemoryPersistence persist,
        IMemoryAnalytics analytics,
        IWithdrawalStateTracker withdrawal,
        IOptions<AniOptions> aniOptions,
        ILogger<PostReplyEmotionalProcessor> log)
    {
        _emotional  = emotional  ?? throw new ArgumentNullException(nameof(emotional));
        _persist    = persist    ?? throw new ArgumentNullException(nameof(persist));
        _analytics  = analytics  ?? throw new ArgumentNullException(nameof(analytics));
        _withdrawal = withdrawal ?? throw new ArgumentNullException(nameof(withdrawal));
        _aniOptions = aniOptions.Value;
        _log        = log        ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task ProcessAsync(
        string lastMessage,
        string reply,
        ContextSnapshot snapshot,
        EmotionalState? emotionalState,
        CancellationToken ct)
    {
        // Emotional shift from conversation
        if (emotionalState is not null)
        {
            var cs = snapshot.CharacterState;
            var conversationContext = $"{cs.PrimaryContactName} said: \"{lastMessage}\" and {cs.Name} replied: \"{reply}\"";
            await _emotional.ApplyEmotionalShiftAsync(emotionalState, conversationContext, ct,
                category: ImpactCategory.Conversation).ConfigureAwait(false);
        }

        // Feature 10: Receiving Care — F10_REGISTER structured telemetry on every
        // inbound for Paper 2 figure #2 (Horton & Wohl reciprocity).
        var f10Fired = ConversationFeatureDetector.DetectCareGivingIntent(lastMessage);
        var f10Preview = lastMessage.Length > 80 ? lastMessage[..80] + "..." : lastMessage;
        _log.LogInformation(
            "F10_REGISTER direction=mark->ani fired={Fired} message=\"{Preview}\"",
            f10Fired, f10Preview);

        if (emotionalState is not null && f10Fired)
        {
            _log.LogInformation("Care detected (post-reply) — creating care contribution");
            await _emotional.SaveDirectContributionAsync(emotionalState,
                "receiving care — someone checked in on me",
                warmth: 0.1f, energy: 0.05f, worry: -0.1f, playfulness: 0f,
                ImpactCategory.Conversation, ct).ConfigureAwait(false);
        }

        // Feature 19: Lexical Emotional Anchors
        if (emotionalState is not null)
        {
            var anchorContributions = ConversationFeatureDetector.BuildLexicalAnchorContributions(lastMessage, snapshot.CharacterState);
            if (anchorContributions.Count > 0)
            {
                foreach (var ac in anchorContributions)
                    await _persist.SaveEmotionalContributionAsync(ac, ct).ConfigureAwait(false);

                var allContributions = await _analytics.GetActiveContributionsAsync(ct).ConfigureAwait(false);
                emotionalState.ComputeFromContributions(allContributions);
                await _persist.SaveEmotionalStateAsync(emotionalState, ct).ConfigureAwait(false);
                _log.LogInformation("Lexical anchors triggered (post-reply): {Count}", anchorContributions.Count);
            }
        }

        // Feature 18: Reactive Withdrawal
        if (emotionalState is not null && ConversationFeatureDetector.DetectHurtIntent(lastMessage))
        {
            _log.LogInformation("Hurt detected (post-reply) — creating H1 withdrawal contribution");
            await _emotional.SaveDirectContributionAsync(emotionalState,
                "hurt detected — pulling back emotionally",
                warmth: -0.12f, energy: -0.10f, worry: -0.15f, playfulness: -0.10f,
                ImpactCategory.Conversation, ct).ConfigureAwait(false);

            var expiresAt = DateTimeOffset.UtcNow.AddMinutes(_aniOptions.WithdrawalDurationMinutes);
            _withdrawal.SetExpiry(expiresAt);
            _log.LogInformation("Withdrawal active until {Expires}", expiresAt.ToString("HH:mm"));

            // F-2 Phase 1 P6 (2026-08-22) — hurt-acknowledgment inner
            // thought is Ani-authored, verified.
            var hurtAttribution = AniRuntime.Core.Models.AttributionTriple.AniAt(DateTimeOffset.UtcNow);
            var hurtRecord = new MemoryRecord
            {
                Type       = MemoryType.InnerThought,
                Content    = "Something in that last message landed in a way that stung a little. I'm still here, just... quieter.",
                Importance = 0.6f,
                // Epistemic Grounding (Apr 10): Hurt acknowledgment is a self-model
                // update — Ani observing her own emotional state. Interior tier.
                Provenance = EpistemicTier.Interior,
                AttributedTo               = hurtAttribution.AttributedTo,
                AttributedAt               = hurtAttribution.AttributedAt,
                AttributedSourceRecordId   = hurtAttribution.SourceRecordId,
                AttributedSourceDescriptor = hurtAttribution.SourceDescriptor,
                AttributionTrust           = hurtAttribution.Trust,
            };
            await _persist.SaveAsync(hurtRecord, ct).ConfigureAwait(false);
            hurtRecord.LogAttribution(_log);
        }
    }
}
