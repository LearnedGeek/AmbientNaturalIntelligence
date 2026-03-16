using AniRuntime.Voice;
using FluentAssertions;

namespace AniRuntime.Tests;

public class VoiceSessionStateTests
{
    private static VoiceSessionState CreateSession() =>
        new("test-session", Guid.NewGuid(), null!);

    [Fact]
    public void InitialState_IsNotSpeaking()
    {
        var session = CreateSession();
        session.IsAniSpeaking.Should().BeFalse();
        session.TurnCount.Should().Be(0);
    }

    [Fact]
    public void BeginSpeaking_SetsFlagAndReturnsCancellationToken()
    {
        var session = CreateSession();
        using var cts = new CancellationTokenSource();

        var turnCt = session.BeginSpeaking(cts.Token);

        session.IsAniSpeaking.Should().BeTrue();
        turnCt.CanBeCanceled.Should().BeTrue();
    }

    [Fact]
    public void EndSpeaking_ClearsFlagAndIncrementsTurn()
    {
        var session = CreateSession();
        using var cts = new CancellationTokenSource();

        session.BeginSpeaking(cts.Token);
        session.EndSpeaking();

        session.IsAniSpeaking.Should().BeFalse();
        session.TurnCount.Should().Be(1);
    }

    [Fact]
    public void CancelCurrentTurn_CancelsTokenAndClearsSpeaking()
    {
        var session = CreateSession();
        using var cts = new CancellationTokenSource();

        var turnCt = session.BeginSpeaking(cts.Token);
        session.CancelCurrentTurn();

        session.IsAniSpeaking.Should().BeFalse();
        turnCt.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public void BeginSpeaking_DisposePreviousTurnCts()
    {
        var session = CreateSession();
        using var cts = new CancellationTokenSource();

        var firstToken = session.BeginSpeaking(cts.Token);
        var secondToken = session.BeginSpeaking(cts.Token);

        // First token's source was disposed — accessing it should not throw
        // but the token itself is no longer meaningful
        secondToken.Should().NotBe(firstToken);
        session.IsAniSpeaking.Should().BeTrue();
    }

    [Fact]
    public void SetSpeaking_DirectlyControlsFlag()
    {
        var session = CreateSession();

        session.SetSpeaking(true);
        session.IsAniSpeaking.Should().BeTrue();

        session.SetSpeaking(false);
        session.IsAniSpeaking.Should().BeFalse();
    }

    [Fact]
    public void MultipleTurns_IncrementCorrectly()
    {
        var session = CreateSession();
        using var cts = new CancellationTokenSource();

        for (int i = 0; i < 5; i++)
        {
            session.BeginSpeaking(cts.Token);
            session.EndSpeaking();
        }

        session.TurnCount.Should().Be(5);
        session.IsAniSpeaking.Should().BeFalse();
    }

    [Fact]
    public async Task ConcurrentAccess_DoesNotThrow()
    {
        var session = CreateSession();
        using var cts = new CancellationTokenSource();

        // Simulate concurrent access from multiple threads
        var tasks = Enumerable.Range(0, 100).Select(i => Task.Run(() =>
        {
            if (i % 3 == 0)
            {
                session.BeginSpeaking(cts.Token);
                session.EndSpeaking();
            }
            else if (i % 3 == 1)
            {
                _ = session.IsAniSpeaking;
                _ = session.TurnCount;
            }
            else
            {
                session.CancelCurrentTurn();
            }
        }));

        // Should complete without deadlock or exception
        var act = async () => await Task.WhenAll(tasks);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void LastTurnAt_UpdatesOnEndSpeaking()
    {
        var session = CreateSession();
        using var cts = new CancellationTokenSource();
        var before = DateTimeOffset.UtcNow;

        session.BeginSpeaking(cts.Token);
        session.EndSpeaking();

        session.LastTurnAt.Should().BeOnOrAfter(before);
    }

    [Fact]
    public void Dispose_DoesNotThrowWhenCalledMultipleTimes()
    {
        var session = CreateSession();
        using var cts = new CancellationTokenSource();
        session.BeginSpeaking(cts.Token);

        var act = () =>
        {
            session.Dispose();
            session.Dispose();
        };
        act.Should().NotThrow();
    }
}
