using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;

namespace AniRuntime.Memory;

/// <summary>
/// Phase 5 SOLID refactor (2026-05-18) — composite façade implementing
/// the full <see cref="IMemoryService"/> by delegating each method to
/// one of the five focused EF services. Replaces the previous
/// monolithic <c>EfMemoryService</c> class.
///
/// <para>
/// The façade exists for **backward compatibility only**: 10 existing
/// consumers inject <see cref="IMemoryService"/> as the composite. Per
/// the ISP guidance on <see cref="IMemoryService"/> itself, new
/// consumers should depend on the narrowest split interface they need
/// (<see cref="IMemoryPersistence"/>, <see cref="IMemorySearch"/>,
/// <see cref="IStateStore"/>, <see cref="IMemoryAnalytics"/>,
/// <see cref="IMemoryMaintenance"/>) — and over time the existing 10
/// consumers should be migrated to those narrower interfaces, at
/// which point this façade can be deleted entirely.
/// </para>
/// <para>
/// **No logic lives in this class** — every method is a one-line
/// delegate to one of the injected focused services. Adding behaviour
/// here would be a SOLID violation that this refactor is specifically
/// designed to prevent.
/// </para>
/// </summary>
public sealed class EfMemoryServiceFacade : IMemoryService
{
    private readonly IMemoryPersistence _persistence;
    private readonly IMemorySearch _search;
    private readonly IStateStore _state;
    private readonly IMemoryAnalytics _analytics;
    private readonly IMemoryMaintenance _maintenance;

    public EfMemoryServiceFacade(
        IMemoryPersistence persistence,
        IMemorySearch search,
        IStateStore state,
        IMemoryAnalytics analytics,
        IMemoryMaintenance maintenance)
    {
        _persistence = persistence;
        _search      = search;
        _state       = state;
        _analytics   = analytics;
        _maintenance = maintenance;
    }

    // ── IMemoryPersistence ─────────────────────────────────────────────
    public Task SaveAsync(MemoryRecord record, CancellationToken ct = default)
        => _persistence.SaveAsync(record, ct);

    public Task SaveCharacterStateAsync(CharacterStateDoc doc, CancellationToken ct = default)
        => _persistence.SaveCharacterStateAsync(doc, ct);

    public Task SaveDesireStateAsync(DesireState state, CancellationToken ct = default)
        => _persistence.SaveDesireStateAsync(state, ct);

    public Task SaveEmotionalStateAsync(EmotionalState state, CancellationToken ct = default)
        => _persistence.SaveEmotionalStateAsync(state, ct);

    public Task SaveEmotionalContributionAsync(EmotionalContribution contribution, CancellationToken ct = default)
        => _persistence.SaveEmotionalContributionAsync(contribution, ct);

    public Task SaveRelationshipHealthAsync(RelationshipHealth health, CancellationToken ct = default)
        => _persistence.SaveRelationshipHealthAsync(health, ct);

    public Task AdjustImportanceAsync(Guid id, float delta, CancellationToken ct = default)
        => _persistence.AdjustImportanceAsync(id, delta, ct);

    public Task AnchorMemoryAsync(Guid id, string reason, CancellationToken ct = default)
        => _persistence.AnchorMemoryAsync(id, reason, ct);

    public Task DeleteAsync(Guid id, CancellationToken ct = default)
        => _persistence.DeleteAsync(id, ct);

    public Task SaveConfabulationFlagAsync(
        string contactMessage, string aniReply, string? topicCategory = null,
        string? notes = null, string? canonicalCategory = null, CancellationToken ct = default)
        => _persistence.SaveConfabulationFlagAsync(contactMessage, aniReply, topicCategory, notes, canonicalCategory, ct);

    public Task<int> MarkConversationTurnInvalidAsync(string aniReplyContent, CancellationToken ct = default)
        => _persistence.MarkConversationTurnInvalidAsync(aniReplyContent, ct);

    public Task<int> MarkConversationTurnConfirmedAsync(string aniReplyContent, DateTimeOffset confirmedAt, CancellationToken ct = default)
        => _persistence.MarkConversationTurnConfirmedAsync(aniReplyContent, confirmedAt, ct);

    public Task<IEnumerable<MemoryRecord>> GetRecentAsync(int limit = 10, CancellationToken ct = default)
        => _persistence.GetRecentAsync(limit, ct);

    public Task<IEnumerable<MemoryRecord>> GetDecayEligibleAsync(int limit = 10, CancellationToken ct = default)
        => _persistence.GetDecayEligibleAsync(limit, ct);

    public Task MarkCompressedAsync(IEnumerable<Guid> sourceIds, Guid gistId, CancellationToken ct = default)
        => _persistence.MarkCompressedAsync(sourceIds, gistId, ct);

    // ── IMemorySearch ──────────────────────────────────────────────────
    public Task<IEnumerable<MemoryRecord>> SearchAsync(
        string query, int topK = 10, CancellationToken ct = default,
        bool enforceOriginQuota = false)
        => _search.SearchAsync(query, topK, ct, enforceOriginQuota);

    public Task<IEnumerable<ScoredMemory>> SearchWithScoresAsync(string query, int topK = 10, CancellationToken ct = default)
        => _search.SearchWithScoresAsync(query, topK, ct);

    public Task<IEnumerable<MemoryRecord>> SearchByTypeAsync(string query, MemoryType type, int topK = 5, CancellationToken ct = default)
        => _search.SearchByTypeAsync(query, type, topK, ct);

