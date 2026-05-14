using System.Diagnostics.CodeAnalysis;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace AniRuntime.Tests.Regression;

/// <summary>
/// Base class for regression-class scenarios per the Test Harness Plan
/// (`docs/spec/ANI-Test-Harness-Plan.md`) and Failure Class Registry
/// (`docs/spec/ANI-Failure-Class-Registry.md`).
///
/// Distinct from <see cref="Infrastructure.AniTestBase"/> in two ways:
///   1. Higher fidelity by default — uses real <see cref="SqliteConversationService"/>
///      against a per-test in-memory SQLite instance (unique GUID-based name) so the
///      data-layer round-trip is exercised, not mocked. Strict mocks for collaborators
///      that don't need to be real for the scenario under test.
///   2. Scenarios describe SPEC behavior (what should happen), not current behavior.
///      A failing scenario means the failure class is OPEN — the spec is not met. The
///      fix is in production code (deferred per harness-first directive); the test
///      is the spec, and stays.
///
/// TDD discipline (per Mark's 2026-05-13 directive): write tests to meet requirements,
/// not tests to satisfy the code. If current code can't pass the test, that's the
/// confirmation of an open failure class, not a reason to soften the assertion.
/// </summary>
[SuppressMessage("Performance", "CA1822", Justification = "Helper methods may be overridden by scenarios.")]
public abstract class RegressionScenarioBase : IDisposable
{
    protected readonly Mock<IMemoryService>                _memory       = new(MockBehavior.Strict);
    protected readonly Mock<IClosedConversationSummarizer> _summarizer   = new(MockBehavior.Strict);
    protected readonly Mock<IClosedConversationStore>      _closedStore  = new(MockBehavior.Strict);
    protected readonly SqliteConversationService           _conversations;

    protected RegressionScenarioBase()
    {
        // Unique in-memory DB per scenario instance — no cross-scenario contamination.
        // Bare name (no separator/extension) triggers the `Mode=Memory;Cache=Shared`
        // branch in SqliteConversationService at line 47-52.
        var dbName = $"ani-regression-{Guid.NewGuid():N}";
        var options = Options.Create(new AniOptions { MemoryDbPath = dbName });
        _conversations = new SqliteConversationService(
            options,
            _memory.Object,
            _summarizer.Object,
            _closedStore.Object,
            NullLogger<SqliteConversationService>.Instance);
    }

    public virtual void Dispose() => _conversations.Dispose();

    /// <summary>
    /// Open a fresh active conversation thread initiated by the named role.
    /// Helper for scenarios that need a thread to write into.
    /// </summary>
    protected async Task<ConversationThread> OpenActiveThreadAsync(string initiatedBy = Roles.Mark)
    {
        var thread = new ConversationThread
        {
            InitiatedBy   = initiatedBy,
            StartedAt     = DateTimeOffset.UtcNow,
            LastMessageAt = DateTimeOffset.UtcNow,
        };
        await _conversations.SaveThreadAsync(thread);
        return thread;
    }

    /// <summary>
    /// Append a message to the thread. Wraps the same call signature
    /// <see cref="OutreachPhase.RecordOutboundInThreadAsync"/> uses
    /// when recording proactive outreaches and reactive shares
    /// (<c>OutreachPhase.cs:121</c>), so scenarios that exercise the
    /// continuity invariant use the same data path.
    /// </summary>
    protected Task AppendMessageAsync(
        Guid threadId,
        string role,
        string content,
        DateTimeOffset? sentAt = null)
        => _conversations.AddMessageAsync(threadId, new ConversationMessage
        {
            Role    = role,
            Content = content,
            SentAt  = sentAt ?? DateTimeOffset.UtcNow,
        });
}
