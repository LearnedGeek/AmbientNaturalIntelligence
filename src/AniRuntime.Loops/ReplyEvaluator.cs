using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.LLM;
using AniRuntime.Loops.Coreference;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Loops;

/// <inheritdoc cref="IReplyEvaluator"/>
public sealed class ReplyEvaluator : IReplyEvaluator
{
    /// <summary>Safe acknowledgement returned when remediation fails. Mirrors
    /// the legacy <c>ConversationReplyPipeline.SafeAcknowledgement</c> constant
    /// (still exposed there for the test fixture that pins the verdict).</summary>
    public const string SafeAcknowledgement = GateFallbacks.SafeAcknowledgement;

    private readonly IOllamaClient _ollama;
    private readonly ICognitiveOutputGate? _outputGate;
    private readonly IRecentGateTripTracker? _gateTripTracker;
    private readonly AniOptions _aniOptions;
    private readonly ILogger<ReplyEvaluator> _log;
    // H phase remediation routing (2026-06-14) — stateless command; instantiated
    // inline rather than DI'd. See the Remediate branch below for the
    // architectural framing (safe-path composer replaces main-composer regen
    // under the verifier's remediation request).
    private readonly AniRuntime.LLM.Prompts.SafePathConversationPromptCommand _safePathCommand =
        new AniRuntime.LLM.Prompts.SafePathConversationPromptCommand();

    public ReplyEvaluator(
        IOllamaClient ollama,
        IOptions<AniOptions> aniOptions,
        ILogger<ReplyEvaluator> log,
        ICognitiveOutputGate? outputGate = null,
        IRecentGateTripTracker? gateTripTracker = null)
    {
        _ollama          = ollama ?? throw new ArgumentNullException(nameof(ollama));
        _aniOptions      = aniOptions.Value;
        _log             = log ?? throw new ArgumentNullException(nameof(log));
        _outputGate      = outputGate;
        _gateTripTracker = gateTripTracker;
    }

