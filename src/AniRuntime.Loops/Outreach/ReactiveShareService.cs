using AniRuntime.Actions;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.LLM;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Loops.Outreach;

/// <summary>
/// RSS-driven reactive share flow. Bypasses the desire engine when a
/// high-relevance perception arrives but is rate-limited via a daily
/// counter + per-share cooldown. Holds counter/day state — registered
/// Singleton.
///
/// <para>
/// Theme N N.6 (May 10, 2026): the selector + composer wiring now run
/// here so reactive shares get the same frame-aware composition as
/// proactive outreach (gated on <c>OutreachFrameSelectorEnabled</c>).
/// </para>
/// </summary>
public sealed class ReactiveShareService : IReactiveShareService
{
    private readonly IStateStore _state;
    private readonly IOllamaClient _ollama;
    private readonly AniActionDispatcher _dispatcher;
    private readonly DesireEngine _desire;
    private readonly IMemoryPersistence _persist;
    private readonly IOutboundThreadRecorder _threadRecorder;
    private readonly IClosedConversationStore? _closedStore;
    private readonly IOutreachFrameSelector? _frameSelector;
    private readonly AniOptions _aniOptions;
    private readonly ILogger<ReactiveShareService> _log;

    private int _shareCount;
    private DateTimeOffset _shareDay = DateTimeOffset.MinValue;

