using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;

namespace AniRuntime.Actions;

/// <summary>
/// Issue #96 (2026-07-15) — First concrete <see cref="IToolCallableAction"/>.
/// Wraps <see cref="IMemorySearch"/> so the LLM can invoke targeted memory
/// lookup as a tool call rather than relying on the standard turn-scoped
/// retrieval that composed the pipeline context.
///
/// **Descriptor wording** matches the phrasing empirically validated on
/// 2026-07-15 against the tool-call fixture — the fixture's tool description
/// is the source of truth for the wording, and this class carries the same
/// text so the classifier sees identical framing in production.
///
/// **Substrate safety.** The action returns a short human-readable string
/// synthesizing the top-K hits. If the runtime persists that string at all,
/// it enters as <c>Provenance = Interior</c> per Issue #96 acceptance
/// criteria — never as Facts / Episodic. This action does NOT itself persist
/// anything; the caller (turn-level loop, deferred to a later PR) decides
/// whether and how to journal the result.
/// </summary>
public sealed class RecallMemoryAction : IToolCallableAction
{
    private readonly IMemorySearch                _search;
    private readonly ILogger<RecallMemoryAction>  _log;

    public ToolDescriptor Descriptor { get; } = new(
        Name:        "recall_memory",
        Description:
            "Search Ani's memory for anything she may already know — prior conversations, " +
            "events, people the user has mentioned (family, friends, coworkers), places, " +
            "plans, or preferences. This tool exists precisely to PROVIDE context Ani " +
            "doesn't currently have in view. Absence of conversation context in this prompt " +
            "is a reason to CALL the tool, not a reason to skip it.",
        ParameterSchema: new Dictionary<string, string>
        {
            ["query"] = "string — the phrase to search memory for",
            ["tier"]  = "string (optional) — 'facts' | 'episodic' | 'interior'; omit for a full-pool search",
        });

    public RecallMemoryAction(IMemorySearch search, ILogger<RecallMemoryAction> log)
    {
        _search = search ?? throw new ArgumentNullException(nameof(search));
        _log    = log    ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<string> InvokeAsync(
        IReadOnlyDictionary<string, string> arguments,
        CancellationToken                   ct)
    {
        if (!arguments.TryGetValue("query", out var query) || string.IsNullOrWhiteSpace(query))
        {
            _log.LogWarning("RECALL_MEMORY_ERROR reason=missing_query arg_count={ArgCount}", arguments.Count);
            return "recall_memory error: no query provided";
        }

        var tierArg = arguments.TryGetValue("tier", out var t) ? t?.Trim().ToLowerInvariant() : null;
        var tier = tierArg switch
        {
            "facts"    => (EpistemicTier?)EpistemicTier.Facts,
            "episodic" => (EpistemicTier?)EpistemicTier.Episodic,
            "interior" => (EpistemicTier?)EpistemicTier.Interior,
            _          => null,
        };

        const int topK = 5;
        try
        {
            IReadOnlyList<ScoredMemory> hits;
            if (tier.HasValue)
            {
                var pool = await _search.SearchByTierAsync(query, tier.Value, topK, ct).ConfigureAwait(false);
                hits = pool.ToList();
            }
            else
            {
                var pool = await _search.SearchWithScoresAsync(query, topK, ct).ConfigureAwait(false);
                hits = pool.ToList();
            }

            _log.LogInformation(
                "RECALL_MEMORY_OK query='{Query}' tier={Tier} hits={HitCount}",
                query, tier?.ToString() ?? "(all)", hits.Count);

            if (hits.Count == 0)
            {
                return $"recall_memory: no results for '{query}'"
                     + (tier.HasValue ? $" in {tier.Value} tier" : "");
            }

            return FormatHits(query, tier, hits);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "RECALL_MEMORY_ERROR query='{Query}' tier={Tier}", query, tier);
            return $"recall_memory error: {ex.GetType().Name}";
        }
    }

    internal static string FormatHits(string query, EpistemicTier? tier, IReadOnlyList<ScoredMemory> hits)
    {
        var header = tier.HasValue
            ? $"recall_memory('{query}', tier={tier.Value}): {hits.Count} result(s)"
            : $"recall_memory('{query}'): {hits.Count} result(s)";

        var lines = hits.Select((h, i) =>
        {
            var content = h.Record.Content ?? string.Empty;
            if (content.Length > 200) content = content.Substring(0, 200) + "…";
            var when = h.Record.OccurredAt != default
                ? $" [{h.Record.OccurredAt:yyyy-MM-dd}]"
                : "";
            return $"{i + 1}. [{h.Record.Provenance}]{when} {content}";
        });

        return header + "\n" + string.Join("\n", lines);
    }
}
