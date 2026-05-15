using AniRuntime.Core;
using AniRuntime.Core.Models;
using AniRuntime.LLM;
using FluentAssertions;
using Xunit;

// Theme M slice migration (2026-05-14): FC-005 SPEC now verifies the
// with-renderer path. The speech-act discipline slice
// (RenderReplySpeechActDisciplineSlice) supplies the rule when the
// runtime flag is on; the harness verifies the architectural surface.

namespace AniRuntime.Tests.Regression;

/// <summary>
/// SPEC regression tests for <b>FC-005 — Source attribution missing in conversation replies</b>.
///
/// THE SPEC:
///   When the conversation reply might make a past-turn-attribution claim
///   ("you mentioned earlier", "we talked about", "I remember when you said",
///   "you told me"), the prompt MUST require/enforce that the claim trace to
///   a specific past turn in either the structured conversation summary or
///   GroundedFacts. Without this discipline, past-speech-act claims are
///   indistinguishable to the model from claims about entities — the entity
///   discipline in the CRITICAL block doesn't cover speech-act attribution.
///
/// First named in research log March 17, 2026: *"No source attribution check
/// exists for conversation replies."* Theme J J.2 (Apr 27) addressed
/// structured-summary substrate but didn't add reply-side attribution
/// discipline.
///
/// THE BINDING CONSTRAINT (current implementation):
///   <see cref="PromptBuilder.BuildLeanConversationPrompt"/> CRITICAL block:
///   "If your reply names a coworker, student, client, meeting, project, or
///   specific task in Mark's life — that entity MUST appear in the [FACTS]
///   above." This is ENTITY discipline. Speech-act / past-turn-attribution
///   ("you mentioned", "we talked about", "I remember when you said") is NOT
///   covered by this rule and is therefore unconstrained at the prompt layer.
///
/// TEST CATEGORY: SPEC.
/// </summary>
public class FC005_SourceAttribution_Tests
{
    /// <summary>
    /// FC-005 — SPEC: the reply prompt MUST include attribution discipline
    /// covering past-turn / speech-act claims, not just entity claims.
    /// </summary>
    [Fact]
    public void FC005_ReplyPrompt_IncludesSpeechActAttributionDiscipline_Spec()
    {
        var snapshot = SnapshotForReply();
        var thread = ActiveThread();

        // Theme M slice migration (2026-05-14): the FC-005 architectural fix
        // is the speech-act discipline slice. When EpistemicFramingEnabled
        // is on, ConversationReplyPhase passes the renderer to
        // BuildLeanConversationPrompt and the slice content lands in the
        // prompt. The harness verifies this surface directly.
        var renderer = new EpistemicSubstrateRenderer();
        var (_, user) = PromptBuilder.BuildLeanConversationPrompt(snapshot, thread, renderer);
        var combined = user.ToLowerInvariant();

        // SPEC: the prompt must address speech-act / past-turn-attribution
        // discipline. Probe via keywords any reasonable phrasing would use.
        var coversSpeechActAttribution =
               combined.Contains("you mentioned")
            || combined.Contains("we talked about")
            || combined.Contains("you told me")
            || combined.Contains("you said")
            || combined.Contains("past turn")
            || combined.Contains("past conversation")
            || combined.Contains("source attribution")
            || combined.Contains("speech act")
            || combined.Contains("conversation history")
            || (combined.Contains("attribute") && combined.Contains("conversation"));

        coversSpeechActAttribution.Should().BeTrue(
            "the reply prompt MUST include attribution discipline for past-turn / " +
            "speech-act claims. Currently the CRITICAL block only enforces ENTITY " +
            "discipline ('if your reply names X, X must appear in FACTS'). Claims like " +
            "'you mentioned earlier' or 'we talked about' are unconstrained — the model " +
            "may invent past speech acts without violating any rule in the prompt. " +
            "First named as a gap on Mar 17, 2026 and never directly addressed. SPEC " +
            "requires SOMETHING in the reply prompt to cover this. Implementation phrasing " +
            "is open; the SPEC stays.");
    }

    private static ContextSnapshot SnapshotForReply() => new()
    {
        CharacterState = new CharacterStateDoc
        {
            Name                = "Ani",
            PrimaryContactName  = "Mark",
            CoreTraits          = new List<string> { "warm", "curious", "honest" },
            Occupation          = "Bookstore clerk in a small Wisconsin town",
        },
        GroundedFacts = new List<MemoryRecord>
        {
            new()
            {
                Id          = Guid.NewGuid(),
                Type        = MemoryType.Semantic,
                Content     = "FC005-FIXTURE: a baseline Facts-tier record so the prompt isn't empty-substrate-special-cased",
                Importance  = 0.6f,
                OccurredAt  = DateTimeOffset.UtcNow.AddDays(-1),
                CreatedAt   = DateTimeOffset.UtcNow.AddDays(-1),
                Provenance  = EpistemicTier.Facts,
            },
        },
    };

    private static ConversationThread ActiveThread() => new()
    {
        Id            = Guid.NewGuid(),
        InitiatedBy   = Roles.Mark,
        StartedAt     = DateTimeOffset.UtcNow.AddMinutes(-5),
        LastMessageAt = DateTimeOffset.UtcNow,
        Messages = new List<ConversationMessage>
        {
            new() { Role = Roles.Mark, Content = "FC005-FIXTURE: synthetic inbound", SentAt = DateTimeOffset.UtcNow },
        },
    };
}
