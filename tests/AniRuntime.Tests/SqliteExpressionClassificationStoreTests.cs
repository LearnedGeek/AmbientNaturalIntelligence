using AniRuntime.Core;
using AniRuntime.Core.Models;
using AniRuntime.Memory;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AniRuntime.Tests;

/// <summary>
/// Issue #41 spec tests for <see cref="SqliteExpressionClassificationStore"/>.
/// Pins schema + roundtrip + per-source-shape edge cases + idempotency on
/// the (source, content_hash) unique index. Real in-memory SQLite (no
/// mocks) per the store-layer testing convention.
/// </summary>
public class SqliteExpressionClassificationStoreTests : IDisposable
{
    private readonly SqliteExpressionClassificationStore _store;

    public SqliteExpressionClassificationStoreTests()
    {
        var dbName  = $"ani-expr-class-test-{Guid.NewGuid():N}";
        var options = Options.Create(new AniOptions { MemoryDbPath = dbName });
        _store = new SqliteExpressionClassificationStore(
            options, NullLogger<SqliteExpressionClassificationStore>.Instance);
    }

    public void Dispose() => _store.Dispose();

    private static ExpressionClassificationRecord RuntimeRecord(
        Guid?  threadId   = null,
        long?  messageId  = null,
        string role       = "mark",
        string feltReg    = "Tenderness",
        string exprEmo    = "Joy",
        float  confidence = 0.82f,
        string content    = "hi honey how was your day") => new()
    {
        Source           = "runtime",
        ThreadId         = threadId  ?? Guid.NewGuid(),
        MessageId        = messageId ?? 42,
        Role             = role,
        ContentHash      = ContentHashOf(content),
        FeltRegister     = feltReg,
        ExpressedEmotion = exprEmo,
        Confidence       = confidence,
        ClassifiedAt     = DateTimeOffset.UtcNow,
    };

    private static ExpressionClassificationRecord BatchRecord(
        string sourceFile = "duck-norris-arc.jsonl",
        int    sourceIdx  = 0,
        string role       = "user",
        string exprEmo    = "Joy",
        string content    = "you absolute legend") => new()
    {
        Source           = "batch_dashboard",
        SourceFile       = sourceFile,
        SourceIndex      = sourceIdx,
        Role             = role,
        ContentHash      = ContentHashOf(content),
        ExpressedEmotion = exprEmo,
        ClassifiedAt     = DateTimeOffset.UtcNow,
    };

