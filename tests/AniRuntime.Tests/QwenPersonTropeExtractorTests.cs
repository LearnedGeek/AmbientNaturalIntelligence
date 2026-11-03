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
/// F-4 Phase 6 — pins the parse surface + fail-open contract of
/// <see cref="QwenPersonTropeExtractor"/>. Confabulation discipline
/// (canonical-cast allowlist, tropes-not-events) is enforced caller-side
/// in <c>ReflectionPhase.ApplyPersonTropes</c>; see its companion tests.
/// See issue #185.
/// </summary>
public class QwenPersonTropeExtractorTests
{
    private static QwenPersonTropeExtractor BuildExtractor(
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
        return new QwenPersonTropeExtractor(
            mock.Object,
            Options.Create(new AniOptions { KnownPeopleExtractorModel = "qwen3:14b" }),
            NullLogger<QwenPersonTropeExtractor>.Instance);
    }

    private static IReadOnlyList<MemoryRecord> Thoughts(params string[] contents)
        => contents.Select(c => new MemoryRecord
        {
            Content = c, Type = MemoryType.InnerThought,
        }).ToList();

    private static IReadOnlyList<string> Cast(params string[] entries) => entries;

    [Fact]
    public async Task Extract_EmptyThoughts_ReturnsEmpty()
    {
        var e = BuildExtractor("{\"people\":[{\"name\":\"Lila\",\"trope\":\"caring\"}]}");
        var result = await e.ExtractAsync(
            Array.Empty<MemoryRecord>(), Cast("Lila"), "Mark",
            Array.Empty<string>(), CancellationToken.None);
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task Extract_EmptyCanonicalCast_ReturnsEmpty()
    {
        // Guard: without a cast, there's nothing an extractor could ground
        // against — skip the LLM hop entirely.
        var e = BuildExtractor("{\"people\":[{\"name\":\"Lila\",\"trope\":\"caring\"}]}");
        var result = await e.ExtractAsync(
            Thoughts("thinking about Lila"), Array.Empty<string>(), "Mark",
            Array.Empty<string>(), CancellationToken.None);
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task Extract_WellFormedResponse_ReturnsTropes()
    {
        var e = BuildExtractor(
            "{\"people\":[{\"name\":\"Lila\",\"trope\":\"classroom patience\"}]}");
        var result = await e.ExtractAsync(
            Thoughts("Lila's class ran long again"),
            Cast("Sister Lila — my code-sis", "Dominus — my dog"),
            "Mark",
            Array.Empty<string>(),
            CancellationToken.None);
        result.Should().HaveCount(1);
        result[0].Name.Should().Be("Lila");
        result[0].Trope.Should().Be("classroom patience");
    }

    [Fact]
    public async Task Extract_TransportFailure_ReturnsEmpty()
    {
        var e = BuildExtractor(string.Empty, throwOnCall: true);
        var result = await e.ExtractAsync(
            Thoughts("x"), Cast("Lila"), "Mark",
            Array.Empty<string>(), CancellationToken.None);
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task Extract_MalformedJson_ReturnsEmpty()
    {
        var e = BuildExtractor("not json");
        var result = await e.ExtractAsync(
            Thoughts("x"), Cast("Lila"), "Mark",
            Array.Empty<string>(), CancellationToken.None);
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task Extract_MissingPeopleField_ReturnsEmpty()
    {
        var e = BuildExtractor("{\"other\":[]}");
        var result = await e.ExtractAsync(
            Thoughts("x"), Cast("Lila"), "Mark",
            Array.Empty<string>(), CancellationToken.None);
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task Extract_NonObjectJsonRoot_ReturnsEmpty()
    {
        // PR #189 Copilot: valid JSON with a non-object root ("[]", "null",
        // "\"foo\"", "42") makes TryGetProperty throw InvalidOperationException
        // which the JsonException catch wouldn't handle. Guard the root kind.
        foreach (var payload in new[] { "[]", "null", "\"foo\"", "42" })
        {
            var e = BuildExtractor(payload);
            var result = await e.ExtractAsync(
                Thoughts("x"), Cast("Lila"), "Mark",
                Array.Empty<string>(), CancellationToken.None);
            result.Should().BeEmpty($"non-object root '{payload}' must fail-open to empty");
        }
    }

    [Fact]
    public async Task Extract_EmptyNameOrTrope_Skipped()
    {
        var e = BuildExtractor(
            "{\"people\":[{\"name\":\"\",\"trope\":\"nothing\"},{\"name\":\"Lila\",\"trope\":\"\"},{\"name\":\"Dominus\",\"trope\":\"loud\"}]}");
        var result = await e.ExtractAsync(
            Thoughts("x"), Cast("Lila", "Dominus"), "Mark",
            Array.Empty<string>(), CancellationToken.None);
        result.Should().ContainSingle();
        result[0].Name.Should().Be("Dominus");
    }

    [Fact]
    public async Task Extract_CapsAt10Entries()
    {
        // Confabulation discipline: even if the LLM over-emits, the parse
        // caps per call. Caller-side allowlist filter also runs.
        var items = string.Join(",", Enumerable.Range(0, 20)
            .Select(i => $"{{\"name\":\"P{i}\",\"trope\":\"t\"}}"));
        var e = BuildExtractor($"{{\"people\":[{items}]}}");
        var result = await e.ExtractAsync(
            Thoughts("x"), Cast("Lila"), "Mark",
            Array.Empty<string>(), CancellationToken.None);
        result.Should().HaveCount(10);
    }

    [Fact]
    public async Task Extract_CancellationFlowsThrough()
    {
        // CLAUDE.md §4: fail-open must not swallow cancellation.
        using var cts = new CancellationTokenSource();
        var mock = new Mock<IOllamaClient>();
        mock.Setup(o => o.ChatJsonWithModelAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .ThrowsAsync(new OperationCanceledException());
        var e = new QwenPersonTropeExtractor(
            mock.Object,
            Options.Create(new AniOptions()),
            NullLogger<QwenPersonTropeExtractor>.Instance);

        Func<Task> act = () => e.ExtractAsync(
            Thoughts("x"), Cast("Lila"), "Mark",
            Array.Empty<string>(), cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
