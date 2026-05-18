using AniRuntime.Core.Interfaces;

namespace AniRuntime.Loops.Admin.Commands;

/// <summary>
/// <c>///help</c> — list all registered admin commands with their help text.
///
/// Build the help message by iterating the injected <see cref="IAdminCommand"/>
/// collection. Adding a new command automatically extends the help output
/// because the new command appears in DI; no central help string to keep
/// in sync.
/// </summary>
public sealed class HelpCommand : IAdminCommand
{
    private readonly IEnumerable<IAdminCommand> _allCommands;

    public HelpCommand(IEnumerable<IAdminCommand> allCommands)
    {
        _allCommands = allCommands ?? throw new ArgumentNullException(nameof(allCommands));
    }

    public string Name => "help";
    public string HelpText => "help — Show this list";

    public Task<string> ExecuteAsync(string trimmedInput, CancellationToken ct)
    {
        var lines = new List<string> { "=== Admin Commands ===" };
        lines.AddRange(_allCommands
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => "///" + c.HelpText));
        return Task.FromResult(string.Join("\n", lines));
    }
}