    private static string ContentHashOf(string text)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    [Fact]
    public async Task NewStore_HasEmptySchema()
    {
        var rows = await _store.GetRecentAsync(10);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveAsync_RuntimeRecord_RoundtripsAllFields()
    {
        var threadId  = Guid.NewGuid();
        var original  = RuntimeRecord(threadId: threadId, messageId: 99, role: "ani",
                                       feltReg: "Playfulness", exprEmo: "Joy", confidence: 0.91f);

        await _store.SaveAsync(original);
        var loaded = (await _store.GetRecentAsync(10)).Single();

        loaded.Source.Should().Be("runtime");
        loaded.ThreadId.Should().Be(threadId);
        loaded.MessageId.Should().Be(99);
        loaded.SourceFile.Should().BeNull("runtime source has no file anchor");
        loaded.SourceIndex.Should().BeNull();
        loaded.Role.Should().Be("ani");
        loaded.ContentHash.Should().Be(original.ContentHash);
        loaded.FeltRegister.Should().Be("Playfulness");
        loaded.ExpressedEmotion.Should().Be("Joy");
        loaded.Confidence.Should().BeApproximately(0.91f, 0.0001f);
        loaded.ClassifiedAt.Should().BeCloseTo(original.ClassifiedAt, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task SaveAsync_BatchRecord_RoundtripsAllFields()
    {
        var original = BatchRecord(sourceFile: "test.jsonl", sourceIdx: 5,
                                    role: "response", exprEmo: "Anger",
                                    content: "i'm furious about this");

        await _store.SaveAsync(original);
        var loaded = (await _store.GetRecentAsync(10)).Single();

        loaded.Source.Should().Be("batch_dashboard");
        loaded.ThreadId.Should().BeNull("batch source has no FK to ANI threads");
        loaded.MessageId.Should().BeNull();
        loaded.SourceFile.Should().Be("test.jsonl");
        loaded.SourceIndex.Should().Be(5);
        loaded.Role.Should().Be("response");
        loaded.ExpressedEmotion.Should().Be("Anger");
        loaded.FeltRegister.Should().BeNull("batch path may skip 9-register classification");
        loaded.Confidence.Should().BeNull();
    }

    /// <summary>
    /// SPEC: re-saving the same (source, content_hash) pair is a no-op.
    /// Lets the batch classifier safely re-process a corpus without
    /// inflating counts.
    /// </summary>
    [Fact]
    public async Task SaveAsync_DuplicateContentHash_IsNoOp()
    {
        var first  = BatchRecord(content: "same content");
        var second = BatchRecord(content: "same content");
        second.ExpressedEmotion = "Sadness";  // would differ if it weren't a no-op

        await _store.SaveAsync(first);
        await _store.SaveAsync(second);

        var rows = (await _store.GetRecentAsync(10)).ToList();
        rows.Should().HaveCount(1, "dedup on (source, content_hash) prevents double-counting");
        rows[0].ExpressedEmotion.Should().Be("Joy", "first write wins; ON CONFLICT DO NOTHING");
    }

    /// <summary>
    /// SPEC: dedup is scoped to (source, content_hash). The SAME content
    /// classified by both runtime and batch should produce TWO rows.
    /// </summary>
    [Fact]
    public async Task SaveAsync_SameContentDifferentSource_BothPersist()
    {
        var content   = "hey honey honey";
        var runtime   = RuntimeRecord(content: content);
        var batch     = BatchRecord(content: content);

        await _store.SaveAsync(runtime);
        await _store.SaveAsync(batch);

        var rows = (await _store.GetRecentAsync(10)).ToList();
        rows.Should().HaveCount(2);
        rows.Select(r => r.Source).Should().BeEquivalentTo(new[] { "runtime", "batch_dashboard" });
    }

    [Fact]
    public async Task GetRecentAsync_SourceFilter_ReturnsOnlyMatching()
    {
        await _store.SaveAsync(RuntimeRecord(content: "a"));
        await _store.SaveAsync(RuntimeRecord(content: "b"));
        await _store.SaveAsync(BatchRecord(content: "c"));

        var runtimeOnly = (await _store.GetRecentAsync(10, source: "runtime")).ToList();
        var batchOnly   = (await _store.GetRecentAsync(10, source: "batch_dashboard")).ToList();

        runtimeOnly.Should().HaveCount(2);
        runtimeOnly.Should().OnlyContain(r => r.Source == "runtime");
        batchOnly.Should().HaveCount(1);
        batchOnly[0].Source.Should().Be("batch_dashboard");
    }

    [Fact]
    public async Task GetRecentAsync_OrdersByClassifiedAtDesc()
    {
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-30);
        var older = RuntimeRecord(content: "older");
        older.ClassifiedAt = t0;
        var newer = RuntimeRecord(content: "newer");
        newer.ClassifiedAt = t0.AddMinutes(20);

        await _store.SaveAsync(older);
        await _store.SaveAsync(newer);

        var rows = (await _store.GetRecentAsync(10)).ToList();
        rows[0].ContentHash.Should().Be(newer.ContentHash, "newer first");
        rows[1].ContentHash.Should().Be(older.ContentHash);
    }

    /// <summary>
    /// SPEC: per-thread retrieval (the C.4 mood-tracking panel data feed).
    /// Returns runtime-source rows for the requested thread, ordered
    /// chronologically — so the panel can render a left-to-right arc.
    /// </summary>
    [Fact]
    public async Task GetByThreadIdAsync_ReturnsThreadRowsChronologically()
    {
        var t  = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);
        var a  = RuntimeRecord(threadId: t, messageId: 1, role: "mark",   content: "msg1");  a.ClassifiedAt = t0;
        var b  = RuntimeRecord(threadId: t, messageId: 2, role: "ani",    content: "msg2");  b.ClassifiedAt = t0.AddMinutes(2);
        var c  = RuntimeRecord(threadId: t, messageId: 3, role: "mark",   content: "msg3");  c.ClassifiedAt = t0.AddMinutes(4);
        var other = RuntimeRecord(threadId: Guid.NewGuid(), content: "different thread");

        await _store.SaveAsync(a);
        await _store.SaveAsync(b);
        await _store.SaveAsync(c);
        await _store.SaveAsync(other);

        var rows = (await _store.GetByThreadIdAsync(t)).ToList();
        rows.Should().HaveCount(3, "only the requested thread");
        rows.Select(r => r.MessageId).Should().Equal(1L, 2L, 3L);
    }

    [Fact]
    public async Task SaveAsync_NullContentHash_PersistsWithoutDedup()
    {
        // Partial unique index excludes rows with null content_hash, so
        // two rows with null hashes coexist (they're audit-traceable via
        // source_file + source_index for batch, or message_id for runtime).
        var a = BatchRecord(content: "x");
        a.ContentHash = null;
        a.SourceIndex = 0;
        var b = BatchRecord(content: "y");
        b.ContentHash = null;
        b.SourceIndex = 1;

        await _store.SaveAsync(a);
        await _store.SaveAsync(b);

        (await _store.GetRecentAsync(10)).Should().HaveCount(2);
    }
}
