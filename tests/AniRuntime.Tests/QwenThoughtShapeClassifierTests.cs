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
/// Foundation Input (F-1) Phase 3 (2026-08-18) — pins
/// <see cref="QwenThoughtShapeClassifier"/> JSON parsing + normalization
/// behavior without hitting a live Ollama endpoint. Actual LLM
/// classification quality is measured empirically against the production
/// corpus (see 2026-08-18 inner-thought shape-scan diagnostic).
/// </summary>
public class QwenThoughtShapeClassifierTests
{
    private static QwenThoughtShapeClassifier BuildClassifier(string ollamaJsonResponse)
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
        return new QwenThoughtShapeClassifier(
            mockOllama.Object, options, NullLogger<QwenThoughtShapeClassifier>.Instance);
    }

    [Theory]
    [InlineData("coherent-thought",   ThoughtShape.CoherentThought)]
    [InlineData("third-person-frame", ThoughtShape.ThirdPersonFrame)]
    [InlineData("fact-catalog",       ThoughtShape.FactCatalog)]
    [InlineData("mumble-loop",        ThoughtShape.MumbleLoop)]
    [InlineData("unclassified",       ThoughtShape.Unclassified)]
    public async Task ClassifyAsync_CanonicalShapes_PassThrough(string modelOutput, ThoughtShape expected)
    {
        var classifier = BuildClassifier($"{{ \"shape\": \"{modelOutput}\" }}");
        var result = await classifier.ClassifyAsync("some thought", CancellationToken.None);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("coherent",             ThoughtShape.CoherentThought)]
    [InlineData("third-person",         ThoughtShape.ThirdPersonFrame)]
    [InlineData("third-person-report",  ThoughtShape.ThirdPersonFrame)]
    [InlineData("fact-list",            ThoughtShape.FactCatalog)]
    [InlineData("prompt-echo",          ThoughtShape.FactCatalog)]
    [InlineData("enumeration",          ThoughtShape.FactCatalog)]
    [InlineData("loop",                 ThoughtShape.MumbleLoop)]
    [InlineData("self-repetition",      ThoughtShape.MumbleLoop)]
    public async Task ClassifyAsync_Aliases_NormalizeToCanonical(string modelOutput, ThoughtShape expected)
    {
        var classifier = BuildClassifier($"{{ \"shape\": \"{modelOutput}\" }}");
        var result = await classifier.ClassifyAsync("some thought", CancellationToken.None);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("COHERENT-THOUGHT",     ThoughtShape.CoherentThought)]
    [InlineData("  Fact-Catalog  ",     ThoughtShape.FactCatalog)]
    [InlineData("third_person_frame",   ThoughtShape.ThirdPersonFrame)]  // underscore variant
    [InlineData("mumble loop",          ThoughtShape.MumbleLoop)]         // space variant
    public async Task ClassifyAsync_CaseAndSeparatorInsensitive(string modelOutput, ThoughtShape expected)
    {
        var classifier = BuildClassifier($"{{ \"shape\": \"{modelOutput}\" }}");
        var result = await classifier.ClassifyAsync("some thought", CancellationToken.None);
        result.Should().Be(expected);
    }

    [Fact]
    public async Task ClassifyAsync_UnknownShape_ReturnsUnclassified()
    {
        var classifier = BuildClassifier("{ \"shape\": \"vibes\" }");
        var result = await classifier.ClassifyAsync("some thought", CancellationToken.None);
        result.Should().Be(ThoughtShape.Unclassified);
    }

    [Fact]
    public async Task ClassifyAsync_MalformedJson_ReturnsUnclassified()
    {
        var classifier = BuildClassifier("{ this is not json");
        var result = await classifier.ClassifyAsync("some thought", CancellationToken.None);
        result.Should().Be(ThoughtShape.Unclassified);
    }

    [Fact]
    public async Task ClassifyAsync_MissingShapeField_ReturnsUnclassified()
    {
        var classifier = BuildClassifier("{ \"something_else\": \"value\" }");
        var result = await classifier.ClassifyAsync("some thought", CancellationToken.None);
        result.Should().Be(ThoughtShape.Unclassified);
    }

    [Fact]
    public async Task ClassifyAsync_EmptyContent_ReturnsUnclassifiedWithoutCallingModel()
    {
        var mockOllama = new Mock<IOllamaClient>(MockBehavior.Strict);  // strict: any call fails the test
        var options = Options.Create(new AniOptions());
        var classifier = new QwenThoughtShapeClassifier(
            mockOllama.Object, options, NullLogger<QwenThoughtShapeClassifier>.Instance);

        var result = await classifier.ClassifyAsync("   ", CancellationToken.None);

        result.Should().Be(ThoughtShape.Unclassified);
        mockOllama.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ClassifyAsync_TransportFailure_ReturnsUnclassified()
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
            .ThrowsAsync(new HttpRequestException("Ollama unreachable"));
        var classifier = new QwenThoughtShapeClassifier(
            mockOllama.Object, Options.Create(new AniOptions()),
            NullLogger<QwenThoughtShapeClassifier>.Instance);

        var result = await classifier.ClassifyAsync("some thought", CancellationToken.None);
        result.Should().Be(ThoughtShape.Unclassified);
    }

    [Fact]
    public async Task ClassifyAsync_Cancellation_Propagates()
    {
        // Cancellation MUST propagate — service shutdown / request timeout
        // has to surface, not be swallowed as Unclassified.
        var mockOllama = new Mock<IOllamaClient>();
        mockOllama
            .Setup(o => o.ChatJsonWithModelAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<string?>()))
            .ThrowsAsync(new OperationCanceledException());
        var classifier = new QwenThoughtShapeClassifier(
            mockOllama.Object, Options.Create(new AniOptions()),
            NullLogger<QwenThoughtShapeClassifier>.Instance);

        var act = () => classifier.ClassifyAsync("some thought", CancellationToken.None);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
