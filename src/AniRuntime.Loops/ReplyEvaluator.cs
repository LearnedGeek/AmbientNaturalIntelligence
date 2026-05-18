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

                var remediationUser =
                    $"Your previous reply tripped a gate check ({string.Join(", ", gateResult.FiredInvariants)}). " +
                    $"Hint: {gateResult.RemediationHint}\n\n" +
                    $"Rewrite your reply to fix this. Same tone, same length, just clear of the issue. " +
                    $"Do NOT acknowledge or reference the gate or hint in the reply itself.\n\n" +
                    $"Original prompt that produced the bad reply:\n{replyPrompt.User}";

                try
                {
                    var regenerated = await _ollama.ChatAsync(
                        replyPrompt.System, snapshot.RecentHistory, remediationUser, ct, replyTemperature)
                        .ConfigureAwait(false);
                    regenerated = AniRuntime.Core.Utilities.MessageCleaner.Clean(regenerated);
                    if (string.IsNullOrWhiteSpace(regenerated))
                    {
                        _log.LogWarning("J.5a gate remediation produced empty reply — keeping original.");
                        return reply;
                    }

                    var regenArtifact = new CognitiveArtifact
                    {
                        Content                 = regenerated,
                        ProducerKind            = artifact.ProducerKind,
                        IntendedSink            = artifact.IntendedSink,
                        ContactName             = artifact.ContactName,
                        GeneratedAt             = DateTimeOffset.Now,
                        ContactRecentMessages   = artifact.ContactRecentMessages,
                        PriorAniMessages        = artifact.PriorAniMessages,
                        SystemPromptText        = artifact.SystemPromptText,
                        WriterInnerThought      = artifact.WriterInnerThought,
                    };

                    OutputGateResult regenResult;
                    try
                    {
                        regenResult = await _outputGate.EvaluateAsync(regenArtifact, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex,
                            "J.5a gate re-evaluation threw on regen — falling back to safe acknowledgement (regen would have dispatched uncovered).");
                        return SafeAcknowledgement;
                    }

                    if (regenResult.Verdict == OutputGateVerdict.Pass)
                    {
                        _log.LogInformation("J.5a gate remediation succeeded — regenerated reply passes gate.");
                        _gateTripTracker?.Record(new GateTripEvent(
                            Timestamp:        DateTimeOffset.UtcNow,
                            ProducerKind:     "ConversationReply",
                            FiredInvariants:  string.Join(",", gateResult.FiredInvariants),
                            Outcome:          GateTripOutcome.RemediatedOk));
                        return regenerated;
                    }

                    _log.LogWarning(
                        "J.5a gate remediation FAILED re-eval [{Fired}] — verdict={Verdict}, hint={Hint}; falling back to safe acknowledgement.",
                        string.Join(",", regenResult.FiredInvariants), regenResult.Verdict, regenResult.RemediationHint);
                    _gateTripTracker?.Record(new GateTripEvent(
                        Timestamp:        DateTimeOffset.UtcNow,
                        ProducerKind:     "ConversationReply",
                        FiredInvariants:  string.Join(",", regenResult.FiredInvariants),
                        Outcome:          GateTripOutcome.FellThroughToSafeAck));
                    return SafeAcknowledgement;
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "J.5a gate remediation regeneration failed — keeping original reply.");
                    return reply;
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
