using AniRuntime.Actions;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;

namespace AniRuntime.Loops;

/// <summary>
/// Orchestrator SOLID refactor §5.1 (2026-05-18) — thin dispatcher.
///
/// Previously a 415-line god-object with 10 DI dependencies and 11
/// command-handler methods on the class. Now: a 2-dependency dispatcher
/// (the injected <see cref="IAdminCommand"/> collection + the action
/// dispatcher for replies). Adding a new command means adding a new
/// <see cref="IAdminCommand"/> implementation + a DI registration — no
/// edit to this class required.
///
/// <para>
/// **Lifetime: Transient.** The dispatcher is stateless (state lives in
/// <see cref="ITestModeTracker"/> + the per-command services). Commands
/// themselves are also Transient.
/// </para>
/// </summary>
public sealed class AdminCommandHandler : IAdminCommandHandler
{
    private readonly IEnumerable<IAdminCommand> _commands;
    private readonly AniActionDispatcher _dispatcher;
    private readonly ILogger<AdminCommandHandler> _log;

    public AdminCommandHandler(
        IEnumerable<IAdminCommand> commands,
        AniActionDispatcher dispatcher,
        ILogger<AdminCommandHandler> log)
    {
        _commands   = commands   ?? throw new ArgumentNullException(nameof(commands));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _log        = log        ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// True iff a message starts with the <c>///</c> prefix (after any
    /// leading whitespace). Kept as a static convenience for legacy
    /// call sites — the same check is available on
    /// <c>IAdminCommandHandler.IsAdminCommand</c>.
    /// </summary>
    public static bool IsAdminCommand(string message)
        => message.TrimStart().StartsWith("///");

    public async Task<bool> HandleAsync(string message, CancellationToken ct)
    {
        var trimmed = message.TrimStart()[3..].Trim().ToLowerInvariant();
        _log.LogInformation("Admin command received: ///{Command}", trimmed);

        var command = _commands.FirstOrDefault(c => c.Matches(trimmed));
        var reply = command is null
            ? $"Unknown command: ///{trimmed}\nSend ///help for available commands."
            : await command.ExecuteAsync(trimmed, ct).ConfigureAwait(false);

        await SendAdminReplyAsync(reply, ct).ConfigureAwait(false);
        return true;
    }

    private async Task SendAdminReplyAsync(string text, CancellationToken ct)
    {
        // F-1 Phase 8d: producer-side wrap for provenance ("outreach-decision.admin-meta").
        // Envelope is producer-side documentation only in this phase; dispatcher
        // continues to consume the bare OutreachDecision.
        var envelope = new OutreachDecisionEnvelope
        {
            Decision = new OutreachDecision
            {
                ShouldReach = true,
                Message     = text,
                ActionType  = ActionTypes.Sms,
                Reasoning   = "admin command response",
                // May 1, 2026 — flag as admin-meta so the dispatch pipeline skips
                // voice / image enrichment. Mark Apr 30 ///tag at 07:47:32 had ~184KB
                // of ElevenLabs audio attached because the meta-dispatch went through
                // the same enrichment path as a real reply. Admin tags are not
                // relational content and should ship as plain SMS.
                IsAdminMeta = true,
            },
            Source = OutreachDecisionSource.AdminMeta,
        };

        await _dispatcher.DispatchAsync(envelope.Decision, ct).ConfigureAwait(false);
    }
}
