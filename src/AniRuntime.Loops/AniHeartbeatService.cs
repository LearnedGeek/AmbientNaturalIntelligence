using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Loops;

/// <summary>
/// Top-level BackgroundService. Owns the cognitive cycle schedule.
///
/// On each iteration:
///   1. Read current desire state
///   2. Check for active conversation (shortens heartbeat to ~45 seconds)
///   3. Compute next wake time (pure function — emerges from her internal state)
///   4. Sleep for that duration (interruptible via RequestEarlyWake)
///   5. Run one cognitive cycle
///
/// No polling. No dice-rolling. Her schedule emerges from who she is right now.
/// Early wake support: when the contact texts, TwilioInboundPerceptionSource calls
/// RequestEarlyWake() to cancel the current sleep and trigger an immediate cycle.
/// </summary>
public class AniHeartbeatService : BackgroundService
{
    private readonly CognitiveCycleProcessor       _cycle;
    private readonly DesireEngine                  _desire;
    private readonly IConversationService          _conversations;
    private readonly AniOptions                    _aniOptions;
    private readonly ILogger<AniHeartbeatService>  _log;

    private CancellationTokenSource? _wakeCts;
    private volatile int _voiceCallActive;

    public AniHeartbeatService(
        CognitiveCycleProcessor      cycle,
        DesireEngine                 desire,
        IConversationService         conversations,
        IOptions<AniOptions>         aniOptions,
        ILogger<AniHeartbeatService> log)
    {
        _cycle         = cycle;
        _desire        = desire;
        _conversations = conversations;
        _aniOptions    = aniOptions.Value;
        _log           = log;
    }

    /// <summary>
    /// Interrupts the current sleep so the next cognitive cycle runs immediately.
    /// Called by TwilioInboundPerceptionSource when the contact texts.
    /// </summary>
    public void RequestEarlyWake()
    {
        _wakeCts?.Cancel();
    }

    /// <summary>
    /// Pause cognitive cycles during a voice call so Ollama is free for voice replies.
    /// </summary>
    public void PauseForVoiceCall() => Interlocked.Exchange(ref _voiceCallActive, 1);
    public void ResumeAfterVoiceCall() => Interlocked.Exchange(ref _voiceCallActive, 0);
    public bool IsVoiceCallActive => _voiceCallActive == 1;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("ANI Runtime started — she is awake");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var delay = await ComputeDelayAsync(stoppingToken).ConfigureAwait(false);

                _log.LogInformation("Next cognitive cycle in {Seconds:F0} sec ({Minutes:F1} min)",
                    delay.TotalSeconds, delay.TotalMinutes);

                // Interruptible sleep — RequestEarlyWake() cancels this
                _wakeCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                try
                {
                    await Task.Delay(delay, _wakeCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    _log.LogInformation("Early wake triggered — conversation mode");
                }
                finally
                {
                    _wakeCts.Dispose();
                    _wakeCts = null;
                }

                // Skip cognitive cycle while a voice call is active — Ollama must be
                // free for voice reply generation (single-threaded inference)
                if (IsVoiceCallActive)
                {
                    _log.LogDebug("Cognitive cycle skipped — voice call active");
                    continue;
                }

                await _cycle.RunAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown — do not log as error
                break;
            }
            catch (Exception ex)
            {
                // Log but do not crash the service — she recovers on the next cycle
                _log.LogError(ex, "Cognitive cycle failed — will retry after cooldown");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken).ConfigureAwait(false);
            }
        }

        _log.LogInformation("ANI Runtime stopped");
    }

    /// <summary>
    /// Determines how long to sleep before the next cycle.
    /// Active conversation with unread contact message → short heartbeat (ConversationHeartbeatSeconds).
    /// Active conversation but Ani spoke last → normal delay (it's the contact's turn).
    /// Active conversation but Ani already chose silence → normal delay (she read it and decided).
    /// Ambient mode → normal exponential delay from DesireEngine.
    /// </summary>
    private async Task<TimeSpan> ComputeDelayAsync(CancellationToken ct)
    {
        var activeThread = await _conversations.GetActiveThreadAsync(ct).ConfigureAwait(false);
        if (activeThread is not null)
        {
            // Only use fast heartbeat when the contact has an unread message waiting for reply
            // AND Ani hasn't already evaluated it (chose silence). Without this check, choosing
            // silence traps her in 45s cycles until the thread times out (BUG-001).
            var hasUnreadFromContact = activeThread.Messages.Count > 0 &&
                                      activeThread.Messages[^1].Role == "mark";
            if (hasUnreadFromContact)
            {
                var lastContactMsg = activeThread.Messages[^1];
                var alreadyEvaluated = _cycle.LastEvaluatedMessageAt.HasValue &&
                                       lastContactMsg.SentAt == _cycle.LastEvaluatedMessageAt.Value;
                if (!alreadyEvaluated)
                    return TimeSpan.FromSeconds(_aniOptions.ConversationHeartbeatSeconds);
            }
        }

        var state = await _desire.GetStateAsync(ct).ConfigureAwait(false);
        return _desire.ComputeNextWakeTime(state);
    }
}
