using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Options;
using Moq;
using AniRuntime.Core;

namespace AniRuntime.Tests.Infrastructure;

/// <summary>
/// Base class for all ANI Runtime tests.
/// Provides pre-built mocks and default options matching appsettings.json defaults.
/// </summary>
public abstract class AniTestBase
{
    protected readonly Mock<IMemoryService>  MockMemory  = new();
    protected readonly Mock<IOllamaClient>   MockOllama  = new();
    protected readonly Mock<IAniAction>      MockAction  = new();

    protected AniTestBase()
    {
        // Default emotional state mock — many tests need this since DesireEngine reads it
        MockMemory.Setup(m => m.GetEmotionalStateAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new EmotionalState());
    }

    protected IOptions<AniOptions> DefaultOptions => Options.Create(new AniOptions
    {
        DesireLambdaMinutes    = 8.0,
        ThinkTargetProbability = 0.70,
        MinWakeMinutes         = 2.0,
        MaxWakeMinutes         = 45.0,
        CooldownMinutes        = 20.0,
        MinOutreachGapMinutes  = 60.0,
        MaxOutreachPerDay      = 4,
        // Disable night/morning gates in unit tests — these are time-dependent
        // and cause false failures when tests run after 10pm.
        MaxNightOutreach       = 100,
    });

    protected IOptions<OllamaOptions> DefaultOllamaOptions => Options.Create(new OllamaOptions());

    protected static DesireState FreshDesireState() => new()
    {
        DesireToConnect  = 0.0f,
        CooldownActive   = false,
        LastContactInbound  = DateTimeOffset.UtcNow,
        LastInnerThought = DateTimeOffset.UtcNow,
        CircadianModifier = 1.0f,
    };

    protected static DesireState HighDesireState() => new()
    {
        DesireToConnect   = 0.9f,
        CooldownActive    = false,
        LastContactInbound   = DateTimeOffset.UtcNow.AddHours(-8),
        LastInnerThought  = DateTimeOffset.UtcNow.AddMinutes(-30),
        CircadianModifier = 1.0f,
    };
}
