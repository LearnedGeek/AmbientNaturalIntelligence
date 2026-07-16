using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;

namespace AniRuntime.Loops;

/// <summary>
/// Issue #96 (2026-07-15) — Production implementation of
/// <see cref="IToolCallInvocation"/>. Owns the classify → dispatch flow so
/// the pipeline call site is a single conditional block.
///
/// Enumerates all registered <see cref="IToolCallableAction"/> at
/// construction (they're DI-singletons per Issue #96 wiring). Rebuilds
/// nothing per-call — the descriptor list and name lookup are cached at
/// startup.
///
/// **Substrate-safety pin.** The result string is returned to the caller
/// as an in-memory value only. This class never writes to memory / substrate.
/// If the pipeline chooses to journal the result (currently: no), it must
/// enter as <see cref="EpistemicTier.Interior"/> per Issue #96 acceptance
/// criteria.
/// </summary>
public sealed class ToolCallInvocation : IToolCallInvocation
{
    private readonly IToolCallClassifier                           _classifier;
    private readonly IReadOnlyList<ToolDescriptor>                 _descriptors;
    private readonly IReadOnlyDictionary<string, IToolCallableAction> _byName;
    private readonly ILogger<ToolCallInvocation>                   _log;

    public ToolCallInvocation(
        IToolCallClassifier                classifier,
        IEnumerable<IToolCallableAction>   actions,
        ILogger<ToolCallInvocation>        log)
    {
        _classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
        _log        = log        ?? throw new ArgumentNullException(nameof(log));

        var actionList = (actions ?? Enumerable.Empty<IToolCallableAction>()).ToList();
        _descriptors = actionList.Select(a => a.Descriptor).ToList();
        _byName      = actionList.ToDictionary(a => a.Descriptor.Name, StringComparer.Ordinal);
    }

    public async Task<string?> TryInvokeAsync(
        string            userMessage,
        string            conversationContext,
        CancellationToken ct)
    {
        if (_descriptors.Count == 0)
        {
            _log.LogDebug("TOOL_CALL_SKIP reason=no_actions_registered");
            return null;
        }

        ToolCallVerdict verdict;
        try
        {
            verdict = await _classifier
                .ClassifyAsync(userMessage, _descriptors, conversationContext ?? string.Empty, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Defensive — classifier already fails open on transport errors,
            // but any bubble-up here should not break the reply pipeline.
            _log.LogWarning(ex, "TOOL_CALL_CLASSIFY_EX — treating as no-call");
            return null;
        }

        if (!verdict.ShouldCallTool || string.IsNullOrWhiteSpace(verdict.ToolName))
        {
            _log.LogDebug("TOOL_CALL_NO_CALL confidence={Confidence:F2} reason={Reason}",
                verdict.Confidence, verdict.Reason ?? "(none)");
            return null;
        }

        if (!_byName.TryGetValue(verdict.ToolName, out var action))
        {
            // Should not happen — the classifier's ParseVerdict already
            // coerces unknown tool names to no-call. Belt-and-suspenders.
            _log.LogWarning("TOOL_CALL_UNKNOWN_TOOL toolName={ToolName}", verdict.ToolName);
            return null;
        }

        var args = verdict.Arguments ?? new Dictionary<string, string>();

        string result;
        try
        {
            result = await action.InvokeAsync(args, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Per Issue #96: "Tool errors surface as attributable errors,
            // not silent fallbacks." Return the exception as a formatted
            // observation string so the character model can see something
            // went wrong rather than getting a silent no-op.
            _log.LogWarning(ex, "TOOL_CALL_INVOKE_EX tool={ToolName}", verdict.ToolName);
            return $"{verdict.ToolName} error: {ex.GetType().Name}";
        }

        _log.LogInformation(
            "TOOL_CALL_INVOKED tool={ToolName} confidence={Confidence:F2} resultChars={ResultLen}",
            verdict.ToolName, verdict.Confidence, result?.Length ?? 0);

        return result;
    }
}
