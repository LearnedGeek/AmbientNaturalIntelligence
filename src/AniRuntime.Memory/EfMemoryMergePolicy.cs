using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Core.Utilities;
using AniRuntime.Memory.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Memory;

/// <summary>
/// Phase 5 SOLID refactor (2026-05-18) — EF Core implementation of
/// <see cref="IMemoryMergePolicy"/>. Houses the Feature 30 three-tier
/// dedup-merge decision logic, the Apr 21 rumination guard, and the
/// cross-type profile correction.
///
/// <para>
/// Behaviour-preserved port of the private helpers in
/// <c>SqliteMemoryService</c>: IsRuminationAsync, FindMergeCandidateAsync,
/// MergeMemoriesAsync, TryCrossTypeProfileCorrectionAsync. The class
/// thresholds and the LLM merge prompt are identical to the original.
/// </para>
/// <para>
/// Dependencies are minimal: <see cref="IDbContextFactory{TContext}"/>
/// for queries + writes, <see cref="IOllamaClient"/> for the LLM merge
/// step + embedding regeneration after merge, <see cref="IMemoryAuditWriter"/>
/// for the merge-audit row, and <see cref="AniOptions"/> for the
/// rumination thresholds (window hours, similarity floor, cluster size).
/// </para>
/// </summary>
public sealed class EfMemoryMergePolicy : IMemoryMergePolicy
{
    // Feature 30 thresholds — exact mirror of SqliteMemoryService constants.
    private const float ExactDuplicateThreshold = 0.95f;
    private const float MergeThreshold = 0.85f;
    private const float ProfileCorrectionThreshold = 0.85f;

    // Memory types that participate in dedup-merge. Mirrored from the
    // legacy DedupableTypes set (InnerThought, Semantic, OpenLoop,
    // Commitment) — Episodic and Perception are first-class events and
    // intentionally exempt.
    private static readonly HashSet<MemoryType> DedupableTypes =
        new() { MemoryType.InnerThought, MemoryType.Semantic, MemoryType.OpenLoop, MemoryType.Commitment };

    private readonly IDbContextFactory<AniDbContext> _dbFactory;
    private readonly IOllamaClient? _ollama;
    private readonly IMemoryAuditWriter _audit;
    private readonly AniOptions _options;
    private readonly ILogger<EfMemoryMergePolicy> _log;

    public EfMemoryMergePolicy(
        IDbContextFactory<AniDbContext> dbFactory,
        IMemoryAuditWriter audit,
        IOptions<AniOptions> options,
        ILogger<EfMemoryMergePolicy> log,
        IOllamaClient? ollama = null)
    {
        _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
        _audit     = audit     ?? throw new ArgumentNullException(nameof(audit));
        _options   = options.Value;
        _log       = log       ?? throw new ArgumentNullException(nameof(log));
        _ollama    = ollama;
    }

    public async Task<bool> IsRuminationAsync(MemoryRecord record, CancellationToken ct = default)
    {
        if (record.Embedding is null) return false;

        try
        {
            var windowSince = DateTimeOffset.UtcNow.AddHours(-_options.RuminationWindowHours);
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

            var recent = await db.Memories
                .Where(m => m.Type == record.Type
                         && m.Embedding != null
                         && m.OccurredAt >= windowSince)
                .OrderByDescending(m => m.OccurredAt)
                .Take(20)
                .Select(m => m.Embedding)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            var similarCount = 0;
            foreach (var existing in recent)
            {
                if (existing is null || existing.Length != record.Embedding.Length) continue;
                var similarity = VectorMath.CosineSimilarity(record.Embedding, existing);
                if (similarity >= _options.RuminationSimilarityThreshold)
                {
                    similarCount++;
                    if (similarCount >= _options.RuminationClusterMinSize)
                        return true;
                }
            }
            return false;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Rumination guard check failed — defaulting to allow save");
            return false;
        }
    }

