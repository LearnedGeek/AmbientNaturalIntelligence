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
/// F-4 Phase 7 — pins the parse surface + fail-open contract of
/// <see cref="QwenArcExtractor"/>. Classification quality is a 30-day
/// post-deploy question (per #186 acceptance).
/// </summary>
public class QwenArcExtractorTests
{
    private static QwenArcExtractor BuildExtractor(
        string ollamaJsonResponse,
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
        return new QwenArcExtractor(
            mock.Object,
            Options.Create(new AniOptions { ArcExtractorModel = "qwen3:14b" }),
            NullLogger<QwenArcExtractor>.Instance);
    }

    private static IReadOnlyList<MemoryRecord> Thoughts(params string[] contents)
        => contents.Select(c => new MemoryRecord
        {
            Content = c, Type = MemoryType.InnerThought,
        }).ToList();

    [Fact]
    public async Task Extract_EmptyThoughts_ReturnsEmpty()
    {
        var e = BuildExtractor("{\"arcs\":[{\"name\":\"shelf\",\"summary\":\"almost done\"}]}");
        var result = await e.ExtractAsync(
            Array.Empty<MemoryRecord>(), Array.Empty<string>(), CancellationToken.None);
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task Extract_WellFormedResponse_ReturnsMentions()
    {
        var e = BuildExtractor(
            "{\"arcs\":[{\"name\":\"shelf\",\"summary\":\"almost done\",\"existing_match\":null,\"is_completion\":false}]}");
        var result = await e.ExtractAsync(Thoughts("building the shelf"), Array.Empty<string>(), CancellationToken.None);
        result.Should().HaveCount(1);
        result[0].Name.Should().Be("shelf");
        result[0].Summary.Should().Be("almost done");
        result[0].ExistingMatch.Should().BeNull();
        result[0].IsCompletion.Should().BeFalse();
    }

    [Fact]
    public async Task Extract_ExistingMatchPopulated_PassesThrough()
    {
        var e = BuildExtractor(
            "{\"arcs\":[{\"name\":\"the shelf\",\"summary\":\"done tonight\",\"existing_match\":\"shelf\",\"is_completion\":true}]}");
        var result = await e.ExtractAsync(
            Thoughts("finally finished the shelf"),
            new[] { "shelf" },
            CancellationToken.None);
        result.Should().ContainSingle();
        result[0].ExistingMatch.Should().Be("shelf");
        result[0].IsCompletion.Should().BeTrue();
    }

    [Fact]
    public async Task Extract_TransportFailure_ReturnsEmpty()
    {
        var e = BuildExtractor(string.Empty, throwOnCall: true);
        var result = await e.ExtractAsync(Thoughts("x"), Array.Empty<string>(), CancellationToken.None);
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task Extract_MalformedJson_ReturnsEmpty()
    {
        var e = BuildExtractor("not json");
        var result = await e.ExtractAsync(Thoughts("x"), Array.Empty<string>(), CancellationToken.None);
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task Extract_MissingArcsField_ReturnsEmpty()
    {
        var e = BuildExtractor("{\"other\":[]}");
        var result = await e.ExtractAsync(Thoughts("x"), Array.Empty<string>(), CancellationToken.None);
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task Extract_EmptyNameEntry_Skipped()
    {
        var e = BuildExtractor(
            "{\"arcs\":[{\"name\":\"\",\"summary\":\"nothing\"},{\"name\":\"shelf\",\"summary\":\"ok\"}]}");
        var result = await e.ExtractAsync(Thoughts("x"), Array.Empty<string>(), CancellationToken.None);
        result.Should().ContainSingle();
        result[0].Name.Should().Be("shelf");
    }

    [Fact]
    public async Task Extract_CapsAt5Entries()
    {
        var items = string.Join(",", Enumerable.Range(0, 10)
            .Select(i => $"{{\"name\":\"arc{i}\",\"summary\":\"s\"}}"));
        var e = BuildExtractor($"{{\"arcs\":[{items}]}}");
        var result = await e.ExtractAsync(Thoughts("x"), Array.Empty<string>(), CancellationToken.None);
        result.Should().HaveCount(5);
    }
}
