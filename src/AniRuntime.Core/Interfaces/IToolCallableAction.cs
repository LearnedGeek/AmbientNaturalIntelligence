using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Issue #96 (2026-07-15) — Bridge between the classifier's structured
/// tool-selection verdict and the deterministic <see cref="IAniAction"/>
/// dispatch surface. Adds the *descriptor* (what the classifier sees) plus
/// an argument-map <see cref="InvokeAsync"/> entry point, on top of the
/// existing action shape.
///
/// **Why not just extend <see cref="IAniAction"/>.** The existing action is
/// keyed by <see cref="IAniAction.ActionType"/> and consumes an
/// <see cref="OutreachDecision"/>. Tool calls take a free-shape argument
/// map (parsed from LLM JSON) and return a string result that gets fed
/// back into the character-model turn. Different call surface, so a new
/// primitive rather than an overloaded existing one.
///
/// **Substrate safety pin (per Issue #96 acceptance criteria and Phase 2.1
/// discipline).** The string result returned from <see cref="InvokeAsync"/>
/// is *never* written to Facts / Episodic tiers directly. If the runtime
/// stores it at all, it enters as <c>Provenance = Interior</c> with the tool
/// name as the attributed source. Same rule as vision-context per Issue #93.
/// </summary>
public interface IToolCallableAction
{
    /// <summary>
    /// Descriptor the classifier sees. The <see cref="ToolDescriptor.Name"/>
    /// here is the identifier the classifier emits in its verdict —
    /// <see cref="IToolCallableAction"/> registrations must have unique
    /// <see cref="ToolDescriptor.Name"/> values.
    /// </summary>
    ToolDescriptor Descriptor { get; }

    /// <summary>
    /// Invoke the tool with arguments extracted by the classifier. Return
    /// value is a short human-readable string that becomes the
    /// tool-observation the character model sees in its next turn's context.
    ///
    /// Errors should return an attributable error string (per Issue #96
    /// "tool errors surface as attributable errors, not silent fallbacks")
    /// rather than throwing — the runtime treats a returned error string
    /// as a first-class observation.
    /// </summary>
    Task<string> InvokeAsync(
        IReadOnlyDictionary<string, string> arguments,
        CancellationToken                   ct);
}
