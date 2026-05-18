using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Loops.Outreach;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Loops;

/// <summary>
/// Outbound-message coordinator. Routes one of three modes per cycle:
/// proactive outreach (<see cref="IOutreachPipeline"/>), reactive share
/// (<see cref="IReactiveShareService"/>), or silence record
/// (<see cref="ISilenceChoiceRecorder"/>). All cycle-level outreach
/// gating + dispatch lives inside the collaborators; this class is a
/// pure router.
///
/// <para>
/// Orchestrator SOLID refactor §5.3 (2026-05-18) — previously a 19-dep
/// class that mixed proactive outreach orchestration, reactive share,
/// silence recording, several JSON parsers, embedding-based echo
/// detection, and conversation-thread persistence. Now a 5-dep
/// coordinator. Static helpers (<see cref="ParseOutreachComposition"/>,
/// <see cref="ParseOutreachDecision"/>, <see cref="ContainsThirdPersonReference"/>,
/// <see cref="RecordOutboundInThreadAsync"/>) are preserved as façade
/// methods that delegate to the pipeline so existing tests keep working.
/// </para>
/// </summary>
public class OutreachPhase
{
    private readonly IOutreachPipeline _pipeline;
    private readonly IReactiveShareService _reactiveShare;
    private readonly ISilenceChoiceRecorder _silenceRecorder;
    private readonly IOutboundThreadRecorder _threadRecorder;
    private readonly AniOptions _aniOptions;
    private readonly ILogger<OutreachPhase> _log;

    public OutreachPhase(
        IOutreachPipeline pipeline,
        IReactiveShareService reactiveShare,
        ISilenceChoiceRecorder silenceRecorder,
        IOutboundThreadRecorder threadRecorder,
        IOptions<AniOptions> aniOptions,
        ILogger<OutreachPhase> log)
    {
        _pipeline        = pipeline        ?? throw new ArgumentNullException(nameof(pipeline));
        _reactiveShare   = reactiveShare   ?? throw new ArgumentNullException(nameof(reactiveShare));
        _silenceRecorder = silenceRecorder ?? throw new ArgumentNullException(nameof(silenceRecorder));
        _threadRecorder  = threadRecorder  ?? throw new ArgumentNullException(nameof(threadRecorder));
        _aniOptions      = aniOptions.Value;
        _log             = log             ?? throw new ArgumentNullException(nameof(log));
    }

    public Task RunOutreachAsync(ContextSnapshot snapshot, string recentThought, CancellationToken ct)
    {
        // Apr 21, 2026 — OutreachEnabled flag (Option C, commit 2). When
        // false, the proactive outreach path short-circuits at entry. Inbound
        // conversation replies (ConversationReplyPhase) are unaffected.
        if (!_aniOptions.OutreachEnabled)
        {
            _log.LogInformation(
                "Outreach disabled: OutreachEnabled=false in config — skipping proactive outreach composition and dispatch");
            return Task.CompletedTask;
        }

        return _pipeline.RunAsync(snapshot, recentThought, ct);
    }

    public Task<bool> TryReactiveShareAsync(
        List<PerceptionEvent> perceptions, CharacterStateDoc charState, CancellationToken ct)
        => _reactiveShare.TryShareAsync(perceptions, charState, ct);

    public Task RecordSilenceChoiceAsync(
        DesireState desireState, EmotionalState emotionalState, CancellationToken ct)
        => _silenceRecorder.RecordAsync(desireState, emotionalState, ct);

    /// <summary>
    /// Façade for tests — delegates to <see cref="IOutboundThreadRecorder"/>.
    /// </summary>
    internal Task RecordOutboundInThreadAsync(string content, CancellationToken ct)
        => _threadRecorder.RecordAsync(content, ct);

    // ─── Static helpers (lifted into OutreachPipeline; re-exported here so
    //     existing tests like OutreachCompositionParseTests keep compiling) ─

    public static (string? Message, string? Notes) ParseOutreachComposition(string? raw)
        => OutreachPipeline.ParseOutreachComposition(raw);

    /// <summary>
    /// Detects third-person references in an outreach message. Preserved
    /// here for tests that pin the behaviour directly.
    /// </summary>
    internal static bool ContainsThirdPersonReference(string message, string contactName)
    {
        var lower = message.ToLowerInvariant();

        var hasThirdPerson = lower.Contains(" him") || lower.Contains(" his ") ||
                             lower.Contains(" he ") || lower.StartsWith("he ") ||
                             lower.StartsWith("his ") || lower.Contains("him.") ||
                             lower.Contains("his.");
        if (hasThirdPerson) return true;

        if (!string.IsNullOrWhiteSpace(contactName) && contactName.Length >= 2)
        {
            var nameLower = contactName.ToLowerInvariant();
            var idx = lower.IndexOf(nameLower, StringComparison.Ordinal);
            while (idx >= 0)
            {
                var before = idx == 0 || !char.IsLetter(lower[idx - 1]);
                var afterIdx = idx + nameLower.Length;
                var after = afterIdx >= lower.Length || !char.IsLetter(lower[afterIdx]);

                if (before && after)
                {
                    if (afterIdx < lower.Length && lower[afterIdx] == ' ')
                        return true;
                    if (afterIdx + 1 < lower.Length && lower[afterIdx] == '\'' && lower[afterIdx + 1] == 's')
                        return true;
                }

                idx = lower.IndexOf(nameLower, afterIdx, StringComparison.Ordinal);
            }
        }

        return false;
    }
}
