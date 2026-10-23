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
/// F-4 Phase 4a.1b — pins the parse surface + fail-open contract of
/// <see cref="QwenSignificanceClassifier"/>. Classification quality itself
/// is an empirical question for the 30-day post-deploy audit (acceptance
/// criterion on #168).
/// </summary>
public class QwenSignificanceClassifierTests
{
    private static QwenSignificanceClassifier BuildClassifier(
        string ollamaJsonResponse,
        AniOptions? options = null,
        bool throwOnCall = false)
    {
        var mock = new Mock<IOllamaClient>();
        if (throwOnCall)
        {
            mock.Setup(o => o.ChatJsonWithModelAsync(
                    It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<string>(),
                    It.IsAny<CancellationToken>(), It.IsAny<string?>()))
                .ThrowsAsync(new HttpRequestException("simulated transport failure"));
        }
        else
        {
            mock.Setup(o => o.ChatJsonWithModelAsync(
                    It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<string>(),
                    It.IsAny<CancellationToken>(), It.IsAny<string?>()))
                .ReturnsAsync(ollamaJsonResponse);
        }

        var opts = Options.Create(options ?? new AniOptions
        {
            SignificanceClassifierEnabled = true,
            SignificanceClassifierModel   = "qwen3:14b",
            SignificanceDefault           = 0.5f,
        });

        return new QwenSignificanceClassifier(
            mock.Object, opts, NullLogger<QwenSignificanceClassifier>.Instance);
    }

    [Fact]
    public async Task ClassifyAsync_EmptyInput_ReturnsDefault()
    {
        var classifier = BuildClassifier("{\"significance\":0.9}");
        var result = await classifier.ClassifyAsync("", CancellationToken.None);
        result.Should().Be(0.5f);
    }

    [Fact]
    public async Task ClassifyAsync_WellFormedJson_ReturnsValue()
    {
        var classifier = BuildClassifier("{\"significance\":0.85}");
        var result = await classifier.ClassifyAsync("Ukraine war escalates", CancellationToken.None);
        result.Should().BeApproximately(0.85f, 0.001f);
    }

    [Fact]
    public async Task ClassifyAsync_LowSignificance_PassesThrough()
    {
        var classifier = BuildClassifier("{\"significance\":0.15}");
        var result = await classifier.ClassifyAsync("Celebrity gossip", CancellationToken.None);
        result.Should().BeApproximately(0.15f, 0.001f);
    }

    [Fact]
    public async Task ClassifyAsync_OutOfRangeHigh_ClampedToOne()
    {
        var classifier = BuildClassifier("{\"significance\":1.3}");
        var result = await classifier.ClassifyAsync("some event", CancellationToken.None);
        result.Should().Be(1.0f);
    }

    [Fact]
    public async Task ClassifyAsync_OutOfRangeLow_ClampedToZero()
    {
        var classifier = BuildClassifier("{\"significance\":-0.2}");
        var result = await classifier.ClassifyAsync("some event", CancellationToken.None);
        result.Should().Be(0.0f);
    }

    [Fact]
    public async Task ClassifyAsync_MalformedJson_FailsOpenToDefault()
    {
        var classifier = BuildClassifier("not json at all");
        var result = await classifier.ClassifyAsync("some event", CancellationToken.None);
        result.Should().Be(0.5f);
    }

    [Fact]
    public async Task ClassifyAsync_MissingSignificanceField_FailsOpenToDefault()
    {
        var classifier = BuildClassifier("{\"something_else\":0.7}");
        var result = await classifier.ClassifyAsync("some event", CancellationToken.None);
        result.Should().Be(0.5f);
    }

    [Fact]
    public async Task ClassifyAsync_WrongTypeOnSignificance_FailsOpenToDefault()
    {
        var classifier = BuildClassifier("{\"significance\":\"high\"}");
        var result = await classifier.ClassifyAsync("some event", CancellationToken.None);
        result.Should().Be(0.5f);
    }

    [Fact]
    public async Task ClassifyAsync_TransportFailure_FailsOpenToDefault()
    {
        var classifier = BuildClassifier(string.Empty, throwOnCall: true);
        var result = await classifier.ClassifyAsync("some event", CancellationToken.None);
        result.Should().Be(0.5f);
    }

    [Fact]
    public async Task ClassifyAsync_ConfigDefault_Honored()
    {
        var opts = new AniOptions
        {
            SignificanceClassifierEnabled = true,
            SignificanceDefault           = 0.3f,
        };
        var classifier = BuildClassifier("malformed", options: opts);
        var result = await classifier.ClassifyAsync("x", CancellationToken.None);
        result.Should().Be(0.3f);
    }

    [Fact]
    public async Task ClassifyAsync_CancellationPropagates()
    {
        var mock = new Mock<IOllamaClient>();
        mock.Setup(o => o.ChatJsonWithModelAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .ThrowsAsync(new OperationCanceledException());
        var opts = Options.Create(new AniOptions());
        var classifier = new QwenSignificanceClassifier(
            mock.Object, opts, NullLogger<QwenSignificanceClassifier>.Instance);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await FluentActions.Awaiting(() => classifier.ClassifyAsync("x", cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }
}