    public Task<IEnumerable<MemoryRecord>> GetByTypeAsync(MemoryType type, int limit = 50, CancellationToken ct = default)
        => _search.GetByTypeAsync(type, limit, ct);

    public Task<IEnumerable<MemoryRecord>> GetAnchoredMemoriesAsync(CancellationToken ct = default)
        => _search.GetAnchoredMemoriesAsync(ct);

    public Task<IEnumerable<MemoryRecord>> GetLinkedMemoriesAsync(
        Guid memoryId, string? relationshipType = null, CancellationToken ct = default)
        => _search.GetLinkedMemoriesAsync(memoryId, relationshipType, ct);

    public Task<IEnumerable<ScoredMemory>> SearchByTierAsync(
        string query, EpistemicTier tier, int topK = 5, CancellationToken ct = default,
        float minCosine = 0.0f)
        => _search.SearchByTierAsync(query, tier, topK, ct, minCosine);

    public Task<IEnumerable<MemoryRecord>> GetByTierAsync(EpistemicTier tier, int limit = 20, CancellationToken ct = default)
        => _search.GetByTierAsync(tier, limit, ct);

    // ── IStateStore ────────────────────────────────────────────────────
    public Task<CharacterStateDoc> GetCharacterStateAsync(CancellationToken ct = default)
        => _state.GetCharacterStateAsync(ct);

    public Task<DesireState> GetDesireStateAsync(CancellationToken ct = default)
        => _state.GetDesireStateAsync(ct);

    public Task<EmotionalState> GetEmotionalStateAsync(CancellationToken ct = default)
        => _state.GetEmotionalStateAsync(ct);

    public Task<RelationshipHealth> GetRelationshipHealthAsync(CancellationToken ct = default)
        => _state.GetRelationshipHealthAsync(ct);

    public Task<List<EmotionalStateSnapshot>> GetEmotionalHistoryAsync(int hours, CancellationToken ct = default)
        => _state.GetEmotionalHistoryAsync(hours, ct);

    // ── IMemoryAnalytics ───────────────────────────────────────────────
    public Task<IEnumerable<OpenLoop>> GetOpenLoopsAsync(CancellationToken ct = default)
        => _analytics.GetOpenLoopsAsync(ct);

    public Task<List<MemoryContradiction>> GetFlaggedContradictionsAsync(bool includeResolved = false, CancellationToken ct = default)
        => _analytics.GetFlaggedContradictionsAsync(includeResolved, ct);

    public Task<int> GetRecentMessageCountAsync(int days, CancellationToken ct = default)
        => _analytics.GetRecentMessageCountAsync(days, ct);

    public Task<float> GetAverageConversationValenceAsync(int days, CancellationToken ct = default)
        => _analytics.GetAverageConversationValenceAsync(days, ct);

    public Task<(int outreach, int inbound)> GetInitiativeBalanceAsync(int days, CancellationToken ct = default)
        => _analytics.GetInitiativeBalanceAsync(days, ct);

    public Task<List<EmotionalContribution>> GetActiveContributionsAsync(CancellationToken ct = default)
        => _analytics.GetActiveContributionsAsync(ct);

    public Task<List<EmotionalContribution>> GetContributionsSinceAsync(DateTimeOffset since, CancellationToken ct = default)
        => _analytics.GetContributionsSinceAsync(since, ct);

    public Task<List<string>> GetProcessedThemesAsync(int maxThemes = 5, CancellationToken ct = default)
        => _analytics.GetProcessedThemesAsync(maxThemes, ct);

    public Task<AttributionDistribution> GetAttributionDistributionAsync(CancellationToken ct = default)
        => _analytics.GetAttributionDistributionAsync(ct);

    // ── IMemoryMaintenance ─────────────────────────────────────────────
    public Task ResolveOpenLoopAsync(Guid id, CancellationToken ct = default)
        => _maintenance.ResolveOpenLoopAsync(id, ct);

    public Task ResolveContradictionAsync(Guid newMemoryId, Guid existingMemoryId, CancellationToken ct = default)
        => _maintenance.ResolveContradictionAsync(newMemoryId, existingMemoryId, ct);

    public Task CleanupDecayedContributionsAsync(CancellationToken ct = default)
        => _maintenance.CleanupDecayedContributionsAsync(ct);

    public Task ExpireContributionAsync(Guid id, CancellationToken ct = default)
        => _maintenance.ExpireContributionAsync(id, ct);

    public Task<(int MergeCount, int LinkCount)> RebuildMemoryLinksAsync(CancellationToken ct = default)
        => _maintenance.RebuildMemoryLinksAsync(ct);

    public Task<int> GetLinkCountAsync(CancellationToken ct = default)
        => _maintenance.GetLinkCountAsync(ct);

    public Task<IReadOnlyList<MemoryLink>> GetAllLinksAsync(CancellationToken ct = default)
        => _maintenance.GetAllLinksAsync(ct);

    public Task<List<AuditEntry>> GetRecentAuditEntriesAsync(int limit = 20, CancellationToken ct = default)
        => _maintenance.GetRecentAuditEntriesAsync(limit, ct);

    public Task<bool> RestoreFromAuditAsync(long auditId, CancellationToken ct = default)
        => _maintenance.RestoreFromAuditAsync(auditId, ct);
}
