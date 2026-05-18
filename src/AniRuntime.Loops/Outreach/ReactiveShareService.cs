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
        OutreachFrame? selectedFrame = null;
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
            frame: selectedFrame,
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

        var decision = new OutreachDecision
        {
            ShouldReach = true,
            Message     = message,
            ActionType  = ActionTypes.Sms,
            Reasoning   = $"reactive share: {shareable.Summary[..Math.Min(60, shareable.Summary.Length)]}",
        };
        await _dispatcher.DispatchAsync(decision, ct).ConfigureAwait(false);
        await _desire.ResetAfterOutreachAsync(ct).ConfigureAwait(false);

        _shareCount++;

        // Apr 29, 2026 (Theme E): record outbound in conversation thread.
        await _threadRecorder.RecordAsync(message, ct).ConfigureAwait(false);

        await _persist.SaveAsync(new MemoryRecord
        {
            Type       = MemoryType.Episodic,
            Content    = $"{charState.Name} shared with {charState.PrimaryContactName}: {message} (about: {shareable.Summary})",
            Importance = 0.5f,
            OccurredAt = DateTimeOffset.UtcNow,
            Provenance = EpistemicTier.Episodic,
        }, ct).ConfigureAwait(false);

        return true;
    }
}
