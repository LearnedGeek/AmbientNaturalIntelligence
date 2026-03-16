using AniRuntime.Core;
using AniRuntime.Core.Models;
using AniRuntime.Voice;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AniRuntime.Tests;

public class ImageSelectionServiceTests
{
    private static readonly string TestDir = Path.Combine(Path.GetTempPath(), $"ani-image-test-{Guid.NewGuid():N}");

    private ImageSelectionService CreateService(
        int maxPerDay = 10,
        double probability = 1.0, // default to always for deterministic tests
        List<(string file, string[] tags)>? images = null)
    {
        Directory.CreateDirectory(TestDir);

        // Create manifest and image files
        var entries = new List<object>();
        if (images is not null)
        {
            foreach (var (file, tags) in images)
            {
                var filePath = Path.Combine(TestDir, file);
                File.WriteAllText(filePath, "fake image data");
                entries.Add(new { file, tags, description = "test" });
            }
        }

        var manifest = System.Text.Json.JsonSerializer.Serialize(new { images = entries },
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        File.WriteAllText(Path.Combine(TestDir, "manifest.json"), manifest);

        return new ImageSelectionService(
            Options.Create(new ImageOptions
            {
                Enabled = true,
                LibraryPath = TestDir,
                MaxImagesPerDay = maxPerDay,
                AttachmentProbability = probability,
            }),
            NullLogger<ImageSelectionService>.Instance);
    }

    private static EmotionalState DefaultState() => new();

    [Fact]
    public async Task SelectImageAsync_EmptyLibrary_ReturnsNull()
    {
        var service = CreateService();

        var result = await service.SelectImageAsync("hello coffee morning", DefaultState());

        result.Should().BeNull();
    }

    [Fact]
    public async Task SelectImageAsync_MatchingTags_ReturnsFilePath()
    {
        var service = CreateService(images: [("cozy.jpg", ["coffee", "morning", "cozy"])]);

        var result = await service.SelectImageAsync("good morning, coffee time!", DefaultState());

        result.Should().NotBeNull();
        result.Should().Contain("cozy.jpg");
    }

    [Fact]
    public async Task SelectImageAsync_NoMatchingTags_ReturnsNull()
    {
        var service = CreateService(images: [("snow.jpg", ["snow", "winter", "cold"])]);

        var result = await service.SelectImageAsync("let's go to the beach!", DefaultState());

        result.Should().BeNull();
    }

    [Fact]
    public async Task SelectImageAsync_DailyRateLimit_ReturnsNullAfterLimit()
    {
        var service = CreateService(maxPerDay: 2, images: [("test.jpg", ["hello", "hi"])]);

        var r1 = await service.SelectImageAsync("hello there", DefaultState());
        var r2 = await service.SelectImageAsync("hi again hello", DefaultState());
        var r3 = await service.SelectImageAsync("hello once more", DefaultState());

        r1.Should().NotBeNull("first image should succeed");
        r2.Should().NotBeNull("second image should succeed");
        r3.Should().BeNull("third image should be rate limited");
    }

    [Fact]
    public async Task SelectImageAsync_ProbabilityZero_AlwaysReturnsNull()
    {
        var service = CreateService(probability: 0.0, images: [("test.jpg", ["hello"])]);

        // Run multiple times — should always be null with 0% probability
        for (int i = 0; i < 10; i++)
        {
            var result = await service.SelectImageAsync("hello", DefaultState());
            result.Should().BeNull($"attempt {i} should be gate-blocked");
        }
    }

    [Fact]
    public async Task SelectImageAsync_BestMatchWins()
    {
        var service = CreateService(images:
        [
            ("generic.jpg", ["morning"]),
            ("perfect.jpg", ["coffee", "morning", "cozy"]),
        ]);

        // "coffee morning cozy" should prefer the image with 3 matching tags over 1
        var results = new List<string?>();
        for (int i = 0; i < 5; i++)
            results.Add(await service.SelectImageAsync("coffee morning cozy vibes", DefaultState()));

        results.Where(r => r is not null).Should().AllSatisfy(r =>
            r.Should().Contain("perfect.jpg"));
    }

    [Fact]
    public async Task SelectImageAsync_SkipsPlaceholder()
    {
        Directory.CreateDirectory(TestDir);
        var manifest = """{"images":[{"file":"placeholder.txt","tags":["test"],"description":"test"}]}""";
        File.WriteAllText(Path.Combine(TestDir, "manifest.json"), manifest);
        File.WriteAllText(Path.Combine(TestDir, "placeholder.txt"), "not an image");

        var service = new ImageSelectionService(
            Options.Create(new ImageOptions { LibraryPath = TestDir, AttachmentProbability = 1.0 }),
            NullLogger<ImageSelectionService>.Instance);

        var result = await service.SelectImageAsync("test", DefaultState());
        result.Should().BeNull();
    }
}
