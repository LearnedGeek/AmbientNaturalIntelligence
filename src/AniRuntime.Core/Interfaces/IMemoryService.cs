using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

public interface IMemoryService
{
    Task SaveAsync(MemoryRecord record, CancellationToken ct = default);
    Task<IEnumerable<MemoryRecord>> SearchAsync(string query, int topK = 10, CancellationToken ct = default);
    Task<IEnumerable<ScoredMemory>> SearchWithScoresAsync(string query, int topK = 10, CancellationToken ct = default);
    Task<IEnumerable<MemoryRecord>> SearchByTypeAsync(string query, MemoryType type, int topK = 5, CancellationToken ct = default);
    Task<IEnumerable<MemoryRecord>> GetByTypeAsync(MemoryType type, int limit = 50, CancellationToken ct = default);
    Task<IEnumerable<OpenLoop>>     GetOpenLoopsAsync(CancellationToken ct = default);
    Task                            ResolveOpenLoopAsync(Guid id, CancellationToken ct = default);
    Task<CharacterStateDoc>         GetCharacterStateAsync(CancellationToken ct = default);
    Task                            SaveCharacterStateAsync(CharacterStateDoc doc, CancellationToken ct = default);
    Task<DesireState>               GetDesireStateAsync(CancellationToken ct = default);
    Task                            SaveDesireStateAsync(DesireState state, CancellationToken ct = default);
    Task<EmotionalState>            GetEmotionalStateAsync(CancellationToken ct = default);
    Task                            SaveEmotionalStateAsync(EmotionalState state, CancellationToken ct = default);
    Task                            AdjustImportanceAsync(Guid id, float delta, CancellationToken ct = default);
    Task<IEnumerable<MemoryRecord>> GetAnchoredMemoriesAsync(CancellationToken ct = default);
    Task                            AnchorMemoryAsync(Guid id, string reason, CancellationToken ct = default);

    // Feature 4: Relationship health persistence
    Task<RelationshipHealth>        GetRelationshipHealthAsync(CancellationToken ct = default);
    Task                            SaveRelationshipHealthAsync(RelationshipHealth health, CancellationToken ct = default);

    // Feature 4/8: Emotional state history queries
    Task<List<EmotionalStateSnapshot>> GetEmotionalHistoryAsync(int hours, CancellationToken ct = default);

    // Feature 15: Memory contradiction flagging
    Task<List<MemoryContradiction>> GetFlaggedContradictionsAsync(bool includeResolved = false, CancellationToken ct = default);
    Task ResolveContradictionAsync(Guid newMemoryId, Guid existingMemoryId, CancellationToken ct = default);

    // Feature 4: Interaction metrics for health computation
    Task<int> GetRecentMessageCountAsync(int days, CancellationToken ct = default);
    Task<float> GetAverageConversationValenceAsync(int days, CancellationToken ct = default);
    Task<(int outreach, int inbound)> GetInitiativeBalanceAsync(int days, CancellationToken ct = default);

    // Emotional contributions — per-thought decay model
    Task SaveEmotionalContributionAsync(EmotionalContribution contribution, CancellationToken ct = default);
    Task<List<EmotionalContribution>> GetActiveContributionsAsync(CancellationToken ct = default);
    Task<List<string>> GetProcessedThemesAsync(int maxThemes = 5, CancellationToken ct = default);
    Task CleanupDecayedContributionsAsync(CancellationToken ct = default);
    Task ExpireContributionAsync(Guid id, CancellationToken ct = default);
}