    public ReactiveShareService(
        IStateStore state,
        IOllamaClient ollama,
        AniActionDispatcher dispatcher,
        DesireEngine desire,
        IMemoryPersistence persist,
        IOutboundThreadRecorder threadRecorder,
        IOptions<AniOptions> aniOptions,
        ILogger<ReactiveShareService> log,
        IClosedConversationStore? closedStore = null,
        IOutreachFrameSelector? frameSelector = null)
    {
        _state         = state         ?? throw new ArgumentNullException(nameof(state));
        _ollama        = ollama        ?? throw new ArgumentNullException(nameof(ollama));
        _dispatcher    = dispatcher    ?? throw new ArgumentNullException(nameof(dispatcher));
        _desire        = desire        ?? throw new ArgumentNullException(nameof(desire));
        _persist        = persist        ?? throw new ArgumentNullException(nameof(persist));
        _threadRecorder = threadRecorder ?? throw new ArgumentNullException(nameof(threadRecorder));
        _closedStore    = closedStore;
        _frameSelector = frameSelector;
        _aniOptions    = aniOptions.Value;
        _log           = log ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<bool> TryShareAsync(
        List<PerceptionEvent> perceptions, CharacterStateDoc charState, CancellationToken ct)
    {
        // F-5 Phase 2 (2026-08-24) — phase scope tag so log lines emitted
        // inside reactive-share evaluation + composition render as
        // [cid:.../ReactiveShare] and are filterable by phase across cycles.
        using var phaseScope = _log.BeginScope(
            new Dictionary<string, object> { ["CyclePhase"] = "ReactiveShare" });

        var threshold = (float)_aniOptions.ReactiveShareThreshold;
        var shareable = perceptions
            .Where(p => p.SourceName == "rss" && p.ContactRelevance >= threshold)
            .OrderByDescending(p => p.ContactRelevance)
            .FirstOrDefault();

        if (shareable is null)
            return false;

        if (_desire.IsNightHours())
        {
            _log.LogDebug("Reactive share blocked — night hours");
            return false;
        }

        var today = DateTimeOffset.Now.Date;
        if (_shareDay.Date != today)
        {
            _shareCount = 0;
            _shareDay   = DateTimeOffset.Now;
        }

        if (_shareCount >= _aniOptions.MaxReactiveSharesPerDay)
        {
            _log.LogDebug("Reactive share blocked — daily limit ({Limit}) reached", _aniOptions.MaxReactiveSharesPerDay);
            return false;
        }

        var desireState = await _desire.GetStateAsync(ct).ConfigureAwait(false);
        var sinceLastOutreach = DateTimeOffset.UtcNow - desireState.LastOutreach;
        if (sinceLastOutreach.TotalMinutes < _aniOptions.ReactiveShareCooldownMinutes)
        {
            _log.LogDebug("Reactive share blocked — only {Minutes:F0} min since last outreach (need {Required})",
                sinceLastOutreach.TotalMinutes, _aniOptions.ReactiveShareCooldownMinutes);
            return false;
        }

        _log.LogInformation("Reactive share triggered: {Summary} (relevance={Relevance:F2})",
            shareable.Summary, shareable.ContactRelevance);

        // ─── Theme N Phase N.6 — frame wiring (gated by OutreachFrameSelectorEnabled) ─
        // F-1 Phase 8b (2026-08-19): SelectFrameAsync now returns
        // IOutreachFrameEnvelope; passthrough properties keep call sites unchanged.
        IOutreachFrameEnvelope? selectedFrame = null;
        string? sharedTopicGist = null;
        if (_aniOptions.OutreachFrameSelectorEnabled && _frameSelector is not null)
        {
            ClosedConversationRecord? recentClosed = null;
            if (_closedStore is not null)
            {
                try
                {
                    var recent = await _closedStore.GetRecentAsync(limit: 1, ct).ConfigureAwait(false);
                    recentClosed   = recent.FirstOrDefault();
                    sharedTopicGist = recentClosed?.Gist;
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "Reactive share: closed-conversation lookup failed — proceeding without shared-topic anchor");
                }
            }

            var selectorSnapshot = new ContextSnapshot
            {
                CharacterState           = charState,
                Perceptions              = perceptions,
                RecentClosedConversation = recentClosed,
                BuiltAt                  = DateTimeOffset.UtcNow,
            };

            selectedFrame = await _frameSelector.SelectFrameAsync(selectorSnapshot, ct).ConfigureAwait(false);
            if (selectedFrame.FrameType == OutreachFrameType.None)
            {
                _log.LogInformation(
                    "Reactive share: frame-selector returned None — suppressing share (substrate too thin)");
                return false;
            }
        }

        var currentMood = await _state.GetEmotionalStateAsync(ct).ConfigureAwait(false);
        var prompt = PromptBuilder.BuildReactiveSharePrompt(
            charState, shareable.Summary, currentMood,
            frame: selectedFrame?.Content,  // F-1 Phase 8b: unwrap envelope for downstream prompt-builder
            sharedTopicGist: sharedTopicGist);
        var message = await _ollama.ChatAsync(
            prompt.System, Array.Empty<ChatMessage>(), prompt.User, ct).ConfigureAwait(false);

        message = AniRuntime.Core.Utilities.MessageCleaner.Clean(message);
        if (string.IsNullOrWhiteSpace(message))
        {
            _log.LogWarning("Reactive share message was empty — skipping");
            return false;
        }

        _log.LogInformation("Reactive share: {Message}", message);

        // F-1 Phase 8d: producer-side wrap for provenance ("outreach-decision.reactive-share").
        var envelope = new OutreachDecisionEnvelope
        {
            Decision = new OutreachDecision
            {
                ShouldReach = true,
                Message     = message,
                ActionType  = ActionTypes.Sms,
                Reasoning   = $"reactive share: {shareable.Summary[..Math.Min(60, shareable.Summary.Length)]}",
            },
            Source = OutreachDecisionSource.ReactiveShare,
        };
        envelope.LogProvenance(_log);
        await _dispatcher.DispatchAsync(envelope.Decision, ct).ConfigureAwait(false);
        await _desire.ResetAfterOutreachAsync(ct).ConfigureAwait(false);

        _shareCount++;

        // Apr 29, 2026 (Theme E): record outbound in conversation thread.
        await _threadRecorder.RecordAsync(message, ct).ConfigureAwait(false);

        // F-2 Phase 1 P6 (2026-08-22) — reactive share is Ani-authored, verified.
        //
        // F-3 U7 (2026-08-24) — construct the composer emission envelope
        // first, then project the attribution triple from it. Mirrors U6
        // for outreach: Option A only (user-facing prose surface, so no
        // structured claim emission that would leak into wire text). The
        // emission carries the composer identity (ReactiveShare) +
        // timestamp; downstream projection is structurally equivalent to
        // the pre-U7 factory call per the U2 migration equivalence pin.
        var shareTime = DateTimeOffset.UtcNow;
        // Persona-lock unwind (2026-08-25): first-person prefix mirrors the
        // FormatSpeaker + FormatOutreach discipline in WellKnown.MemoryPrefixes.
        // The pre-fix "Ani shared with Mark: ..." format was the one
        // Ani-authored persistence site emitting third-person prose; every
        // reactive-share record then entered substrate as third-person and
        // primed perspective drift in downstream composers. AttributedTo=Ani
        // + Provenance=Episodic + Type=Episodic still carry the structural
        // authorship signal — the prose no longer needs to duplicate it.
        var shareContent = $"I shared with {charState.PrimaryContactName}: {message} (about: {shareable.Summary})";
        var shareEmission = ComposerEmissionExtensions.AniEmission(
            content:      shareContent,
            composerRole: CognitiveProducerKind.ReactiveShare,
            emittedAt:    shareTime);
        var shareAttribution = shareEmission.ToAttributionTriple();
        var shareRecord = new MemoryRecord
        {
            Type       = MemoryType.Episodic,
            Content    = shareEmission.Content,
            Importance = 0.5f,
            OccurredAt = shareTime,
            Provenance = EpistemicTier.Episodic,
            AttributedTo               = shareAttribution.AttributedTo,
            AttributedAt               = shareAttribution.AttributedAt,
            AttributedSourceRecordId   = shareAttribution.SourceRecordId,
            AttributedSourceDescriptor = shareAttribution.SourceDescriptor,
            AttributionTrust           = shareAttribution.Trust,
        };
        await _persist.SaveAsync(shareRecord, ct).ConfigureAwait(false);
        shareRecord.LogAttribution(_log);

        // 2026-09-12 — also persist the underlying source as a Facts-tier
        // record so Ani can cite the URL/outlet/headline when Mark asks
        // "where did you see that article?" later. Before this, the source
        // metadata (URL, outlet name, headline) was in the transient
        // PerceptionEvent at compose time but got dropped after dispatch,
        // leaving Ani unable to answer follow-up questions about her own
        // reactive shares. The Facts record's Content is written with an
        // unambiguous "URL: <url>" pattern so the composer will include
        // it verbatim in a reply. Follow-up issue #155 tracks the transient-
        // fact accumulation concern (post-#97 recency-off means these
        // don't age out via time-decay; cosine gating is the natural filter).
        await PersistShareableSourceAsFactAsync(shareable, charState, shareTime, ct)
            .ConfigureAwait(false);

        return true;
    }

