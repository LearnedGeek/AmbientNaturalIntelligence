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
/// Early wake support: when Mark texts, the TwilioInboundPerceptionSource calls
/// RequestEarlyWake() to cancel the current sleep and trigger an immediate cycle.
/// </summary>
public class AniHeartbeatService : BackgroundService
{
    private readonly CognitiveCycleProcessor      _cycle;
    private readonly DesireEngine                 _desire;
    private readonly IConversationService         _conversations;
    private readonly AniOptions                   _aniOptions;
    private readonly ILogger<AniHeartbeatService> _log;

    private CancellationTokenSource? _wakeCts;

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
    /// Called by TwilioInboundPerceptionSource when Mark texts.
    /// </summary>
    public void RequestEarlyWake()
    {
        _wakeCts?.Cancel();
    }

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
    /// Active conversation → short heartbeat (ConversationHeartbeatSeconds).
    /// Ambient mode → normal exponential delay from DesireEngine.
    /// </summary>
    private async Task<TimeSpan> ComputeDelayAsync(CancellationToken ct)
    {
        var activeThread = await _conversations.GetActiveThreadAsync(ct).ConfigureAwait(false);
        if (activeThread is not null)
        {
            return TimeSpan.FromSeconds(_aniOptions.ConversationHeartbeatSeconds);
        }

        var state = await _desire.GetStateAsync(ct).ConfigureAwait(false);
        return _desire.ComputeNextWakeTime(state);
    }
}