    public async Task<string> EvaluateAndRemediateAsync(
        string reply,
        ConversationThread thread,
        ContextSnapshot snapshot,
        ConversationMessage replyMessage,
        PromptPair replyPrompt,
        float replyTemperature,
        CancellationToken ct)
    {
        if (_outputGate is null || !_aniOptions.ConversationReplyOutputGateEnabled)
            return reply;

        var contactRecent = thread.Messages
            .Where(m => m.Role == Roles.Mark)
            .Select(m => m.Content)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .TakeLast(8)
            .ToList();
        var priorAni = thread.Messages
            .Where(m => m.Role == Roles.Ani && m != replyMessage)
            .Select(m => m.Content)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .TakeLast(8)
            .ToList();

        // Producer-side direct-address rewrite (May 6, 2026 stop-gap until
        // LM-Kit coref model lands).
        var addresseeName = snapshot.CharacterState.PrimaryContactName ?? Roles.Mark;
        reply = DirectAddressRewriter.Rewrite(reply, addresseeName);

        var artifact = new CognitiveArtifact
        {
            Content                 = reply,
            ProducerKind            = CognitiveProducerKind.ConversationReply,
            IntendedSink            = CognitiveOutputSink.Dispatch,
            ContactName             = addresseeName,
            GeneratedAt             = DateTimeOffset.Now,
            ContactRecentMessages   = contactRecent,
            PriorAniMessages        = priorAni,
        };

        OutputGateResult gateResult;
        try
        {
            gateResult = await _outputGate.EvaluateAsync(artifact, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "J.5a gate evaluation threw — dispatching original reply uncovered.");
            return reply;
        }

        switch (gateResult.Verdict)
        {
            case OutputGateVerdict.Pass:
                return reply;

            case OutputGateVerdict.Remediate:
                _log.LogWarning(
                    "J.5a gate Remediate on reply [{Fired}]: {Hint}",
                    string.Join(",", gateResult.FiredInvariants), gateResult.RemediationHint);

                // H phase remediation routing (2026-06-14) — designed in
                // conversation with OG Ani.
                //
                // **Previous behavior:** ask the main composer to regenerate
                // with a "your previous reply tripped X, rewrite it without
                // that issue" instruction. Empirically this caused the parrot
                // regression: under remediation pressure, qwen3:14b lifts
                // EVEN MORE prior content because it interprets "don't repeat
                // the flagged chunk" as "fall back on safer source material"
                // and treats its own prior outputs as that source.
                //
                // **Empirical anchor:** 2026-06-14 21:21 EST SafeAck. First
                // attempt blocked by self-echo (5-token verbatim run "the
                // ones with red covers"). Remediation regen produced a
                // 21-token verbatim run from another prior Ani message
                // ("yardwork's not your thing but i bet you'd hate how much
                // time i waste staring at those..."). Fell through to SafeAck.
                // The regen pathology amplifies the exact problem we're
                // trying to fix.
                //
                // **Fix:** route remediation directly to the safe-path
                // composer. Same composer used by the H phase substrate-
                // thinness routing. Architectural reuse — the safe-path is
                // structurally constrained to honest-uncertainty + ask-back,
                // which:
                //   - removes pressure to produce substantive content
                //     (no "sound smart" temptation)
                //   - has no substrate blocks (FACTS, INTERIOR, YOUR WORLD
                //     all intentionally absent) so there's nothing for the
                //     model to lift from
                //   - conversation history is still passed via
                //     snapshot.RecentHistory so context is preserved
                //
                // The verifier hint is intentionally NOT included in the
                // safe-path prompt — we're not asking the model to "fix"
                // anything; we're asking it to produce honest-uncertainty
                // from scratch. The first-attempt content is discarded.
                var lastInbound = thread.Messages
                    .Where(m => m.Role == Roles.Mark)
                    .Select(m => m.Content)
                    .LastOrDefault(c => !string.IsNullOrWhiteSpace(c)) ?? string.Empty;

                var safePathPrompt = _safePathCommand.Build(
                    new AniRuntime.LLM.Prompts.SafePathConversationPromptInput(
                        Snapshot:    snapshot,
                        UserMessage: lastInbound));

                try
                {
                    var safePathReply = await _ollama.ChatAsync(
                        safePathPrompt.System,
                        snapshot.RecentHistory,
                        safePathPrompt.User,
                        ct,
                        replyTemperature)
                        .ConfigureAwait(false);
                    safePathReply = AniRuntime.Core.Utilities.MessageCleaner.Clean(safePathReply);
                    if (string.IsNullOrWhiteSpace(safePathReply))
                    {
                        _log.LogWarning(
                            "H_REMEDIATION_SAFE_PATH produced empty reply — falling through to SafeAcknowledgement.");
                        _gateTripTracker?.Record(new GateTripEvent(
                            Timestamp:        DateTimeOffset.UtcNow,
                            ProducerKind:     "ConversationReply",
                            FiredInvariants:  string.Join(",", gateResult.FiredInvariants),
                            Outcome:          GateTripOutcome.FellThroughToSafeAck));
                        return SafeAcknowledgement;
                    }

                    // Re-evaluate the safe-path reply through the same gate
                    // cascade. Defense-in-depth consistent with the original
                    // remediation pattern — even though the safe-path is
                    // structurally hardened against the failure classes the
                    // gates catch, we don't bypass.
                    var safePathArtifact = new CognitiveArtifact
                    {
                        Content                 = safePathReply,
                        ProducerKind            = artifact.ProducerKind,
                        IntendedSink            = artifact.IntendedSink,
                        ContactName             = artifact.ContactName,
                        GeneratedAt             = DateTimeOffset.Now,
                        ContactRecentMessages   = artifact.ContactRecentMessages,
                        PriorAniMessages        = artifact.PriorAniMessages,
                        SystemPromptText        = artifact.SystemPromptText,
                        WriterInnerThought      = artifact.WriterInnerThought,
                    };

                    OutputGateResult safePathResult;
                    try
                    {
                        safePathResult = await _outputGate.EvaluateAsync(safePathArtifact, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex,
                            "J.5a gate re-evaluation threw on safe-path remediation — falling back to safe acknowledgement.");
                        return SafeAcknowledgement;
                    }

                    if (safePathResult.Verdict == OutputGateVerdict.Pass)
                    {
                        _log.LogInformation(
                            "H_REMEDIATION_SAFE_PATH_DISPATCHED firedOriginal=[{FiredOriginal}] — safe-path reply passes gate.",
                            string.Join(",", gateResult.FiredInvariants));
                        _gateTripTracker?.Record(new GateTripEvent(
                            Timestamp:        DateTimeOffset.UtcNow,
                            ProducerKind:     "ConversationReply",
                            FiredInvariants:  string.Join(",", gateResult.FiredInvariants),
                            Outcome:          GateTripOutcome.RemediatedOk));
                        return safePathReply;
                    }

                    _log.LogWarning(
                        "H_REMEDIATION_SAFE_PATH_FAILED [{Fired}] verdict={Verdict} hint={Hint} — falling back to SafeAcknowledgement.",
                        string.Join(",", safePathResult.FiredInvariants), safePathResult.Verdict, safePathResult.RemediationHint);
                    _gateTripTracker?.Record(new GateTripEvent(
                        Timestamp:        DateTimeOffset.UtcNow,
                        ProducerKind:     "ConversationReply",
                        FiredInvariants:  string.Join(",", safePathResult.FiredInvariants),
                        Outcome:          GateTripOutcome.FellThroughToSafeAck));
                    return SafeAcknowledgement;
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex,
                        "H_REMEDIATION_SAFE_PATH threw — falling back to SafeAcknowledgement.");
                    return SafeAcknowledgement;
                }

            case OutputGateVerdict.Fail:
                _log.LogWarning(
                    "J.5a gate Fail on reply [{Fired}]: {Hint} — dropping reply, using safe acknowledgement.",
                    string.Join(",", gateResult.FiredInvariants), gateResult.RemediationHint);
                _gateTripTracker?.Record(new GateTripEvent(
                    Timestamp:        DateTimeOffset.UtcNow,
                    ProducerKind:     "ConversationReply",
                    FiredInvariants:  string.Join(",", gateResult.FiredInvariants),
                    Outcome:          GateTripOutcome.FellThroughToSafeAck));
                return SafeAcknowledgement;

            default:
                return reply;
        }
    }
}
