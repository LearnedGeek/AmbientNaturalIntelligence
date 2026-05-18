using AniRuntime.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace AniRuntime.Loops.Admin.Commands;

/// <summary>
/// <c>///help</c> — list all registered admin commands with their help text.
///
/// <para>
/// Resolves the full <see cref="IAdminCommand"/> collection lazily via
/// <see cref="IServiceProvider"/> rather than via constructor injection.
/// Direct injection of <c>IEnumerable&lt;IAdminCommand&gt;</c> creates a
/// circular dependency under Singleton lifetime — HelpCommand is itself
/// a member of that enumeration, and the DI container's upfront graph
/// validation rejects the cycle. Lazy resolution sidesteps the cycle:
/// the enumeration isn't walked until <see cref="ExecuteAsync"/> runs.
/// </para>
///
/// <para>
/// Adding a new command automatically extends the help output because
/// the new command appears in DI; no central help string to keep in sync.
/// </para>
/// </summary>
public sealed class HelpCommand : IAdminCommand
{
    private readonly IServiceProvider _services;

    public HelpCommand(IServiceProvider services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    public string Name => "help";
    public string HelpText => "help — Show this list";

    public Task<string> ExecuteAsync(string trimmedInput, CancellationToken ct)
    {
        var allCommands = _services.GetServices<IAdminCommand>();
        var lines = new List<string> { "=== Admin Commands ===" };
        lines.AddRange(allCommands
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => "///" + c.HelpText));
        return Task.FromResult(string.Join("\n", lines));
    }
}
