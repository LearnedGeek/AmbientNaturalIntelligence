namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Routes admin commands (`///` prefix) directly to handler logic, bypassing
/// the conversation pipeline entirely. Defined in Core so the Perception
/// layer can route inbound admin commands without depending on Loops.
///
/// Apr 28, 2026 architectural fix: previously the perception source added
/// admin commands to the conversation_messages table so the cognitive
/// cycle could read them and dispatch. That coupled administrative metadata
/// to the relational substrate, causing leaks (LastContactInbound update,
/// thread-summary inclusion, structured-summary surfacing into prompts).
/// The interface decouples routing from the conversation pipeline so admin
/// commands can be processed-once-and-done without persistence.
/// </summary>
public interface IAdminCommandHandler
{
    /// <summary>
    /// Process the admin command and dispatch any reply (e.g., status text,
    /// tag confirmation). Returns true when handled, false when the command
    /// was unrecognized.
    /// </summary>
    Task<bool> HandleAsync(string message, CancellationToken ct = default);

    /// <summary>
    /// Static contract: a message is an admin command iff it starts with the
    /// "///" prefix (after leading whitespace). Lives on the interface so any
    /// consumer can check without instantiating a handler.
    /// </summary>
    static bool IsAdminCommand(string? message)
        => message?.TrimStart().StartsWith("///") ?? false;
}
