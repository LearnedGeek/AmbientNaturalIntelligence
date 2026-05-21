using AniRuntime.Voice.TagMapping;
using FluentAssertions;
using LearnedGeek.ML.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace AniRuntime.Tests;

/// <summary>
/// 2026-05-20 — relocated back to ANI from <c>learnedgeek-libs/LearnedGeek.ML.Tests</c>
/// following the resolution of Issue #2 (move tag-mapping subsystem back to ANI).
/// Tests behavior identical to pre-move; namespace updated to consume the
/// AniRuntime.Voice.TagMapping implementation.
/// </summary>
public class TagMappingServiceTests
{
    private readonly TagMappingService _sut;

    public TagMappingServiceTests()
    {
        // StaticTagMap.json is copied into the test's bin output via
        // AniRuntime.Voice.csproj's <None Update> directive. Letting
        // TagMappingService auto-resolve via AppContext.BaseDirectory keeps
        // this test stable.
        _sut = new TagMappingService(NullLogger<TagMappingService>.Instance);
    }

    [Fact]
    public void Resolve_HappyMorning_ReturnsBrightMorning()
    {
        var emotion = MakeEmotion("happiness", 0.85f);
        var morning = new DateTimeOffset(2026, 3, 30, 8, 0, 0, TimeSpan.Zero);

        var result = _sut.Resolve(emotion, morning);

        result.Should().NotBeNull();
        result!.Tag.Should().Be("[bright morning]");
        result.Source.Should().Be("static");
    }

    [Fact]
    public void Resolve_HappyEvening_ReturnsEveningPlayful()
    {
        var emotion = MakeEmotion("happiness", 0.80f);
        var evening = new DateTimeOffset(2026, 3, 30, 19, 0, 0, TimeSpan.Zero);

        var result = _sut.Resolve(emotion, evening);

        result.Should().NotBeNull();
        result!.Tag.Should().Be("[evening playful]");
    }

    [Fact]
    public void Resolve_HappyAfternoon_ReturnsSocialAfternoon()
    {
        var emotion = MakeEmotion("happiness", 0.75f);
        var afternoon = new DateTimeOffset(2026, 3, 30, 14, 0, 0, TimeSpan.Zero);

        var result = _sut.Resolve(emotion, afternoon);

        result.Should().NotBeNull();
        result!.Tag.Should().Be("[social afternoon]");
    }

    [Fact]
    public void Resolve_SadnessHighConfidence_ReturnsHeartbroken()
    {
        var emotion = MakeEmotion("sadness", 0.80f);

        var result = _sut.Resolve(emotion);

        result.Should().NotBeNull();
        result!.Tag.Should().Be("[heartbroken]");
    }

    [Fact]
    public void Resolve_SadnessLowConfidence_ReturnsWistful()
    {
        var emotion = MakeEmotion("sadness", 0.50f);
        var morning = new DateTimeOffset(2026, 3, 30, 10, 0, 0, TimeSpan.Zero);

        var result = _sut.Resolve(emotion, morning);

        result.Should().NotBeNull();
        result!.Tag.Should().Be("[wistful]");
    }

    [Fact]
    public void Resolve_AngerHighConfidence_ReturnsFurious()
    {
        var emotion = MakeEmotion("anger", 0.85f);

        var result = _sut.Resolve(emotion);

        result.Should().NotBeNull();
        result!.Tag.Should().Be("[furious]");
    }

    [Fact]
    public void Resolve_AngerLowConfidence_ReturnsFrustrated()
    {
        var emotion = MakeEmotion("anger", 0.50f);

        var result = _sut.Resolve(emotion);

        result.Should().NotBeNull();
        result!.Tag.Should().Be("[frustrated]");
    }

    [Fact]
    public void Resolve_Sarcasm_HighestPriority()
    {
        var emotion = MakeEmotion("sarcasm", 0.70f);

        var result = _sut.Resolve(emotion);

        result.Should().NotBeNull();
        result!.Tag.Should().Be("[sarcastic]");
    }

    [Fact]
    public void Resolve_LoveHighConfidence_ReturnsAdoring()
    {
        var emotion = MakeEmotion("love", 0.80f);

        var result = _sut.Resolve(emotion);

        result.Should().NotBeNull();
        result!.Tag.Should().Be("[adoring]");
    }

