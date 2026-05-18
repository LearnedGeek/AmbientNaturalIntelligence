using AniRuntime.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace AniRuntime.Loops.Admin.Commands;

/// <summary>
/// <c>///new-thread</c> — confirm a fresh conversation thread.
///
/// The cognitive cycle closes the current thread BEFORE invoking the
/// admin command handler (see <c>CognitiveCycleProcessor</c> ~line 152),
/// so by the time this command runs the thread is already closed. The
/// command's only job is to confirm to Mark that it happened.
/// </summary>
public sealed class NewThreadCommand : IAdminCommand
{
    private readonly ILogger<NewThreadCommand> _log;

    public NewThreadCommand(ILogger<NewThreadCommand> log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public string Name => "new-thread";
    public string HelpText => "new-thread (alias: newthread) — Close current thread, start fresh";

    /// <summary>
    /// Accepts both <c>///new-thread</c> (canonical) and <c>///newthread</c>
    /// (no-hyphen alias). Mark hit the unknown-command path twice on the
    /// no-hyphen form, so the alias prevents muscle-memory typos breaking
    /// the diagnostic loop.
    /// </summary>
    public bool Matches(string trimmedInput) =>
        string.Equals(trimmedInput, "new-thread", StringComparison.OrdinalIgnoreCase)
        || string.Equals(trimmedInput, "newthread", StringComparison.OrdinalIgnoreCase);

    public Task<string> ExecuteAsync(string trimmedInput, CancellationToken ct)
    {
        _log.LogInformation("Admin: new-thread — current thread closed by cycle processor");
        return Task.FromResult("Thread closed. Next message will start a fresh thread.");
    }
}
