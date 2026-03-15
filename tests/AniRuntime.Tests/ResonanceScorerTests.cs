using AniRuntime.Core.Models;
using AniRuntime.Emergence;
using FluentAssertions;

namespace AniRuntime.Tests;

public class ResonanceScorerTests
{
    [Fact]
    public void Score_AmbientCycle_ReturnsLowScore()
    {
        var obs = new CycleObservation
        {
            InnerThought = "The sun is nice today.",
            RelationalValence = 0.1f,
        };

        var score = ResonanceScorer.Score(obs);

        score.Should().BeInRange(0f, 0.3f, "ambient cycle with no emotional shift or outreach");
    }

    [Fact]
    public void Score_HighEmotionalShift_ScoresHigher()
    {
        var obs = new CycleObservation
        {
            InnerThought = "Mark said something really sweet.",
            WarmthDelta = 0.30f,
            RelationalValence = 0.8f,
            Severity = 0.8f,
        };

        var score = ResonanceScorer.Score(obs);

        score.Should().BeGreaterThan(0.3f, "large warmth delta should boost emotional magnitude");
    }

    [Fact]
    public void Score_OutreachSend_ScoresHigh()
    {
        var obs = new CycleObservation
        {
            InnerThought = "I want to reach out.",
            OutreachOutcome = "send",
            OutreachMessage = "Hey, thinking of you!",
            DesireToConnect = 0.8f,
            DesireThresholdCrossed = true,
            RelationalValence = 0.5f,
        };

        var score = ResonanceScorer.Score(obs);

        score.Should().BeGreaterThan(0.4f, "outreach send = 1.0 outreach quality + desire signal");
    }

    [Fact]
    public void Score_ConversationReply_ScoresModerately()
    {
        var obs = new CycleObservation
        {
            WasConversationCycle = true,
            ContactMessage = "How are you?",
            ReplyMessage = "I'm doing great!",
            InnerThought = "Mark reached out.",
            RelationalValence = 0.6f,
        };

        var score = ResonanceScorer.Score(obs);

        score.Should().BeGreaterThan(0.3f, "conversation cycle with reply has relational + outreach signal");
    }

    [Fact]
    public void Score_SilenceChoice_ScoresLow()
    {
        var obs = new CycleObservation
        {
            InnerThought = "I'll let the silence be.",
            ChoseSilence = true,
            RelationalValence = 0.2f,
        };

        var score = ResonanceScorer.Score(obs);

        score.Should().BeInRange(0.05f, 0.4f, "silence is meaningful but low-key");
    }

    [Fact]
    public void Score_EmptyCycle_ReturnsZero()
    {
        var obs = new CycleObservation();

        var score = ResonanceScorer.Score(obs);

        score.Should().Be(0f, "empty cycle has no signals");
    }

    [Fact]
    public void Score_IsClamped_Between0And1()
    {
        // Max out all signals
        var obs = new CycleObservation
        {
            InnerThought = "Everything is happening at once.",
            Reflection = "This is significant.",
            RelationalValence = 1.0f,
            WarmthDelta = 0.5f,
            EnergyDelta = 0.5f,
            WorryDelta = 0.5f,
            PlayfulnessDelta = 0.5f,
            Severity = 1.0f,
            OutreachOutcome = "send",
            OutreachMessage = "Hey!",
            DesireToConnect = 1.0f,
            DesireThresholdCrossed = true,
            WasConversationCycle = true,
            ContactMessage = "Hi",
            ReplyMessage = "Hello!",
        };

        var score = ResonanceScorer.Score(obs);

        score.Should().BeInRange(0f, 1f);
    }

    [Fact]
    public void EmotionalMagnitude_UsesMaxAbsDelta()
    {
        var obs = new CycleObservation
        {
            WarmthDelta = 0.1f,
            EnergyDelta = -0.25f,
            WorryDelta = 0.05f,
            PlayfulnessDelta = 0.0f,
        };

        var magnitude = ResonanceScorer.EmotionalMagnitude(obs);

        // Max abs delta = 0.25, normalized by 0.35 = ~0.714
        magnitude.Should().BeApproximately(0.25f / 0.35f, 0.01f);
    }

    [Fact]
    public void NoveltySignal_ReturnsZero_WhenNoInnerThought()
    {
        var obs = new CycleObservation { InnerThought = null };

        ResonanceScorer.NoveltySignal(obs).Should().Be(0f);
    }

    [Fact]
    public void OutreachQuality_SuppressedDesire_ReturnsPointFour()
    {
        var obs = new CycleObservation
        {
            DesireThresholdCrossed = true,
            OutreachOutcome = "suppress-coherence",
        };

        ResonanceScorer.OutreachQuality(obs).Should().Be(0.4f);
    }
}
