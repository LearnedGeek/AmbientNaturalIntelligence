namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Orchestrator SOLID refactor §5.1 (2026-05-18) — Command pattern for
/// the `///` admin command surface. Each command becomes its own class
/// implementing this interface; <c>AdminCommandHandler</c> becomes a
/// thin dispatcher that picks the matching command and runs it.
///
/// <para>
/// Adding a new command is now Open/Closed-clean: new file, new class,
/// new DI registration. No existing command (and no central switch
/// statement) needs to change. Help-text enumeration is automatic via
/// <see cref="HelpText"/> — no hand-maintained list to drift out of
/// sync with the implemented commands.
/// </para>
/// <para>
/// **Lifetime: Transient.** Commands are stateless. State that needs to
/// span calls (e.g. test-mode flag) lives in its own singleton service
/// (<see cref="ITestModeTracker"/>) that commands inject.
/// </para>
/// </summary>
public interface IAdminCommand
{
    /// <summary>
    /// The command name (lowercase, no leading slashes). Used for
    /// matching exact-name commands and for ordering in help output.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// One-line help text shown by the <c>///help</c> command. Format
    /// convention: <c>"name [args] — short description"</c>.
    /// </summary>
    string HelpText { get; }

    /// <summary>
    /// Does this command match the given (already-trimmed, lowercased,
    /// /// -stripped) input? Default implementation is exact-name match;
    /// commands that take arguments (e.g. <c>tag &lt;label&gt;</c>) override
    /// to do prefix matching.
    /// </summary>
    bool Matches(string trimmedInput) =>
        string.Equals(trimmedInput, Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Run the command. Returns the reply string that will be SMS-dispatched
    /// back to Mark. The caller (<c>AdminCommandHandler</c>) owns dispatch;
    /// commands focus on producing the reply content.
    /// </summary>
    Task<string> ExecuteAsync(string trimmedInput, CancellationToken ct);
}