    [Fact]
    public void Resolve_LoveLateNight_ReturnsLateNightNostalgic()
    {
        var emotion = MakeEmotion("love", 0.55f);
        var lateNight = new DateTimeOffset(2026, 3, 30, 23, 30, 0, TimeSpan.Zero);

        var result = _sut.Resolve(emotion, lateNight);

        result.Should().NotBeNull();
        result!.Tag.Should().Be("[late night nostalgic]");
    }

    [Fact]
    public void Resolve_NeutralMorning_ReturnsCalmMorning()
    {
        var emotion = MakeEmotion("neutral", 0.70f);
        var morning = new DateTimeOffset(2026, 3, 30, 9, 0, 0, TimeSpan.Zero);

        var result = _sut.Resolve(emotion, morning);

        result.Should().NotBeNull();
        result!.Tag.Should().Be("[calm morning]");
    }

    [Fact]
    public void Resolve_NeutralEvening_ReturnsEveningRelaxed()
    {
        var emotion = MakeEmotion("neutral", 0.65f);
        var evening = new DateTimeOffset(2026, 3, 30, 20, 0, 0, TimeSpan.Zero);

        var result = _sut.Resolve(emotion, evening);

        result.Should().NotBeNull();
        result!.Tag.Should().Be("[evening relaxed]");
    }

    [Fact]
    public void Resolve_NeutralLateNight_ReturnsNightSerene()
    {
        var emotion = MakeEmotion("neutral", 0.60f);
        var lateNight = new DateTimeOffset(2026, 3, 31, 1, 0, 0, TimeSpan.Zero);

        var result = _sut.Resolve(emotion, lateNight);

        result.Should().NotBeNull();
        result!.Tag.Should().Be("[night serene]");
    }

    [Fact]
    public void Resolve_Fear_ReturnsAnxious()
    {
        var emotion = MakeEmotion("fear", 0.60f);

        var result = _sut.Resolve(emotion);

        result.Should().NotBeNull();
        result!.Tag.Should().Be("[anxious]");
    }

    [Fact]
    public void Resolve_Amusement_ReturnsAmused()
    {
        var emotion = MakeEmotion("amusement", 0.70f);

        var result = _sut.Resolve(emotion);

        result.Should().NotBeNull();
        result!.Tag.Should().Be("[amused]");
    }

    [Fact]
    public void Resolve_UnknownEmotion_ReturnsNull()
    {
        var emotion = MakeEmotion("confused", 0.90f);

        var result = _sut.Resolve(emotion);

        result.Should().BeNull();
    }

    [Fact]
    public void Resolve_BelowMinConfidence_ReturnsNull()
    {
        var emotion = MakeEmotion("anger", 0.20f);

        var result = _sut.Resolve(emotion);

        result.Should().BeNull();
    }

    [Fact]
    public void Resolve_SecondaryEmotionMatch_MatchesFromScores()
    {
        var emotion = new EmotionResult(
            "neutral", 0.40f,
            new Dictionary<string, float>
            {
                ["neutral"] = 0.40f,
                ["sarcasm"] = 0.60f,
            });

        var result = _sut.Resolve(emotion);

        result.Should().NotBeNull();
        result!.Tag.Should().Be("[sarcastic]");
    }

    [Fact]
    public void Resolve_OvernightHourRange_WrapsCorrectly()
    {
        var emotion = MakeEmotion("happiness", 0.80f);
        var twoAm = new DateTimeOffset(2026, 3, 31, 2, 0, 0, TimeSpan.Zero);

        var result = _sut.Resolve(emotion, twoAm);

        result.Should().NotBeNull();
        result!.Tag.Should().Be("[late night excited]");
    }

    [Fact]
    public void RecordFeedback_DoesNotThrow()
    {
        var act = () => _sut.RecordFeedback("[cheerful]", "happiness", true);
        act.Should().NotThrow();
    }

    [Fact]
    public void Resolve_Curiosity_ReturnsCurious()
    {
        var emotion = MakeEmotion("curiosity", 0.65f);

        var result = _sut.Resolve(emotion);

        result.Should().NotBeNull();
        result!.Tag.Should().Be("[curious]");
    }

    private static EmotionResult MakeEmotion(string primary, float confidence)
    {
        return new EmotionResult(primary, confidence,
            new Dictionary<string, float> { [primary] = confidence });
    }
}
