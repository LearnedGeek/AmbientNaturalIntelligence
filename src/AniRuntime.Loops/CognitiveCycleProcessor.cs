using System.Text.Json;
using AniRuntime.Actions;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.LLM;
using Microsoft.Extensions.Logging;

namespace AniRuntime.Loops;

/// <summary>
/// Ani's full cognitive cycle, executed once per scheduled wake.
///
/// Phase sequence:
///   1. Perception  — poll all enabled sources since last cycle
///   2. Context     — build snapshot once, share across all phases
///   3. Inner thought — private LLM call; score Mark valence; persist
///   4. Desire update — apply temporal drift and trigger weights
///   5. Outreach    — conditional on desire threshold; dispatch or cooldown
///
/// Constructor is kept to 5 dependencies per code quality standards.
/// PromptBuilder is stateless and called statically.
/// Perception sources are injected as IEnumerable<IPerceptionSource>.
/// </summary>
public class CognitiveCycleProcessor
{
    private readonly IMemoryService                  _memory;
    private readonly IOllamaClient                   _ollama;
    private readonly DesireEngine                    _desire;
    private readonly AniActionDispatcher             _dispatcher;
    private readonly IEnumerable<IPerceptionSource>  _sources;
    private readonly ILogger<CognitiveCycleProcessor> _log;

    private DateTimeOffset _lastCycleAt = DateTimeOffset.UtcNow;

    public CognitiveCycleProcessor(
        IMemoryService                 memory,
        IOllamaClient                  ollama,
        DesireEngine                   desire,
        AniActionDispatcher            dispatcher,
        IEnumerable<IPerceptionSource> sources,
        ILogger<CognitiveCycleProcessor> log)
    {
        _memory     = memory;
        _ollama     = ollama;
        _desire     = desire;
        _dispatcher = dispatcher;
        _sources    = sources;
        _log        = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _log.LogDebug("Cognitive cycle starting");

        // Phase 1: Perception
        var perceptions = await PollPerceptionSourcesAsync(ct).ConfigureAwait(false);

        // Phase 2: Context snapshot — built once, shared across all phases
        var snapshot = await BuildContextSnapshotAsync(perceptions, ct).ConfigureAwait(false);

        // Phase 3: Inner thought
        var (thought, valence) = await RunInnerThoughtAsync(snapshot, ct).ConfigureAwait(false);

        await _memory.SaveAsync(new MemoryRecord
        {
            Type        = MemoryType.InnerThought,
            Content     = thought,
            MarkValence = valence,
            Importance  = valence > 0.6f ? 0.8f : 0.3f,
            OccurredAt  = DateTimeOffset.UtcNow,
        }, ct).ConfigureAwait(false);

        _log.LogDebug("Inner thought (valence={Valence:F2}): {Thought}",
            valence, thought[..Math.Min(80, thought.Length)]);

        // Phase 4: Desire update
        await _desire.ApplyDriftAsync(ct).ConfigureAwait(false);

        if (valence > 0.6f)
            await _desire.AddTriggerAsync(
                TriggerType.SpontaneousThought, valence,
                $"thought: {thought[..Math.Min(60, thought.Length)]}", ct).ConfigureAwait(false);

        // Phase 5: Outreach — only if desire crosses threshold
        if (!await _desire.ShouldReachOutAsync(ct).ConfigureAwait(false))
        {
            _log.LogDebug("Desire below threshold — no outreach this cycle");
            _lastCycleAt = DateTimeOffset.UtcNow;
            return;
        }

        await RunOutreachAsync(snapshot, thought, ct).ConfigureAwait(false);
        _lastCycleAt = DateTimeOffset.UtcNow;
    }

    // ── Private phases ────────────────────────────────────────────────────────

    private async Task<List<PerceptionEvent>> PollPerceptionSourcesAsync(CancellationToken ct)
    {
        var events = new List<PerceptionEvent>();

        foreach (var source in _sources.Where(s => s.IsEnabled))
        {
            try
            {
                var polled = await source.PollAsync(_lastCycleAt, ct).ConfigureAwait(false);
                events.AddRange(polled);
            }
            catch (Exception ex)
            {
                // A failing perception source must not kill the cognitive cycle
                _log.LogWarning(ex, "Perception source '{Source}' failed — skipping", source.SourceName);
            }
        }

        return events;
    }