    public async Task<Guid?> FindExactContentDuplicateAsync(
        MemoryRecord record, CancellationToken ct = default)
    {
        if (!DedupableTypes.Contains(record.Type)) return null;
        if (string.IsNullOrEmpty(record.Content)) return null;

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

            // Match on (Type, Content, SourceName). SourceName matters because a
            // character-seed Mia record and a (hypothetical) user-typed Mia record
            // should not collapse — same content, different origin/trust tier.
            // Null SourceName matches null SourceName (EF Core translates correctly).
            var existing = await db.Memories
                .Where(m => m.Type == record.Type
                         && m.Content == record.Content
                         && m.SourceName == record.SourceName
                         && m.Id != record.Id)
                .OrderBy(m => m.CreatedAt)
                .Select(m => (Guid?)m.Id)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            return existing;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Exact-content dedup query failed — falling through to cosine-merge policy");
            return null;
        }
    }

    public async Task<MergeCandidate?> FindMergeCandidateAsync(
        MemoryRecord record, CancellationToken ct = default)
    {
        if (record.Embedding is null || !DedupableTypes.Contains(record.Type)) return null;

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var rows = await db.Memories
                .Where(m => m.Type == record.Type && m.Embedding != null)
                .OrderByDescending(m => m.OccurredAt)
                .Take(50)
                .Select(m => new { m.Id, m.Content, m.Embedding })
                .ToListAsync(ct)
                .ConfigureAwait(false);

            foreach (var row in rows)
            {
                if (row.Embedding is null || row.Embedding.Length != record.Embedding!.Length) continue;
                var similarity = VectorMath.CosineSimilarity(record.Embedding!, row.Embedding);

                if (similarity >= ExactDuplicateThreshold)
                    return new MergeCandidate(row.Id, row.Content, IsExactDuplicate: true);

                if (similarity >= MergeThreshold)
                    return new MergeCandidate(row.Id, row.Content, IsExactDuplicate: false);
            }

            return null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Merge candidate search failed — will insert as new");
            return null;
        }
    }

    public async Task<string?> MergeAsync(
        Guid existingId, string existingContent, string newContent,
        CancellationToken ct = default)
    {
        if (_ollama is null) return null;

        try
        {
            const string system =
                "You merge two memories into one concise statement. Preserve the most current and specific information. If they conflict, keep the newer information but note what changed. Output only the merged memory, nothing else. No quotes, no commentary.";
            var user = $"Old memory: {existingContent}\nNew memory: {newContent}";
            var merged = await _ollama.InnerMonologueChatAsync(
                system, Array.Empty<ChatMessage>(), user, ct, keepAlive: "0")
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(merged)) return null;

            merged = merged.Trim().Trim('"');

            var newEmbedding = await _ollama.EmbedAsync(merged, ct).ConfigureAwait(false);

            // Update the existing record in place. occurred_at is intentionally
            // preserved (see Apr 12 fix in legacy — bumping it broke Park et al.
            // recency model by accreting merges and never decaying).
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var entity = await db.Memories.FirstOrDefaultAsync(m => m.Id == existingId, ct)
                .ConfigureAwait(false);
            if (entity is null)
            {
                _log.LogWarning("Merge target {Id} disappeared between candidate-find and merge-write — skipping update", existingId);
                return null;
            }

            entity.Content   = merged;
            entity.Embedding = newEmbedding;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            await _audit.WriteAsync(
                memoryId:         existingId,
                action:           "merge",
                source:           "merge",
                contentBefore:    existingContent,
                contentAfter:     merged,
                typeBefore:       null,
                typeAfter:        null,
                importanceBefore: null,
                importanceAfter:  null,
                ct: ct).ConfigureAwait(false);

            _log.LogInformation("Memory merge: updated {ExistingId} — '{Old}' + '{New}' → '{Merged}'",
                existingId,
                existingContent[..Math.Min(40, existingContent.Length)],
                newContent[..Math.Min(40, newContent.Length)],
                merged[..Math.Min(60, merged.Length)]);

            return merged;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Memory merge failed — will insert as new record");
            return null;
        }
    }

    public async Task TryProfileCorrectionAsync(MemoryRecord record, CancellationToken ct = default)
    {
        if (record.Embedding is null || _ollama is null) return;

        // Defense-in-depth: even if the SaveAsync caller filter is bypassed
        // in the future, never merge an Ani-speaker record into Profile.
        // (Apr 12 bug: "I said to Mark: 'you're adorable...'" merged into an
        // Interest/Profile at cosine 0.727, silently overwriting canonical profile.)
        if (record.Content.StartsWith("I said to",        StringComparison.OrdinalIgnoreCase) ||
            record.Content.StartsWith("I reached out to", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

            // Only compare against Semantic "About Mark" / "Shared experience" / "Interest:" profile memories.
            var profileMemories = await db.Memories
                .Where(m => m.Type == MemoryType.Semantic
                         && m.Embedding != null
                         && (m.Content.StartsWith("About ")
                             || m.Content.StartsWith("Shared experience")
                             || m.Content.StartsWith("Interest:")))
                .OrderByDescending(m => m.OccurredAt)
                .Take(100)
                .Select(m => new { m.Id, m.Content, m.Embedding })
                .ToListAsync(ct)
                .ConfigureAwait(false);

            foreach (var row in profileMemories)
            {
                if (row.Embedding is null || row.Embedding.Length != record.Embedding.Length) continue;

                var similarity = VectorMath.CosineSimilarity(record.Embedding, row.Embedding);

                if (similarity >= ProfileCorrectionThreshold)
                {
                    _log.LogInformation(
                        "Cross-type correction candidate (cosine={Similarity:F3}): '{Existing}' may be updated by '{New}'",
                        similarity,
                        row.Content[..Math.Min(50, row.Content.Length)],
                        record.Content[..Math.Min(50, record.Content.Length)]);

                    await MergeAsync(row.Id, row.Content, record.Content, ct).ConfigureAwait(false);
                    return; // Only correct one profile memory per incoming record
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Cross-type profile correction failed — continuing without");
        }
    }
}
