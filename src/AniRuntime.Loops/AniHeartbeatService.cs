using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AniRuntime.Loops;

/// <summary>
/// Top-level BackgroundService. Owns the cognitive cycle schedule.
///
/// On each iteration:
///   1. Read current desire state
///   2. Compute next wake time (pure function — emerges from her internal state)
///   3. Sleep for that duration
///   4. Run one cognitive cycle
///
/// No polling. No dice-rolling. Her schedule emerges from who she is right now.
/// </summary>
public class AniHeartbeatService : BackgroundService
{
    private readonly CognitiveCycleProcessor      _cycle;
    private readonly DesireEngine                 _desire;
    private readonly ILogger<AniHeartbeatService> _log;

    public AniHeartbeatService(
        CognitiveCycleProcessor      cycle,
        DesireEngine                 desire,
        ILogger<AniHeartbeatService> log)
    {
        _cycle  = cycle;
        _desire = desire;
        _log    = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("ANI Runtime started — she is awake");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var state = await _desire.GetStateAsync(stoppingToken).ConfigureAwait(false);
                var delay = _desire.ComputeNextWakeTime(state);

                _log.LogInformation("Next cognitive cycle in {Minutes:F1} min", delay.TotalMinutes);

                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
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
}
