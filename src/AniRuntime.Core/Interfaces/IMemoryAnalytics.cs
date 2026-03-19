using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Analytical queries over memory — open loops, contradictions, metrics.
/// Split from IMemoryService (ISP) — analytics consumers don't need
/// CRUD or search operations.
/// </summary>
public interface IMemoryAnalytics
{
    Task<IEnumerable<OpenLoop>> GetOpenLoopsAsync(CancellationToken ct = default);
    Task<List<MemoryContradiction>> GetFlaggedContradictionsAsync(bool includeResolved = false, CancellationToken ct = default);
    Task<int> GetRecentMessageCountAsync(int days, CancellationToken ct = default);
    Task<float> GetAverageConversationValenceAsync(int days, CancellationToken ct = default);
    Task<(int outreach, int inbound)> GetInitiativeBalanceAsync(int days, CancellationToken ct = default);
    Task<List<EmotionalContribution>> GetActiveContributionsAsync(CancellationToken ct = default);
    Task<List<EmotionalContribution>> GetContributionsSinceAsync(DateTimeOffset since, CancellationToken ct = default);
    Task<List<string>> GetProcessedThemesAsync(int maxThemes = 5, CancellationToken ct = default);
}
