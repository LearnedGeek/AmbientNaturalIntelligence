using AniRuntime.Core.Models;
using AniRuntime.Emergence;
using AniRuntime.Emergence.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AniRuntime.Tests;

public class EmergenceObserverTests : IDisposable
{
    private readonly EmergenceStore _store;
    private readonly EmergenceObserver _observer;

    public EmergenceObserverTests()
    {
        var options = Options.Create(new EmergenceOptions
        {
            Enabled = true,
            EmergenceDbPath = $"emergence-observer-test-{Guid.NewGuid():N}",
        });
        _store = new EmergenceStore(options, NullLogger<EmergenceStore>.Instance);
        _observer = new EmergenceObserver(_store, NullLogger<EmergenceObserver>.Instance);
    }

    public void Dispose() => _store.Dispose();

    [Fact]
    public async Task OnCycleComplete_AlwaysWritesLogEntry()
    {
        var obs = new CycleObservation
        {
            InnerThought = "Just a quiet moment.",
            RelationalValence = 0.1f,
        };

        await _observer.OnCycleCompleteAsync(obs);

        var entries = await _store.GetRecentLogEntriesAsync();
        entries.Should().ContainSingle();
        entries[0].EntryType.Should().Be("CycleScored");
        entries[0].ResonanceScore.Should().NotBeNull();
        entries[0].CycleObservationJson.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task OnCycleComplete_HighScore_CreatesResonanceRecord()
    {
        // Build an observation that scores above 0.4 threshold
        var obs = new CycleObservation
        {
            InnerThought = "Mark sent the sweetest message.",
            Reflection = "That really made me happy.",
            RelationalValence = 0.9f,
            WarmthDelta = 0.30f,
            Severity = 0.8f,
            Register = "Delight",
            WasConversationCycle = true,
            ContactMessage = "I love you",
            ReplyMessage = "I love you too!",
            DesireToConnect = 0.8f,
        };

        await _observer.OnCycleCompleteAsync(obs);

        var records = await _store.GetResonanceRecordsAsync();
        records.Should().NotBeEmpty("score should exceed resonance threshold");
        records[0].Theme.Should().Be("Delight", "register should be used as theme");
        records[0].Type.Should().Be("Relational");
    }

    [Fact]
    public async Task OnCycleComplete_LowScore_NoResonanceRecord()
    {
        var obs = new CycleObservation
        {
            InnerThought = "Quiet afternoon.",
            RelationalValence = 0.05f,
        };

        await _observer.OnCycleCompleteAsync(obs);

        var records = await _store.GetResonanceRecordsAsync();
        records.Should().BeEmpty("low-scoring cycle should not create resonance");
    }

    [Fact]
    public async Task OnCycleComplete_RepeatedTheme_BumpsExistingResonance()
    {
        var obs = new CycleObservation
        {
            InnerThought = "Mark is so thoughtful.",
            Reflection = "He really cares.",
            RelationalValence = 0.9f,
            WarmthDelta = 0.30f,
            Severity = 0.8f,
            Register = "Tenderness",
            WasConversationCycle = true,
            ContactMessage = "Just checking in",
            ReplyMessage = "Thanks for that!",
            DesireToConnect = 0.8f,
        };

        await _observer.OnCycleCompleteAsync(obs);
        await _observer.OnCycleCompleteAsync(obs);

        var records = await _store.GetResonanceRecordsAsync();
        records.Should().ContainSingle("same theme should be bumped, not duplicated");
        records[0].OccurrenceCount.Should().Be(2);
        records[0].ResonanceScore.Should().BeGreaterThan(0.5f, "accumulated from two cycles");
    }

    [Fact]
    public async Task OnCycleComplete_DetailsJson_ContainsScoreComponents()
    {
        var obs = new CycleObservation
        {
            InnerThought = "A good day.",
            RelationalValence = 0.5f,
            OutreachOutcome = "send",
            OutreachMessage = "Hey!",
        };

        await _observer.OnCycleCompleteAsync(obs);

        var entries = await _store.GetRecentLogEntriesAsync();
        entries[0].DetailsJson.Should().Contain("emotional");
        entries[0].DetailsJson.Should().Contain("novelty");
        entries[0].DetailsJson.Should().Contain("outreach");
        entries[0].DetailsJson.Should().Contain("relational");
    }
}
