using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Loops;

/// <summary>
/// Theme M Phase M.0 (May 5, 2026) — no-op composer for the conscious-
/// substrate gist. Returns <see cref="ConsciousSubstrateGist.Empty"/> for
/// every cycle. Exists so the architectural skeleton + telemetry harness
/// + spec tests are in place before any slice has content.
///
/// **What this class does NOT do (by design):**
/// - Does NOT call <see cref="IMemoryService"/>.SaveAsync.
/// - Does NOT call <see cref="IConversationService"/>.AddMessageAsync.
/// - Does NOT produce side effects that cause the gist content to enter
///   retrieval pools.
/// - Does NOT mutate the <see cref="ContextSnapshot"/>.
///
/// These properties are pinned by spec tests in
/// <c>tests/AniRuntime.Tests/ConsciousSubstrateGistContractTests.cs</c>:
/// strict-mock <see cref="IMemoryService"/> and
/// <see cref="IConversationService"/> with NO setups means any unauthorized
/// write call will raise; the no-op composer makes none, so the tests pass
/// from M.0 forward and continue to enforce read-only as slice
/// implementations land in M.1+.
///
/// **M.1 onward:** subclass or replace this composer with a real
/// implementation that produces slice content. The interface contract
/// (no memory writes, no conversation writes, no retrieval-pool entry)
/// stays in force; spec tests catch any implementation that violates
/// the read-only architectural property.
///
/// Plan: docs/spec/ANI-Theme-M-Conscious-Substrate-Individuation-Plan.md §3
/// (read-only as core architectural property).
/// </summary>
public class ConsciousSubstrateGistComposer : IConsciousSubstrateGist
{
    private readonly IOptions<AniOptions>                       _options;
    private readonly ILogger<ConsciousSubstrateGistComposer>    _log;

    public ConsciousSubstrateGistComposer(
        IOptions<AniOptions>                       options,
        ILogger<ConsciousSubstrateGistComposer>    log)
    {
        _options = options;
        _log     = log;
    }

    /// <inheritdoc />
    public Task<ConsciousSubstrateGist> ComputeGistAsync(
        ContextSnapshot   snapshot,
        CancellationToken ct = default)
    {
        // M.0: no-op. Every call returns Empty. The consumer's telemetry
        // (M0_GIST_COMPOSITION at the prompt-build site) records that the
        // composer ran with all-false slice flags + zero-token budget, so
        // the substrate-source-ratio baseline accumulates against the
        // pre-Theme-M state.
        //
        // Even when ConsciousSubstrateGistEnabled is true in M.0, we still
        // return Empty — the flag's purpose in M.0 is to gate the consumer's
        // injection logic into the prompt, not to gate composer execution.
        // This means the spec-tested property (no memory writes) holds
        // regardless of flag state.
        return Task.FromResult(ConsciousSubstrateGist.Empty);
    }
}
