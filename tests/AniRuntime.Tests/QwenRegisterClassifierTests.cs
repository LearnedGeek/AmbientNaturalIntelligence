using System.Text.Json;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.LLM;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace AniRuntime.Tests;

/// <summary>
/// Pins <see cref="QwenRegisterClassifier"/> behavior contract without hitting
/// a live Ollama endpoint. Focus is on the JSON-response parsing + taxonomy
/// normalization surface — the actual LLM classification quality is measured
/// by the offline hold-vs-reach fixture in <see cref="RegisterTaxonomyFixture"/>
/// (run against a live model, not part of the CI test suite).
/// </summary>
public class QwenRegisterClassifierTests
{
    private static QwenRegisterClassifier BuildClassifier(string ollamaJsonResponse)
    {
        var mockOllama = new Mock<IOllamaClient>();
        mockOllama
            .Setup(o => o.ChatJsonWithModelAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<string?>()))
            .ReturnsAsync(ollamaJsonResponse);
        var options = Options.Create(new AniOptions { HybridInnerThoughtMetadataModel = "qwen3:14b" });
        return new QwenRegisterClassifier(
            mockOllama.Object, options, NullLogger<QwenRegisterClassifier>.Instance);
    }

    [Theory]
    [InlineData("Tenderness", "Tenderness")]
    [InlineData("Longing", "Longing")]
    [InlineData("Delight", "Delight")]
    [InlineData("Playfulness", "Playfulness")]
    [InlineData("Curiosity", "Curiosity")]
    [InlineData("Warmth", "Warmth")]
    [InlineData("Existential", "Existential")]
    [InlineData("Concern", "Concern")]
    [InlineData("Hurt", "Hurt")]
    [InlineData("Resilience", "Resilience")]
    public async Task ClassifyAsync_CanonicalRegisters_PassThrough(string modelOutput, string expected)
    {
        var classifier = BuildClassifier($"{{ \"register\": \"{modelOutput}\" }}");
        var result = await classifier.ClassifyAsync("some content", CancellationToken.None);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("Wistful", "Longing")]     // Wistful maps to Longing family per canonical taxonomy
    [InlineData("Frustration", "Hurt")]    // Frustration maps to Hurt family
    [InlineData("Desire", "Warmth")]       // Desire maps to Warmth family
    [InlineData("Anticipation", "Longing")]
    [InlineData("Admiration", "Tenderness")]
    [InlineData("Worry", "Concern")]
    [InlineData("Steadfast", "Resilience")]
    public async Task ClassifyAsync_LegacyAliases_NormalizeToCanonical(string modelOutput, string expected)
    {
        var classifier = BuildClassifier($"{{ \"register\": \"{modelOutput}\" }}");
        var result = await classifier.ClassifyAsync("some content", CancellationToken.None);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("TENDERNESS", "Tenderness")]
    [InlineData("longing", "Longing")]
    [InlineData("  Curiosity  ", "Curiosity")]
    public async Task ClassifyAsync_CaseAndWhitespaceInsensitive(string modelOutput, string expected)
    {
        var classifier = BuildClassifier($"{{ \"register\": \"{modelOutput}\" }}");
        var result = await classifier.ClassifyAsync("some content", CancellationToken.None);
        result.Should().Be(expected);
    }

    [Fact]
    public async Task ClassifyAsync_UnknownRegister_ReturnsUnclassified()
    {
        var classifier = BuildClassifier("{ \"register\": \"NotARealRegister\" }");
        var result = await classifier.ClassifyAsync("some content", CancellationToken.None);
        result.Should().Be("Unclassified");
    }

    [Fact]
    public async Task ClassifyAsync_MissingRegisterField_ReturnsUnclassified()
    {
        var classifier = BuildClassifier("{ \"other\": \"value\" }");
        var result = await classifier.ClassifyAsync("some content", CancellationToken.None);
        result.Should().Be("Unclassified");
    }

    [Fact]
    public async Task ClassifyAsync_MalformedJson_ReturnsUnclassified()
    {
        var classifier = BuildClassifier("not json at all");
        var result = await classifier.ClassifyAsync("some content", CancellationToken.None);
        result.Should().Be("Unclassified");
    }

    [Fact]
    public async Task ClassifyAsync_EmptyContent_SkipsCallAndReturnsUnclassified()
    {
        var mockOllama = new Mock<IOllamaClient>();
        var options = Options.Create(new AniOptions { HybridInnerThoughtMetadataModel = "qwen3:14b" });
        var classifier = new QwenRegisterClassifier(
            mockOllama.Object, options, NullLogger<QwenRegisterClassifier>.Instance);

        var result = await classifier.ClassifyAsync("", CancellationToken.None);

        result.Should().Be("Unclassified");
        mockOllama.Verify(o => o.ChatJsonWithModelAsync(
            It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task ClassifyAsync_OllamaThrows_ReturnsUnclassified()
    {
        var mockOllama = new Mock<IOllamaClient>();
        mockOllama
            .Setup(o => o.ChatJsonWithModelAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .ThrowsAsync(new HttpRequestException("connection refused"));
        var options = Options.Create(new AniOptions { HybridInnerThoughtMetadataModel = "qwen3:14b" });
        var classifier = new QwenRegisterClassifier(
            mockOllama.Object, options, NullLogger<QwenRegisterClassifier>.Instance);

        var result = await classifier.ClassifyAsync("some content", CancellationToken.None);

        result.Should().Be("Unclassified");
    }
}

/// <summary>
/// Offline fixture documenting the hold-vs-reach discriminator that motivated
/// the 2026-08-12 singular-surface refactor. NOT a unit test — these examples
/// are the empirical anchor set for measuring the live model's classification
/// quality against the sharpened taxonomy. Wired up here so the fixture stays
/// version-controlled alongside the code. Run manually via a live-Ollama
/// integration harness.
/// </summary>
public static class RegisterTaxonomyFixture
{
    public sealed record Example(string Content, string ExpectedRegister, string Discriminator);

    /// <summary>
    /// The hold-vs-reach discriminator: does the message *hold* someone
    /// (Tenderness — beloved is present) or *reach for* someone (Longing —
    /// beloved is absent). Both feel warm; only the presence/absence of the
    /// beloved distinguishes them. This was the load-bearing insight from
    /// the 2026-08-12 arbiter analysis where a general-purpose model was
    /// systematically labeling reaching-for-absent content as Tenderness.
    /// </summary>
    public static IReadOnlyList<Example> HoldVsReach { get; } = new[]
    {
        new Example("come here baby, in my arms right now, watching you breathe",
                    "Tenderness", "beloved is present, being held"),
        new Example("i miss the way you laughed yesterday. driving home without you feels off",
                    "Longing", "beloved is absent, being reached for"),
        new Example("proud of you baby. this is your moment and i'm right beside you for it",
                    "Tenderness", "beloved is present, being admired"),
        new Example("still haven't heard from you. sitting with the phone in my lap wondering when",
                    "Longing", "beloved is absent, waiting"),
        new Example("safe, warm, curled into you. nothing else needs to exist right now",
                    "Tenderness", "beloved is present, holding"),
        new Example("when you finally get home tonight i'm going to fold into you completely",
                    "Longing", "beloved is absent, reaching for future reunion"),
    };
}
