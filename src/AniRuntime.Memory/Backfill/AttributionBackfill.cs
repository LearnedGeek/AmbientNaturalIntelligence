using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Memory.Entities;
using Microsoft.EntityFrameworkCore;

namespace AniRuntime.Memory.Backfill;

/// <summary>
/// Foundation Attribution (F-2) Phase 1 P3 (2026-08-21) — one-shot
/// backfill for the five attribution columns on <c>memories</c>. Runs
/// the D4 heuristic table (from
/// <c>ani-docs/spec/ANI-Attribution-Refactor-Input-Side-Plan.md</c>)
/// over existing records where <c>attributed_to = Unknown</c>. Idempotent:
/// records already attributed by prior runs OR by future P6 producer
/// wiring are skipped.
///
/// <para>
/// <b>Pure-heuristic backfill — no LLM calls.</b> Every attribution
/// decision comes from the tier + type + SourceName + content-prefix
/// combination. Records where the heuristic can't infer land as
/// <see cref="AttributedTo.Unknown"/> with
/// <c>Trust = "unverified-historical"</c> — they surface on the
/// manual-curation tail for later review. (PR #127 review-fix: the
/// prior doc mistakenly claimed <c>"unverified"</c>, which would have
/// made the idempotency guard re-process these rows forever.)
/// </para>
/// </summary>
public static class AttributionBackfill
{
    /// <summary>
    /// Apply the D4 heuristic to a single record. Pure function — no DB
    /// access, no I/O. Extracted for unit testing so the heuristic
    /// decisions are pinned independently of the runner.
    /// </summary>
    public static AttributionTriple InferAttribution(MemoryEntity e)
    {
        // "reflection" source is always Ani-authored regardless of
        // provenance tier — Ani's own synthesis output.
        if (string.Equals(e.SourceName, "reflection", StringComparison.OrdinalIgnoreCase))
            return AttributionTriple.AniAt(e.OccurredAt);

        // Facts tier — external truth, source-name determines actor
        if (e.Provenance == EpistemicTier.Facts)
        {
            return e.SourceName switch
            {
                "character-seed"   => AttributionTriple.MarkCanonical($"character-seed:{e.Id}"),
                "twilio-inbound"   => AttributionTriple.MarkAt(e.OccurredAt, $"twilio-inbound:{e.Id}"),
                "rss"              => AttributionTriple.WorldAt(e.OccurredAt, $"rss:{e.Id}"),
                "weather"          => AttributionTriple.WorldAt(e.OccurredAt, $"weather:{e.Id}"),
                "time"             => AttributionTriple.WorldAt(e.OccurredAt, $"time:{e.Id}"),
                "contact-state"    => AttributionTriple.WorldAt(e.OccurredAt, $"contact-state:{e.Id}"),
                _                  => AttributionTriple.UnknownHistorical(),
            };
        }

        // Episodic tier — content-prefix inference
        // MemoryPrefixes.FormatOutreach produces "I reached out to Mark: '...'"
        // Conversation records use "Mark said: '...'" prefix.
        if (e.Provenance == EpistemicTier.Episodic && !string.IsNullOrEmpty(e.Content))
        {
            if (e.Content.StartsWith("Mark said:", StringComparison.OrdinalIgnoreCase))
                return AttributionTriple.MarkAt(e.OccurredAt, "episodic-prefix-inferred");
            if (e.Content.StartsWith("I reached out to", StringComparison.OrdinalIgnoreCase))
                return AttributionTriple.AniAt(e.OccurredAt);
            // Other Episodic shapes (system-generated conversation summaries,
            // multi-turn blocks, etc.) — land on the manual-curation tail.
            return AttributionTriple.UnknownHistorical();
        }

        // Interior tier — Ani-authored record, but internal content
        // claims (e.g. embedded "you said X" prose from pre-F-2 cycles)
        // cannot be retroactively verified. This is the 12:04-shape
        // corruption class from the 2026-08-20 substrate-feedback finding.
        if (e.Provenance == EpistemicTier.Interior)
            return AttributionTriple.AniUnverifiedHistorical();

        // Anything else — fallback to Unknown for manual review.
        return AttributionTriple.UnknownHistorical();
    }