    /// <summary>
    /// Persists a Facts-tier record capturing the source of a reactive share
    /// (URL from PerceptionEvent.Metadata if RSS captured it, outlet name,
    /// headline, summary). Fail-open: exceptions are logged and swallowed
    /// so a source-persistence problem never blocks the share dispatch,
    /// which has already succeeded.
    /// </summary>
    private async Task PersistShareableSourceAsFactAsync(
        PerceptionEvent shareable,
        CharacterStateDoc charState,
        DateTimeOffset shareTime,
        CancellationToken ct)
    {
        try
        {
            string? url    = null;
            string? title  = null;
            string? outlet = null;
            if (shareable.Metadata is { Count: > 0 })
            {
                if (shareable.Metadata.TryGetValue("url", out var u)) url    = u as string;
                if (shareable.Metadata.TryGetValue("title", out var t)) title  = t as string;
                if (shareable.Metadata.TryGetValue("outlet", out var o)) outlet = o as string;
            }

            // Compose content with an unambiguous "URL: <url>" pattern so
            // the composer will lift it verbatim when Mark asks for the
            // source. Fallback pieces used when RSS metadata was thin.
            var contentBuilder = new System.Text.StringBuilder();
            contentBuilder.Append("Article Ani shared with ")
                          .Append(charState.PrimaryContactName)
                          .Append(" on ")
                          .Append(shareTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))
                          .Append(": ");
            if (!string.IsNullOrWhiteSpace(title))
            {
                contentBuilder.Append('"').Append(title).Append('"');
            }
            else
            {
                contentBuilder.Append(shareable.Summary);
            }
            if (!string.IsNullOrWhiteSpace(outlet))
            {
                contentBuilder.Append(" (from ").Append(outlet).Append(')');
            }
            if (!string.IsNullOrWhiteSpace(url))
            {
                contentBuilder.Append(". URL: ").Append(url);
            }
            if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(shareable.Summary))
            {
                contentBuilder.Append(". Summary: ").Append(shareable.Summary);
            }
            var sourceContent = contentBuilder.ToString();

            var sourceRecord = new MemoryRecord
            {
                Type       = MemoryType.Semantic,
                Content    = sourceContent,
                Importance = 0.5f,
                OccurredAt = shareTime,
                Provenance = EpistemicTier.Facts,
                SourceName = "reactive-share-source",
                AttributedTo               = AttributedTo.World,
                AttributedAt               = shareTime,
                AttributionTrust           = "verified",
            };
            await _persist.SaveAsync(sourceRecord, ct).ConfigureAwait(false);
            _log.LogInformation(
                "REACTIVE_SHARE_SOURCE persisted: outlet={Outlet} hasUrl={HasUrl} contentChars={Chars}",
                outlet ?? "(none)", !string.IsNullOrWhiteSpace(url), sourceContent.Length);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Reactive-share source-persistence failed — dispatch already succeeded");
        }
    }
}
