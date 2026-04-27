using AniRuntime.Core;
using AniRuntime.Core.Models;
using AniRuntime.Emergence;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AniRuntime.Tests;

public class Em9DetectorTests
{
    private static readonly DateTimeOffset NOW = new(2026, 04, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeProvider FixedTime = new FakeTimeProvider(NOW);

    private static MemoryRecord Memory(
        DateTimeOffset occurredAt,
        DecayTier tier = DecayTier.Standard,
        string? sourceName = null,
        string content = "test memory")
        => new()
        {
            Id          = Guid.NewGuid(),
            OccurredAt  = occurredAt,
            Content     = content,
            DecayTier   = tier,
            SourceName  = sourceName,
            Type        = MemoryType.Episodic,
        };

    private sealed class CapturingLogger : ILogger<Em9Detector>
    {
        public List<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state,
            Exception? ex, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, ex));
    }

    [Fact]
    public void Analyze_LogsAnchoredTierMemoryOlderThan90Days()
    {
        var log = new CapturingLogger();
        var detector = new Em9Detector(log, FixedTime);
        var oldAnchored = Memory(
            occurredAt: NOW.AddDays(-100),
            tier: DecayTier.Anchored,
            content: "Mark's daughter is named after his grandmother");

        detector.Analyze(Em9EmissionContext.Outreach, "thinking of you", new[] { oldAnchored });

        log.Messages.Should().HaveCount(1);
        log.Messages[0].Should().Contain("EM9_CANDIDATE");
        log.Messages[0].Should().Contain("path=anchored-tier");
        log.Messages[0].Should().Contain("emission=outreach");
        log.Messages[0].Should().MatchRegex(@"ageDays=\d{3}\.\d");
    }

    [Fact]
    public void Analyze_LogsReflectionDerivedMemoryOlderThan90Days()
    {
        var log = new CapturingLogger();
        var detector = new Em9Detector(log, FixedTime);
        var oldReflection = Memory(
            occurredAt: NOW.AddDays(-120),
            sourceName: SourceNames.ReflectionSynthesis,
            content: "He calls warmth what others would call patience");

        detector.Analyze(Em9EmissionContext.ConversationReply, "you've always been steady", new[] { oldReflection });

        log.Messages.Should().HaveCount(1);
        log.Messages[0].Should().Contain("path=reflection-derived");
        log.Messages[0].Should().Contain("emission=conversation-reply");
    }

    [Fact]
    public void Analyze_DoesNotLogCosineTopKMemoryEvenIfOld()
    {
        // A non-anchored, non-reflection memory older than 90 days surfaced
        // via cosine top-k retrieval is the "expected" path — explicitly NOT
        // logged. This is the conservative-detection discipline.
        var log = new CapturingLogger();
        var detector = new Em9Detector(log, FixedTime);
        var oldCosine = Memory(
            occurredAt: NOW.AddDays(-95),
            tier: DecayTier.Standard,
            sourceName: null,
            content: "Mark mentioned his commute");

        detector.Analyze(Em9EmissionContext.Outreach, "thinking of you", new[] { oldCosine });

        log.Messages.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_DoesNotLogYoungerMemoriesEvenIfArchitectural()
    {
        // The age threshold is 90 days. An anchored memory only 50 days old
        // doesn't qualify — it's still in the recency window where its
        // surfacing is more plausibly cosine-driven anyway.
        var log = new CapturingLogger();
        var detector = new Em9Detector(log, FixedTime);
        var youngAnchored = Memory(
            occurredAt: NOW.AddDays(-50),
            tier: DecayTier.Anchored);
        var youngReflection = Memory(
            occurredAt: NOW.AddDays(-30),
            sourceName: SourceNames.ReflectionSynthesis);

        detector.Analyze(Em9EmissionContext.Outreach, "hey", new[] { youngAnchored, youngReflection });

        log.Messages.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_LogsOneEntryPerQualifyingMemoryInPool()
    {
        var log = new CapturingLogger();
        var detector = new Em9Detector(log, FixedTime);
        var anchored1 = Memory(NOW.AddDays(-100), DecayTier.Anchored);
        var anchored2 = Memory(NOW.AddDays(-150), DecayTier.Anchored);
        var reflection = Memory(NOW.AddDays(-95), sourceName: SourceNames.ReflectionSynthesis);
        var youngCosine = Memory(NOW.AddDays(-10)); // ignored
        var oldCosine   = Memory(NOW.AddDays(-200)); // ignored — cosine path

        detector.Analyze(Em9EmissionContext.Outreach, "morning",
            new[] { anchored1, anchored2, reflection, youngCosine, oldCosine });

        log.Messages.Should().HaveCount(3); // two anchored + one reflection
        log.Messages.Should().AllSatisfy(m => m.Should().Contain("EM9_CANDIDATE"));
    }

    [Fact]
    public void Analyze_HandlesEmptyOrNullInputsGracefully()
    {
        var log = new CapturingLogger();
        var detector = new Em9Detector(log, FixedTime);

        detector.Analyze(Em9EmissionContext.Outreach, "", Array.Empty<MemoryRecord>());
        detector.Analyze(Em9EmissionContext.Outreach, "msg", null!);

        log.Messages.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_TruncatesLongMemoryAndOutputContent()
    {
        // Logging shouldn't include arbitrarily-long content. Memory previews
        // capped at 100 chars; output previews at 240. Both with ellipsis.
        var log = new CapturingLogger();
        var detector = new Em9Detector(log, FixedTime);
        var longContent = new string('x', 500);
        var longOutput  = new string('y', 800);
        var mem = Memory(NOW.AddDays(-100), DecayTier.Anchored, content: longContent);

        detector.Analyze(Em9EmissionContext.Outreach, longOutput, new[] { mem });

        log.Messages.Should().HaveCount(1);
        log.Messages[0].Should().Contain("xxx...");
        log.Messages[0].Should().Contain("yyy...");
        log.Messages[0].Length.Should().BeLessThan(800); // far under raw payload size
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FakeTimeProvider(DateTimeOffset now) { _now = now; }
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