    private async Task<ContextSnapshot> BuildContextSnapshotAsync(
        List<PerceptionEvent> perceptions, CancellationToken ct)
    {
        var charState   = await _memory.GetCharacterStateAsync(ct).ConfigureAwait(false);
        var desireState = await _desire.GetStateAsync(ct).ConfigureAwait(false);
        var recentMem   = await _memory.GetByTypeAsync(MemoryType.Episodic, 10, ct).ConfigureAwait(false);
        var openLoops   = await _memory.GetOpenLoopsAsync(ct).ConfigureAwait(false);

        return new ContextSnapshot
        {
            CharacterState = charState,
            DesireState    = desireState,
            RecentMemory   = recentMem.ToList(),
            OpenLoops      = openLoops.ToList(),
            Perceptions    = perceptions,
            BuiltAt        = DateTimeOffset.UtcNow,
        };
    }

    private async Task<(string thought, float valence)> RunInnerThoughtAsync(
        ContextSnapshot snapshot, CancellationToken ct)
    {
        var thoughtPrompt = PromptBuilder.BuildInnerThoughtPrompt(snapshot);
        var thought       = await _ollama.ChatAsync(
            thoughtPrompt.System, snapshot.RecentHistory, thoughtPrompt.User, ct)
            .ConfigureAwait(false);

        var valence = await ScoreMarkValenceAsync(thought, snapshot.CharacterState, ct)
            .ConfigureAwait(false);

        return (thought, valence);
    }

    private async Task<float> ScoreMarkValenceAsync(
        string thought, CharacterStateDoc character, CancellationToken ct)
    {
        var prompt = PromptBuilder.BuildValenceScoringPrompt(thought, character);
        var raw    = await _ollama.ChatAsync(
            prompt.System, Array.Empty<ChatMessage>(), prompt.User, ct)
            .ConfigureAwait(false);

        return ParseValenceScore(raw);
    }

    private async Task RunOutreachAsync(
        ContextSnapshot snapshot, string recentThought, CancellationToken ct)
    {
        var outreachPrompt = PromptBuilder.BuildOutreachPrompt(snapshot, recentThought);
        var raw            = await _ollama.ChatAsync(
            outreachPrompt.System, snapshot.RecentHistory, outreachPrompt.User, ct)
            .ConfigureAwait(false);

        var decision = ParseOutreachDecision(raw);

        if (!decision.ShouldReach)
        {
            _log.LogDebug("Ani chose not to reach out: {Reasoning}", decision.Reasoning);
            await _desire.ApplyCooldownAsync(TimeSpan.FromMinutes(20), ct).ConfigureAwait(false);
            return;
        }

        _log.LogInformation("Ani reaching out: {Message}", decision.Message);

        await _dispatcher.DispatchAsync(decision, ct).ConfigureAwait(false);
        await _desire.ResetAfterOutreachAsync(ct).ConfigureAwait(false);

        await _memory.SaveAsync(new MemoryRecord
        {
            Type       = MemoryType.Episodic,
            Content    = $"Ani reached out: {decision.Message}",
            Importance = 0.7f,
            OccurredAt = DateTimeOffset.UtcNow,
        }, ct).ConfigureAwait(false);
    }

    // ── Parsing ───────────────────────────────────────────────────────────────

    private static float ParseValenceScore(string raw)
    {
        try
        {
            var doc   = JsonDocument.Parse(raw.Trim());
            var score = doc.RootElement.GetProperty("score").GetDouble();
            return (float)Math.Clamp(score, 0.0, 1.0);
        }
        catch
        {
            // Unparseable valence defaults to neutral — not a fatal failure
            return 0.3f;
        }
    }

    private static OutreachDecision ParseOutreachDecision(string raw)
    {
        try
        {
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            return JsonSerializer.Deserialize<OutreachDecision>(raw.Trim(), opts)
                   ?? new OutreachDecision { ShouldReach = false };
        }
        catch
        {
            // Unparseable outreach decision defaults to no-reach — never dispatch on bad data
            return new OutreachDecision { ShouldReach = false, Reasoning = "parse failure" };
        }
    }
}
