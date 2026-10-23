using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Loops;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// F-4 Phase 7 — pins the pure state mutations
/// <see cref="ReflectionPhase.ApplyArcMentions"/> and
/// <see cref="ReflectionPhase.ApplyArcStallAndCap"/>. These are the
/// shape of the lifecycle discipline (update vs create, completion,
/// stall transition, soft cap) without any Ollama hop.
/// </summary>
public class OngoingArcsMutationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    // ── ApplyArcMentions ───────────────────────────────────────────────────

    [Fact]
    public void ApplyMentions_EmptyList_NoChange()
    {
        var cs = new CharacterStateDoc();
        ReflectionPhase.ApplyArcMentions(cs, Array.Empty<ArcMention>(), Now)
            .Should().BeFalse();
        cs.OngoingArcs.Should().BeEmpty();
    }

    [Fact]
    public void ApplyMentions_NewArc_Created()
    {
        var cs = new CharacterStateDoc();
        var mentions = new[] { new ArcMention("the shelf", "almost done", null, false) };
        var changed = ReflectionPhase.ApplyArcMentions(cs, mentions, Now);
        changed.Should().BeTrue();
        cs.OngoingArcs.Should().ContainSingle();
        cs.OngoingArcs[0].Name.Should().Be("the shelf");
        cs.OngoingArcs[0].Summary.Should().Be("almost done");
        cs.OngoingArcs[0].StartedAt.Should().Be(Now);
        cs.OngoingArcs[0].Status.Should().Be(ArcStatus.Active);
    }

    [Fact]
    public void ApplyMentions_ExistingMatch_UpdatesLastTouchedAndSummary()
    {
        var cs = new CharacterStateDoc();
        cs.OngoingArcs.Add(new ArcState
        {
            Name = "shelf", Summary = "started", StartedAt = Now.AddDays(-3),
            LastTouchedAt = Now.AddDays(-3),
        });
        var mentions = new[] { new ArcMention("the shelf", "halfway", "shelf", false) };

        var changed = ReflectionPhase.ApplyArcMentions(cs, mentions, Now);

        changed.Should().BeTrue();
        cs.OngoingArcs.Should().ContainSingle("existing match should update, not create duplicate");
        cs.OngoingArcs[0].Summary.Should().Be("halfway");
        cs.OngoingArcs[0].LastTouchedAt.Should().Be(Now);
    }

    [Fact]
    public void ApplyMentions_NameMatchCaseInsensitive_UpdatesExisting()
    {
        var cs = new CharacterStateDoc();
        cs.OngoingArcs.Add(new ArcState { Name = "Shelf", Summary = "started", LastTouchedAt = Now.AddDays(-2) });
        var mentions = new[] { new ArcMention("shelf", "progress", null, false) };
        var changed = ReflectionPhase.ApplyArcMentions(cs, mentions, Now);
        changed.Should().BeTrue();
        cs.OngoingArcs.Should().ContainSingle();
        cs.OngoingArcs[0].Summary.Should().Be("progress");
    }

    [Fact]
    public void ApplyMentions_CompletionOnExistingArc_StatusTransitions()
    {
        var cs = new CharacterStateDoc();
        cs.OngoingArcs.Add(new ArcState { Name = "shelf", Status = ArcStatus.Active });
        var mentions = new[] { new ArcMention("shelf", "finished tonight", null, true) };

        ReflectionPhase.ApplyArcMentions(cs, mentions, Now);

        cs.OngoingArcs[0].Status.Should().Be(ArcStatus.Completed);
    }

    [Fact]
    public void ApplyMentions_CompletionOnUnknownArc_NotCreated()
    {
        // A completion-framed mention of an arc we've never seen is likely
        // a hallucination — don't create a new slot.
        var cs = new CharacterStateDoc();
        var mentions = new[] { new ArcMention("never-mentioned", "done!", null, true) };
        ReflectionPhase.ApplyArcMentions(cs, mentions, Now);
        cs.OngoingArcs.Should().BeEmpty();
    }

    [Fact]
    public void ApplyMentions_StalledArcReferenced_ResurrectsToActive()
    {
        var cs = new CharacterStateDoc();
        cs.OngoingArcs.Add(new ArcState
        {
            Name = "cookbook", Status = ArcStatus.Stalled,
            LastTouchedAt = Now.AddDays(-30),
        });
        var mentions = new[] { new ArcMention("cookbook", "picking it back up", null, false) };

        ReflectionPhase.ApplyArcMentions(cs, mentions, Now);

        cs.OngoingArcs[0].Status.Should().Be(ArcStatus.Active);
    }

    [Fact]
    public void ApplyMentions_EmptyName_Skipped()
    {
        var cs = new CharacterStateDoc();
        var mentions = new[]
        {
            new ArcMention("", "junk", null, false),
            new ArcMention("real-arc", "ok", null, false),
        };
        ReflectionPhase.ApplyArcMentions(cs, mentions, Now);
        cs.OngoingArcs.Should().ContainSingle();
    }

    // ── ApplyArcStallAndCap ────────────────────────────────────────────────

    [Fact]
    public void StallAndCap_ActiveArcBeyondThreshold_Stalls()
    {
        var cs = new CharacterStateDoc();
        cs.OngoingArcs.Add(new ArcState
        {
            Name = "stale", Status = ArcStatus.Active,
            LastTouchedAt = Now.AddDays(-30),
        });
        var changed = ReflectionPhase.ApplyArcStallAndCap(
            cs, Now, TimeSpan.FromDays(28), maxActive: 10);
        changed.Should().BeTrue();
        cs.OngoingArcs[0].Status.Should().Be(ArcStatus.Stalled);
    }

    [Fact]
    public void StallAndCap_RecentArc_StaysActive()
    {
        var cs = new CharacterStateDoc();
        cs.OngoingArcs.Add(new ArcState
        {
            Name = "fresh", Status = ArcStatus.Active,
            LastTouchedAt = Now.AddDays(-3),
        });
        var changed = ReflectionPhase.ApplyArcStallAndCap(
            cs, Now, TimeSpan.FromDays(28), maxActive: 10);
        changed.Should().BeFalse();
        cs.OngoingArcs[0].Status.Should().Be(ArcStatus.Active);
    }

    [Fact]
    public void StallAndCap_SoftCap_OldestActiveStalls()
    {
        var cs = new CharacterStateDoc();
        for (var i = 0; i < 5; i++)
        {
            cs.OngoingArcs.Add(new ArcState
            {
                Name = $"arc{i}", Status = ArcStatus.Active,
                LastTouchedAt = Now.AddDays(-i),
            });
        }
        // maxActive=3 — oldest 2 should stall
        var changed = ReflectionPhase.ApplyArcStallAndCap(
            cs, Now, TimeSpan.FromDays(28), maxActive: 3);
        changed.Should().BeTrue();
        cs.OngoingArcs.Count(a => a.Status == ArcStatus.Active).Should().Be(3);
        // The oldest-touched (arc4 at -4 days, arc3 at -3 days) stall first
        cs.OngoingArcs.First(a => a.Name == "arc4").Status.Should().Be(ArcStatus.Stalled);
        cs.OngoingArcs.First(a => a.Name == "arc3").Status.Should().Be(ArcStatus.Stalled);
    }
}