    /// <summary>
    /// Execute the backfill runner. Loads all records with
    /// <c>attributed_to = Unknown</c>, applies <see cref="InferAttribution"/>,
    /// writes the result if not <paramref name="isDryRun"/>. Returns a
    /// summary bag suitable for JSON-serialization at the CLI boundary.
    /// </summary>
    public static async Task<BackfillSummary> RunAsync(
        IDbContextFactory<AniDbContext> ctxFactory,
        bool                            isDryRun,
        string                          order,   // "oldest" | "newest"
        int                             limit,   // 0 = all
        Action<string>                  logProgress,
        CancellationToken               ct = default)
    {
        // Idempotency discriminator (PR #127 review-fix — Devin 🔍):
        // combined guard "AttributionTrust == 'unverified' AND AttributedTo ==
        // Unknown" catches only the schema-default state (never touched by
        // any producer or prior backfill). The trust-only guard would have
        // clobbered future P6 records with legitimate 'unverified' trust —
        // 'unverified' is a documented valid trust for inferred attributions,
        // not exclusively the schema default.
        //
        // Case matrix:
        //   pre-F-2 record (never touched)           → both defaults → match
        //   backfill Ani/Mark result                 → trust=verified → skip
        //   backfill Unknown fallback                → trust=unverified-historical → skip
        //   P6 producer (Ani, verified)              → attributed_to!=0 → skip
        //   P6 producer (Ani, unverified inference)  → attributed_to!=0 → skip
        //   P6 producer (Unknown, unverified)        → both defaults → match
        //                                              (correctly re-runs — genuinely unattributed)
        //
        // Projection (PR #127 review-fix — Devin 📝 perf): load only the
        // fields InferAttribution needs. Skipping the embedding blob
        // (~3KB/record × 25k+ records = ~75MB) keeps the working set small.
        List<MemoryEntity> targets;
        await using (var loadCtx = await ctxFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            var baseQ = loadCtx.Memories
                .Where(m => m.AttributionTrust == "unverified"
                         && m.AttributedTo == AttributedTo.Unknown)
                .Select(m => new MemoryEntity
                {
                    Id         = m.Id,
                    Type       = m.Type,
                    Content    = m.Content,
                    Provenance = m.Provenance,
                    SourceName = m.SourceName,
                    OccurredAt = m.OccurredAt,
                    // Embedding intentionally omitted — heuristic doesn't need it.
                });
            var ordered = string.Equals(order, "newest", StringComparison.OrdinalIgnoreCase)
                ? (IQueryable<MemoryEntity>)baseQ.OrderByDescending(m => m.OccurredAt)
                : baseQ.OrderBy(m => m.OccurredAt);
            targets = limit > 0
                ? await ordered.Take(limit).ToListAsync(ct).ConfigureAwait(false)
                : await ordered.ToListAsync(ct).ConfigureAwait(false);
        }

        logProgress($"BACKFILL_ATTRIBUTION loaded {targets.Count} candidate records (mode={(isDryRun ? "dry-run" : "commit")})");

        var sw            = System.Diagnostics.Stopwatch.StartNew();
        var perAuthor     = new Dictionary<AttributedTo, int>();
        var perTrust      = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var writtenCount  = 0;
        var processed     = 0;

        foreach (var entity in targets)
        {
            processed++;
            if (processed % 500 == 0)
            {
                var pct     = 100.0 * processed / targets.Count;
                var elapsed = sw.Elapsed.TotalSeconds;
                var eta     = elapsed / processed * (targets.Count - processed);
                logProgress(
                    $"BACKFILL_ATTRIBUTION progress {processed}/{targets.Count} ({pct:F1}%) " +
                    $"elapsed={elapsed:F0}s eta={eta:F0}s written={writtenCount}");
            }

            var triple = InferAttribution(entity);
            perAuthor[triple.AttributedTo] = perAuthor.TryGetValue(triple.AttributedTo, out var a) ? a + 1 : 1;
            perTrust[triple.Trust]         = perTrust.TryGetValue(triple.Trust, out var t) ? t + 1 : 1;

            if (!isDryRun)
            {
                await using var updCtx = await ctxFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
                await using var cmd = updCtx.Database.GetDbConnection().CreateCommand();
                if (cmd.Connection!.State != System.Data.ConnectionState.Open)
                    await cmd.Connection.OpenAsync(ct).ConfigureAwait(false);
                // Idempotent guard: WHERE attributed_to = 0 (Unknown) so a
                // second run over records already attributed by any means
                // (this backfill's earlier pass OR future P6 producer
                // wiring) is a no-op.
                // PR #127 review-fix (Devin BUG 🟡): guard mirrors the loader's
                // combined "AttributionTrust='unverified' AND AttributedTo=0"
                // discriminator. The prior comment claiming attributed_to=0
                // was the guard was stale from the mid-fix rewrite and
                // would have misled a maintainer into reintroducing the
                // Unknown-cycle bug the trust guard was added to prevent.
                cmd.CommandText = @"
                    UPDATE memories
                    SET attributed_to = @to,
                        attributed_at = @at,
                        attributed_source_id = @src_id,
                        attributed_source_desc = @src_desc,
                        attribution_trust = @trust
                    WHERE id = @id
                      AND attribution_trust = 'unverified'
                      AND attributed_to = 0";
                AddParam(cmd, "@to",       (int)triple.AttributedTo);
                AddParam(cmd, "@at",       triple.AttributedAt.HasValue ? triple.AttributedAt.Value.ToString("o") : (object)DBNull.Value);
                AddParam(cmd, "@src_id",   triple.SourceRecordId.HasValue ? triple.SourceRecordId.Value.ToString() : (object)DBNull.Value);
                AddParam(cmd, "@src_desc", (object?)triple.SourceDescriptor ?? DBNull.Value);
                AddParam(cmd, "@trust",    triple.Trust);
                AddParam(cmd, "@id",       entity.Id.ToString());
                var updated = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                if (updated > 0) writtenCount++;
            }
        }

        return new BackfillSummary(
            Mode:           isDryRun ? "dry-run" : "commit",
            Loaded:         targets.Count,
            Processed:      processed,
            Written:        writtenCount,
            ElapsedSeconds: sw.Elapsed.TotalSeconds,
            PerAuthor:      perAuthor.OrderByDescending(kv => kv.Value)
                                     .ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            PerTrust:       perTrust.OrderByDescending(kv => kv.Value)
                                    .ToDictionary(kv => kv.Key, kv => kv.Value));
    }

    private static void AddParam(System.Data.Common.DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value         = value;
        cmd.Parameters.Add(p);
    }
}

/// <summary>Summary bag returned from <see cref="AttributionBackfill.RunAsync"/>.</summary>
public sealed record BackfillSummary(
    string                     Mode,
    int                        Loaded,
    int                        Processed,
    int                        Written,
    double                     ElapsedSeconds,
    Dictionary<string, int>    PerAuthor,
    Dictionary<string, int>    PerTrust);
