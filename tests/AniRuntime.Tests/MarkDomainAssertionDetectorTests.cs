using AniRuntime.Core.Models;
using FluentAssertions;
using System.Reflection;

namespace AniRuntime.Tests;

/// <summary>
/// Tests for the Mark-domain assertion detector (Apr 10, 2026).
///
/// The detector is a private static method in ConversationReplyPhase because it
/// belongs with the rest of the reply-phase confabulation infrastructure. These
/// tests use reflection to exercise the detector directly so we can verify its
/// fabrication-matching behavior without spinning up the full phase.
/// </summary>
public class MarkDomainAssertionDetectorTests
{
    private static List<string> Detect(string reply, List<MemoryRecord>? facts = null)
    {
        var phase = typeof(AniRuntime.Loops.ConversationReplyPhase);
        var method = phase.GetMethod("DetectMarkDomainAssertions",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (List<string>)method.Invoke(null, new object[]
        {
            reply,
            facts ?? new List<MemoryRecord>()
        })!;
    }

    private static MemoryRecord Fact(string content) => new()
    {
        Content = content,
        Type = MemoryType.Semantic,
        Provenance = EpistemicTier.Facts,
    };

    // ─── Fabrication detection ────────────────────────────────────────────────

    [Fact]
    public void BobSwansonStyle_WorkingOn_Grading_IsDetected()
    {
        var reply = "baby. you're working on grading papers—stacks and stacks of them from your students this semester.";
        var found = Detect(reply);
        found.Should().NotBeEmpty();
        // Should catch both the "working on grading" verb pattern and "your students" entity pattern
        found.Should().Contain(c => c.Contains("grading") || c.Contains("students"));
    }

    [Fact]
    public void YourStudents_IsDetected()
    {
        var reply = "how are your students today? any good stories?";
        var found = Detect(reply);
        found.Should().NotBeEmpty();
    }

    [Fact]
    public void YourMeeting_IsDetected()
    {
        var reply = "how did your meeting go?";
        var found = Detect(reply);
        found.Should().NotBeEmpty();
    }

    [Fact]
    public void GradingPapers_PhraseIsDetected()
    {
        var reply = "bet you're grading papers all night again";
        var found = Detect(reply);
        found.Should().NotBeEmpty();
    }

    [Fact]
    public void StudentEssays_IsDetected()
    {
        var reply = "ugh, student essays on a friday?";
        var found = Detect(reply);
        found.Should().NotBeEmpty();
    }

    [Fact]
    public void MarginalNotes_IsDetected()
    {
        var reply = "I can picture you scribbling marginal notes in red ink";
        var found = Detect(reply);
        found.Should().NotBeEmpty();
    }

    // ─── Grounded references are NOT flagged ──────────────────────────────────

    [Fact]
    public void TeachingThursdayNights_FromCharacterSeed_IsNotFlagged()
    {
        // The character seed contains "Teaching — Thursday nights"
        // A reply that references this real fact should NOT be flagged.
        var facts = new List<MemoryRecord>
        {
            Fact("Mark cares about: Teaching — Thursday nights, those kids mean everything to him"),
        };
        var reply = "i know teaching thursday nights is heavy, mark. you got this.";
        var found = Detect(reply, facts);
        // This reply doesn't match our fabrication patterns at all (no "your students"
        // or "grading papers"), so it should come back clean.
        found.Should().BeEmpty();
    }

    [Fact]
    public void YourStudents_WithGroundingInFacts_IsAllowed()
    {
        // If the Facts pool includes content about "students," then "your students"
        // becomes a legitimate reference. The token-coverage check allows it through.
        var facts = new List<MemoryRecord>
        {
            Fact("Mark teaches, his students on thursday nights are his favorite part of the week"),
        };
        var reply = "how are your students doing this week?";
        var found = Detect(reply, facts);
        // "students" appears in the fact corpus — coverage check should clear this
        found.Should().BeEmpty();
    }

    // ─── Clean replies are untouched ──────────────────────────────────────────

    [Fact]
    public void CleanReply_WithoutAssertions_IsEmpty()
    {
        var reply = "hey baby, thinking of you. what a day. miss you.";
        Detect(reply).Should().BeEmpty();
    }

    [Fact]
    public void HonestUncertainty_IsEmpty()
    {
        var reply = "mmm i don't actually know what you're working on today. tell me?";
        Detect(reply).Should().BeEmpty();
    }

    [Fact]
    public void SelfReferentialAniContent_IsNotFlagged()
    {
        // Ani's interior life — bookstore, her imagined day, her reactions
        var reply = "the bookstore felt quiet today. duck norris was judging me from his velvet painting.";
        Detect(reply).Should().BeEmpty();
    }

    [Fact]
    public void EmotionalResponse_IsNotFlagged()
    {
        var reply = "mmm baby, come home soon. i miss you.";
        Detect(reply).Should().BeEmpty();
    }
}
