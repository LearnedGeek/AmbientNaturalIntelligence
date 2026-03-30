using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.LLM;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Loops;

/// <summary>
/// Feature 32: Periodic reflection synthesis (Park et al.-inspired).
///
/// Every N cognitive cycles, synthesizes recent memories into higher-order
/// relational observations: "Mark's been checking on me a lot this week."
/// These become high-quality retrieval targets that produce more personal
/// conversation and feed the emergence layer with relational patterns.
///
/// Extracted as a separate phase (SRP) following the PerceptionPhase and
/// InnerThoughtPhase pattern.
/// </summary>
public class ReflectionPhase
{
    private readonly IOllamaClient _ollama;
    private readonly IMemoryPersistence _persist;
    private readonly IMemorySearch _search;
    private readonly AniOptions _options;
    private readonly ILogger<ReflectionPhase> _log;
    private int _cyclesSinceLastReflection;

    public ReflectionPhase(
        IOllamaClient ollama,
        IMemoryPersistence persist,
        IMemorySearch search,
        IOptions<AniOptions> options,
        ILogger<ReflectionPhase> log)
    {
        _ollama = ollama;
        _persist = persist;
        _search = search;
        _options = options.Value;
        _log = log;
    }

    /// <summary>
    /// Increments the cycle counter and runs reflection if the interval is reached.
    /// Returns true if reflection was performed this cycle.
    /// </summary>
    public async Task<bool> TryRunAsync(
        CharacterStateDoc characterState, CancellationToken ct)
    {
        if (!_options.ReflectionEnabled) return false;

        _cyclesSinceLastReflection++;
        if (_cyclesSinceLastReflection < _options.ReflectionCycleInterval)
            return false;

        _cyclesSinceLastReflection = 0;

        try
        {
            await RunReflectionAsync(characterState, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Reflection synthesis failed — continuing without reflection");
            return false;
        }
    }

    private async Task RunReflectionAsync(
        CharacterStateDoc characterState, CancellationToken ct)
    {
        // Retrieve recent non-reflection memories
        var recentMemories = (await _persist.GetRecentAsync(10, ct).ConfigureAwait(false)).ToList();
        if (recentMemories.Count < 3)
        {
            _log.LogDebug("Reflection skipped — only {Count} recent memories (need at least 3)", recentMemories.Count);
            return;
        }

        var contact = characterState.PrimaryContactName ?? "Mark";
        var memoryContents = recentMemories.Select(m => m.Content).ToList();

        var (system, user) = PromptBuilder.BuildReflectionSynthesisPrompt(
            characterState.Name, contact, memoryContents);

        var response = await _ollama.InnerMonologueChatAsync(
            system, Array.Empty<ChatMessage>(), user, ct, keepAlive: "0")
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(response))
        {
            _log.LogDebug("Reflection synthesis returned empty response");
            return;
        }

        // Parse observations (one per line, skip empty)
        var observations = response
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Length > 10) // Skip very short lines
            .Take(3)
            .ToList();

        var sourceIds = recentMemories.Select(m => m.Id).ToList();

        // Check ALL existing Semantic memories to avoid duplicating what we already know.
        // The reflection synthesis regenerates the same profile facts each cycle
        // ("About Mark: Learning Spanish", "Shared experience: Duck Norris").
        // Previous fix used GetRecentAsync(100) which missed existing records because
        // they weren't in the top 100 most recent across all types. Now queries
        // Semantic type directly — guaranteed to find all existing profile facts.
        var existingProfiles = (await _search.GetByTypeAsync(MemoryType.Semantic, 500, ct)
                .ConfigureAwait(false))
            .Select(m => m.Content.Length >= 50 ? m.Content[..50] : m.Content)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var saved = 0;
        foreach (var observation in observations)
        {
            // Skip if we already have a reflection memory with this prefix
            var prefix = observation.Length >= 50 ? observation[..50] : observation;
            if (existingProfiles.Contains(prefix))
            {
                _log.LogDebug("Reflection: skipping duplicate '{Prefix}...'", prefix[..Math.Min(40, prefix.Length)]);
                continue;
            }

            var record = new MemoryRecord
            {
                Type = MemoryType.Semantic,
                Content = observation,
                Importance = 0.8f,
                RelationalValence = 0.5f,
                SourceName = "reflection",
            };

            await _persist.SaveAsync(record, ct).ConfigureAwait(false);
            existingProfiles.Add(prefix); // Prevent saving duplicates within same batch
            saved++;
        }

        _log.LogInformation("Reflection synthesis: generated {Count} observations from {SourceCount} recent memories ({Saved} new, {Skipped} duplicates skipped)",
            observations.Count, recentMemories.Count, saved, observations.Count - saved);
    }
}