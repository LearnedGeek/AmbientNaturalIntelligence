using AniRuntime.Voice.TagMapping;
using FluentAssertions;
using LearnedGeek.ML.Interfaces;
using LearnedGeek.ML.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AniRuntime.Tests;

/// <summary>
/// 2026-05-20 — relocated back to ANI from <c>learnedgeek-libs/LearnedGeek.ML.Tests</c>
/// following the resolution of Issue #2 (move tag-mapping subsystem back to ANI).
/// Tests behavior identical to pre-move; namespace updated to consume the
/// AniRuntime.Voice.TagMapping implementation. ITextClassificationService stays
/// in the library since it's genuinely cross-project.
/// </summary>
public class MLVoiceTagEnricherTests
{
    private readonly Mock<ITextClassificationService> _classifier = new();
    private readonly Mock<ITagMappingService> _tagMapping = new();
    private readonly MLVoiceTagEnricher _sut;

    public MLVoiceTagEnricherTests()
    {
        _sut = new MLVoiceTagEnricher(
            _classifier.Object,
            _tagMapping.Object,
            NullLogger<MLVoiceTagEnricher>.Instance);
    }

    [Fact]
    public async Task EnrichAsync_EmptyText_ReturnsUnchanged()
    {
        var result = await _sut.EnrichAsync("  ");
        result.Should().Be("  ");
    }

    [Fact]
    public async Task EnrichAsync_AlreadyTagged_ReturnsUnchanged()
    {
        var result = await _sut.EnrichAsync("[cheerful] Hello there!");
        result.Should().Be("[cheerful] Hello there!");
        _classifier.Verify(c => c.ClassifyEmotionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EnrichAsync_HappyEmotion_PrependsTag()
    {
        var emotion = new EmotionResult("happiness", 0.85f,
            new Dictionary<string, float> { ["happiness"] = 0.85f });
        var sarcasm = new SarcasmResult(false, 0.10f);
        var resolution = new TagResolution("[cheerful]", "static", "happiness", 0.85f);

        _classifier.Setup(c => c.ClassifyEmotionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(emotion);
        _classifier.Setup(c => c.DetectSarcasmAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(sarcasm);
        _tagMapping.Setup(t => t.Resolve(emotion, null))
            .Returns(resolution);

        var result = await _sut.EnrichAsync("I had a great day!");

        result.Should().Be("[cheerful] I had a great day!");
    }

    [Fact]
    public async Task EnrichAsync_SarcasmOverridesEmotion()
    {
        var emotion = new EmotionResult("happiness", 0.70f,
            new Dictionary<string, float> { ["happiness"] = 0.70f });
        var sarcasm = new SarcasmResult(true, 0.80f);
        var sarcasmResolution = new TagResolution("[sarcastic]", "static", "sarcasm", 0.80f);

        _classifier.Setup(c => c.ClassifyEmotionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(emotion);
        _classifier.Setup(c => c.DetectSarcasmAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(sarcasm);
        _tagMapping.Setup(t => t.Resolve(It.Is<EmotionResult>(e => e.PrimaryEmotion == "sarcasm"), null))
            .Returns(sarcasmResolution);

        var result = await _sut.EnrichAsync("Oh sure, that sounds great.");

        result.Should().Be("[sarcastic] Oh sure, that sounds great.");
    }

    [Fact]
    public async Task EnrichAsync_LowConfidenceSarcasm_DoesNotOverride()
    {
        var emotion = new EmotionResult("happiness", 0.80f,
            new Dictionary<string, float> { ["happiness"] = 0.80f });
        var sarcasm = new SarcasmResult(true, 0.30f);
        var resolution = new TagResolution("[cheerful]", "static", "happiness", 0.80f);

        _classifier.Setup(c => c.ClassifyEmotionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(emotion);
        _classifier.Setup(c => c.DetectSarcasmAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(sarcasm);
        _tagMapping.Setup(t => t.Resolve(emotion, null))
            .Returns(resolution);

        var result = await _sut.EnrichAsync("That's wonderful!");

        result.Should().Be("[cheerful] That's wonderful!");
    }

    [Fact]
    public async Task EnrichAsync_NoResolution_ReturnsUntagged()
    {
        var emotion = new EmotionResult("confused", 0.40f,
            new Dictionary<string, float> { ["confused"] = 0.40f });
        var sarcasm = new SarcasmResult(false, 0.05f);

        _classifier.Setup(c => c.ClassifyEmotionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(emotion);
        _classifier.Setup(c => c.DetectSarcasmAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(sarcasm);
        _tagMapping.Setup(t => t.Resolve(It.IsAny<EmotionResult>(), null))
            .Returns((TagResolution?)null);

        var result = await _sut.EnrichAsync("Hmm, I'm not sure about that.");

        result.Should().Be("Hmm, I'm not sure about that.");
    }

    [Fact]
    public async Task EnrichAsync_ClassifierThrows_ReturnsUntagged()
    {
        _classifier.Setup(c => c.ClassifyEmotionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Model not loaded"));
        _classifier.Setup(c => c.DetectSarcasmAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Model not loaded"));

        var result = await _sut.EnrichAsync("Hello world");

        result.Should().Be("Hello world");
    }

    [Fact]
    public async Task EnrichAsync_ParenthesisTag_ReturnsUnchanged()
    {
        var result = await _sut.EnrichAsync("(whispers) Don't tell anyone");
        result.Should().Be("(whispers) Don't tell anyone");
    }

    [Fact]
    public void RecordFeedback_DelegatestoTagMapping()
    {
        var resolution = new TagResolution("[cheerful]", "static", "happiness", 0.85f);

        _sut.RecordFeedback(resolution, true);

        _tagMapping.Verify(t => t.RecordFeedback("[cheerful]", "happiness", true), Times.Once);
    }
}
