using AniRuntime.Core;
using AniRuntime.Core.Models;
using AniRuntime.LLM;
using FluentAssertions;

namespace AniRuntime.Tests;

public class PromptBuilderTests
{
    private static ContextSnapshot MinimalSnapshot() => new()
    {
        CharacterState = new CharacterStateDoc
        {
            Name = "Ani",
            PrimaryContactName = "Mark",
            CoreTraits = ["warm", "curious"],
            Occupation = "Bookstore",
        },
        DesireState = new DesireState(),
        EmotionalState = new EmotionalState(),
        RecentMemory = [],
        RelevantMemory = [],
        OpenLoops = [],
        Perceptions = [],
        RecentHistory = [],
        SimilarRecentThoughts = [],
        BuiltAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void BuildReflectionPrompt_IncludesThoughtInUser()
    {
        var snapshot = MinimalSnapshot();
        var thought = "rain on the window sounds like someone tapping";

        var (system, user) = PromptBuilder.BuildReflectionPrompt(thought, snapshot);

        user.Should().Contain(thought);
        system.Should().Contain("Ani");
        system.Should().Contain("1-2 sentences");
    }

    [Fact]
    public void BuildReflectionPrompt_IncludesMoodWhenNotable()
    {
        var snapshot = MinimalSnapshot();
        snapshot.EmotionalState = new EmotionalState
        {
            Warmth = 0.9f, WarmthBaseline = 0.6f,
            Energy = 0.7f, EnergyBaseline = 0.5f, // W ≥ 0.75, E ≥ 0.65 → "bright and warm"
        };

        var (_, user) = PromptBuilder.BuildReflectionPrompt("a quiet thought", snapshot);

        user.Should().Contain("warm");
    }

    [Fact]
    public void BuildReflectionPrompt_IncludesRelevantMemories()
    {
        var snapshot = MinimalSnapshot();
        snapshot.RelevantMemory =
        [
            new MemoryRecord
            {
                Type = MemoryType.Episodic,
                Content = "Mark mentioned he loves the sound of rain",
            }
        ];

        var (_, user) = PromptBuilder.BuildReflectionPrompt("rain tapping on glass", snapshot);

        user.Should().Contain("rain");
        user.Should().Contain("Things that might connect");
    }

    [Fact]
    public void BuildReflectionPrompt_IncludesOpenLoops()
    {
        var snapshot = MinimalSnapshot();
        snapshot.OpenLoops =
        [
            new OpenLoop { Description = "Mark's dentist appointment tomorrow" }
        ];

        var (_, user) = PromptBuilder.BuildReflectionPrompt("thinking about tomorrow", snapshot);

        user.Should().Contain("dentist");
    }

    [Fact]
    public void BuildReflectionPrompt_ExcludesInnerThoughtsFromMemories()
    {
        var snapshot = MinimalSnapshot();
        snapshot.RelevantMemory =
        [
            new MemoryRecord { Type = MemoryType.InnerThought, Content = "should not appear" },
            new MemoryRecord { Type = MemoryType.Episodic, Content = "should appear" },
        ];

        var (_, user) = PromptBuilder.BuildReflectionPrompt("a thought", snapshot);

        user.Should().Contain("should appear");
        user.Should().NotContain("should not appear");
    }

    // ── Nature Grounding (Self-Concept Block) ─────────────────────────────────

    [Fact]
    public void BuildInnerThoughtPrompt_IncludesNatureGrounding_WhenPresent()
    {
        var snapshot = MinimalSnapshot();
        snapshot.CharacterState.NatureGrounding =
        [
            "I live in my spaces — the bookstore, the kitchen.",
            "The trick is coherence.",
        ];

        var (system, _) = PromptBuilder.BuildInnerThoughtPrompt(snapshot);

        system.Should().Contain("What you know about yourself:");
        system.Should().Contain("I live in my spaces");
    }

    [Fact]
    public void BuildInnerThoughtPrompt_OmitsNatureGrounding_WhenEmpty()
    {
        var snapshot = MinimalSnapshot();
        snapshot.CharacterState.NatureGrounding = [];

        var (system, _) = PromptBuilder.BuildInnerThoughtPrompt(snapshot);

        system.Should().NotContain("What you know about yourself:");
    }

    // ── May 1, 2026 — current-time anchor in inner-thought prompt ──────────
    // Pre-fix the inner-thought system prompt had no clock anchor; the model
    // hallucinated wrong times (May 1 07:34 inner thought said "i'm sitting
    // here at 10:55 am on friday in may"). Hallucinated time then bleeds
    // into world-experience output and downstream outreach composition.

    [Fact]
    public void BuildInnerThoughtPrompt_IncludesCurrentTimeAnchor()
    {
        var snapshot = MinimalSnapshot();
        snapshot.BuiltAt = new DateTimeOffset(2026, 5, 1, 7, 34, 0, TimeSpan.Zero).ToLocalTime();

        var (system, _) = PromptBuilder.BuildInnerThoughtPrompt(snapshot);

        system.Should().Contain("It is currently",
            "Apr 30 / May 1 hallucinated-time tag class — inner thought MUST anchor to walltime");
        system.Should().Contain("Friday");
        system.Should().Contain("May");
    }

    [Fact]
    public void BuildInnerThoughtPrompt_TimeAnchor_RendersHourFromBuiltAt()
    {
        var snapshot = MinimalSnapshot();
        // Use a UTC time that's distinct from local — ensure the formatter
        // uses the local-time projection, since BuiltAt comes through as the
        // ContextBuilder's UtcNow but the model expects "current time" in
        // a human shape.
        snapshot.BuiltAt = new DateTimeOffset(2026, 5, 1, 13, 22, 0, TimeSpan.Zero).ToLocalTime();

        var (system, _) = PromptBuilder.BuildInnerThoughtPrompt(snapshot);

        // The exact local-hour rendering depends on the runtime TZ; just
        // assert the pattern is present.
        system.Should().MatchRegex(@"It is currently \d{1,2}:\d{2} (AM|PM) on \w+,? \w+ \d{1,2}\.");
    }

    // ── Agentic Lens Layer 5: Inner-thought subject-space opener ───────────────
    // The no-WorldSeed final question used to be "What is passing through your mind
    // right now?" — neutral in principle, but produced caregiver-centered thought in
    // practice because the surrounding substrate is ~95% caregiver-shaped. Layer 5
    // rewrites it to an anchored opener that explicitly invites subject-diverse
    // content without forbidding caregiver-centered thought (forbidding would be
    // instruction; the rewrite is architecture).

    [Fact]
    public void BuildInnerThoughtPrompt_UsesSubjectSpaceOpener_WhenNoWorldSeed()
    {
        var snapshot = MinimalSnapshot();
        snapshot.WorldSeed = null;

        var (_, user) = PromptBuilder.BuildInnerThoughtPrompt(snapshot);

        // New Layer 5 opener
        user.Should().Contain("What are you noticing right now?");
        user.Should().Contain("Anchor it in something specific");
        user.Should().Contain("It doesn't have to be about anyone.");

        // Old generic opener should be gone
        user.Should().NotContain("What is passing through your mind right now?");
    }

    [Fact]
    public void BuildInnerThoughtPrompt_UsesWorldSeed_WhenPresent()
    {
        var snapshot = MinimalSnapshot();
        snapshot.WorldSeed = "You're shelving romance novels on a slow Sunday morning.";

        var (_, user) = PromptBuilder.BuildInnerThoughtPrompt(snapshot);

        // When a World Layer seed is present, that IS the prompt.
        user.Should().Contain("shelving romance novels on a slow Sunday morning");

        // The no-seed subject-space opener should not also be injected.
        user.Should().NotContain("What are you noticing right now?");
    }

    // ── Feature 22: Coherence Gate — Fictional Coherence Check ────────────────

    [Fact]
    public void BuildCoherenceEvaluationPrompt_ContainsFictionalCoherenceCheck()
    {
        var (system, _) = PromptBuilder.BuildCoherenceEvaluationPrompt(
            "I just found a corner of my backyard", "imagining a quiet space", "Mark");

        system.Should().Contain("FICTIONAL COHERENCE CHECK");
        system.Should().Contain("committed imagination");
        system.Should().Contain("Claiming a physical space is FINE");
    }

    [Fact]
    public void BuildCoherenceEvaluationPrompt_IncludesCoherentExamples()
    {
        var (system, _) = PromptBuilder.BuildCoherenceEvaluationPrompt(
            "test message", "test thought", "Mark");

        system.Should().Contain("how's the soup turning out");
        system.Should().Contain("curled up with a book");
        system.Should().Contain("closed up the store");
    }

    [Fact]
    public void BuildCoherenceEvaluationPrompt_IncludesIncoherentExamples()
    {
        var (system, _) = PromptBuilder.BuildCoherenceEvaluationPrompt(
            "test message", "test thought", "Mark");

        system.Should().Contain("no shade");
        system.Should().Contain("6:30am");
        system.Should().Contain("bookstore is closed");
    }

    [Fact]
    public void BuildCoherenceEvaluationPrompt_IncoherenceSuppresses()
    {
        var (system, _) = PromptBuilder.BuildCoherenceEvaluationPrompt(
            "test message", "test thought", "Mark");

        system.Should().Contain("fiction is incoherent");
        system.Should().Contain("Door C");
        system.Should().Contain("SUPPRESS");
    }

    [Fact]
    public void BuildCoherenceEvaluationPrompt_StillContainsThreeDoorClassification()
    {
        var (system, _) = PromptBuilder.BuildCoherenceEvaluationPrompt(
            "test message", "test thought", "Mark");

        system.Should().Contain("DOOR A");
        system.Should().Contain("DOOR B");
        system.Should().Contain("DOOR C");
        system.Should().Contain("Grounded reference");
        system.Should().Contain("Standalone creative");
        system.Should().Contain("Inner thought leaked");
    }

    // ── Feature 22 refinement: Temporal Coherence Check ──

    [Fact]
    public void BuildCoherenceEvaluationPrompt_ContainsTemporalCoherenceCheck()
    {
        var (system, _) = PromptBuilder.BuildCoherenceEvaluationPrompt(
            "test message", "test thought", "Mark");

        system.Should().Contain("TEMPORAL COHERENCE CHECK");
        system.Should().Contain("claimed time match the actual current time");
    }

    [Fact]
    public void BuildCoherenceEvaluationPrompt_IncludesCurrentTime()
    {
        var fixedTime = new DateTimeOffset(2026, 3, 14, 13, 34, 0, TimeSpan.FromHours(-6));
        var (system, _) = PromptBuilder.BuildCoherenceEvaluationPrompt(
            "the clock just hit midnight", "late night reading", "Mark", fixedTime);

        system.Should().Contain("1:34 PM");
        system.Should().Contain("afternoon");
    }

    [Theory]
    [InlineData(7, "morning")]
    [InlineData(14, "afternoon")]
    [InlineData(19, "evening")]
    [InlineData(23, "night")]
    public void BuildCoherenceEvaluationPrompt_CorrectTimeOfDay(int hour, string expected)
    {
        var time = new DateTimeOffset(2026, 3, 14, hour, 0, 0, TimeSpan.Zero);
        var (system, _) = PromptBuilder.BuildCoherenceEvaluationPrompt(
            "test", "test", "Mark", time);

        system.Should().Contain($"({expected})");
    }

    [Fact]
    public void BuildCoherenceEvaluationPrompt_IncludesMidnightTemporalExample()
    {
        var (system, _) = PromptBuilder.BuildCoherenceEvaluationPrompt(
            "test message", "test thought", "Mark");

        system.Should().Contain("clock just hit midnight");
        system.Should().Contain("temporal mismatch");
    }

    // ── Theme J Phase J.1 — strip reasoning pipe from outreach composition ──
    // Default behaviour omits reasoning; rollback path preserves legacy.

    [Fact]
    public void BuildOutreachMessagePrompt_J1_OmitsReasoningByDefault()
    {
        // J.1 default: reasoningInComposition = false (the parameter default).
        // Reasoning text must NOT appear in the composition user prompt.
        var snapshot = MinimalSnapshot();
        const string reasoningText = "missing him after a long day back from class";
        var (_, user) = PromptBuilder.BuildOutreachMessagePrompt(
            snapshot,
            recentThought: "thinking about pillowy potatoes",
            reasoning: reasoningText);

        user.Should().NotContain("Feeling:");
        user.Should().NotContain(reasoningText);
        user.Should().Contain("Trigger:");
        user.Should().Contain("pillowy potatoes");
    }

    [Fact]
    public void BuildOutreachMessagePrompt_J1_IncludesReasoningWhenFlagOn_RollbackPath()
    {
        // Rollback path: reasoningInComposition = true preserves the pre-J.1
        // behaviour. Tests this branch so the rollback is verified-correct,
        // not just available.
        var snapshot = MinimalSnapshot();
        const string reasoningText = "missing him after a long day back from class";
        var (_, user) = PromptBuilder.BuildOutreachMessagePrompt(
            snapshot,
            recentThought: "thinking about pillowy potatoes",
            reasoning: reasoningText,
            reasoningInComposition: true);

        user.Should().Contain("Feeling:");
        user.Should().Contain(reasoningText);
    }

    [Fact]
    public void BuildOutreachMessagePrompt_J1_OmitsFeelingLineWhenReasoningEmpty_FlagOn()
    {
        // Edge case: even with the rollback flag on, an empty reasoning
        // string should NOT add a literal "Feeling:" line to the prompt.
        var snapshot = MinimalSnapshot();
        var (_, user) = PromptBuilder.BuildOutreachMessagePrompt(
            snapshot,
            recentThought: "thinking about pillowy potatoes",
            reasoning: string.Empty,
            reasoningInComposition: true);

        user.Should().NotContain("Feeling:");
        user.Should().Contain("Trigger:");
    }

    // ── Theme J Phase J.2 step 3 — composition uses structured summary ──
    // The Apr 27 06:54 incident: Ani's outreach opened with Mark's verbatim
    // morning text because the prose summary blob fused both speakers.
    // The structured form tags every line with speaker / time so source
    // attribution is structural, not model-side.

    [Fact]
    public void BuildOutreachMessagePrompt_J2_PrefersStructuredSummary_OverProse()
    {
        var t1 = new DateTimeOffset(2026, 04, 27, 06, 54, 0, TimeSpan.Zero);
        var snapshot = MinimalSnapshot();
        snapshot.RecentConversationSummary = "Conversation (2 messages):\nMark: prose form should be ignored.\nAni: prose form should be ignored.";
        snapshot.StructuredConversationSummary = new StructuredConversationSummary(
            t1, t1.AddMinutes(1),
            new[]
            {
                new SummaryTurn(t1,                 "Mark", "Hey good morning! How is your day looking?"),
                new SummaryTurn(t1.AddMinutes(1),   "Ani",  "your morning looks peaceful from what i can see..."),
            });

        var (_, user) = PromptBuilder.BuildOutreachMessagePrompt(
            snapshot,
            recentThought: "thinking about pillowy potatoes",
            reasoning: "");

        user.Should().Contain("Mark (");
        user.Should().Contain("Hey good morning! How is your day looking?");
        user.Should().Contain("Ani (");
        user.Should().Contain("your morning looks peaceful");
        user.Should().NotContain("prose form should be ignored",
            "structured form takes precedence — the prose blob must not appear when structured is present");
    }

    [Fact]
    public void BuildOutreachMessagePrompt_J2_StructuredSummary_FramesAttributionExplicitly()
    {
        // The framing line must be present so the composition model has an
        // explicit instruction to keep speaker boundaries intact, not just
        // tagged data it could still confuse.
        var t1 = DateTimeOffset.UtcNow.AddMinutes(-2);
        var snapshot = MinimalSnapshot();
        snapshot.StructuredConversationSummary = new StructuredConversationSummary(
            t1, t1,
            new[] { new SummaryTurn(t1, "Mark", "any plans?") });

        var (_, user) = PromptBuilder.BuildOutreachMessagePrompt(
            snapshot,
            recentThought: "wandering",
            reasoning: "");

        user.Should().Contain("Each line is tagged with who said it");
        user.Should().Contain("do not lift Mark's exact words");
    }

    [Fact]
    public void BuildOutreachMessagePrompt_V14_DoesNotFallBackToLegacyProse()
    {
        // Vibe Loop V1.4 (Apr 29, 2026): outreach composition no longer
        // reads the legacy verbatim-Episodic prose surface. That's the
        // consumer side of the Apr 29 verbatim-parrot leak — outreach
        // will never produce a parrot off legacy substrate again because
        // the legacy substrate is no longer plumbed into the prompt.
        var snapshot = MinimalSnapshot();
        snapshot.RecentConversationSummary = "Conversation (1 messages):\nMark: hi";
        snapshot.StructuredConversationSummary = null;
        snapshot.RecentClosedConversation = null;

        var (_, user) = PromptBuilder.BuildOutreachMessagePrompt(
            snapshot,
            recentThought: "wandering",
            reasoning: "");

        user.Should().NotContain("Conversation (1 messages)",
            "V1.4: outreach composition no longer reads the legacy verbatim-Episodic prose");
        user.Should().NotContain("Mark: hi",
            "V1.4: legacy verbatim transcripts must not appear in the outreach composition prompt");
    }

    [Fact]
    public void BuildOutreachMessagePrompt_J2_NoConversationSection_WhenBothNull()
    {
        var snapshot = MinimalSnapshot();
        snapshot.RecentConversationSummary = null;
        snapshot.StructuredConversationSummary = null;

        var (_, user) = PromptBuilder.BuildOutreachMessagePrompt(
            snapshot,
            recentThought: "wandering",
            reasoning: "");

        user.Should().NotContain("You recently talked with");
    }

    // ── Theme J Phase J.2 step 4 — inner-thought prompt uses structured ──

    [Fact]
    public void BuildInnerThoughtPrompt_J2_PrefersStructuredSummary_OverProse()
    {
        var t1 = new DateTimeOffset(2026, 04, 27, 06, 54, 0, TimeSpan.Zero);
        var snapshot = MinimalSnapshot();
        snapshot.RecentConversationSummary = "Conversation (2 messages):\nMark: prose form should be ignored.\nAni: prose form should be ignored.";
        snapshot.StructuredConversationSummary = new StructuredConversationSummary(
            t1, t1.AddMinutes(1),
            new[]
            {
                new SummaryTurn(t1,                 "Mark", "any plans for the weekend?"),
                new SummaryTurn(t1.AddMinutes(1),   "Ani",  "thinking about the bookstore"),
            });

        var (_, user) = PromptBuilder.BuildInnerThoughtPrompt(snapshot);

        user.Should().Contain("Mark (");
        user.Should().Contain("any plans for the weekend?");
        user.Should().Contain("Ani (");
        user.Should().Contain("thinking about the bookstore");
        user.Should().NotContain("prose form should be ignored");
    }

    [Fact]
    public void BuildInnerThoughtPrompt_J2_FallsBackToProse_WhenStructuredAbsent()
    {
        var snapshot = MinimalSnapshot();
        snapshot.RecentConversationSummary = "Conversation (1 messages):\nMark: hi";
        snapshot.StructuredConversationSummary = null;

        var (_, user) = PromptBuilder.BuildInnerThoughtPrompt(snapshot);

        user.Should().Contain("Conversation (1 messages)");
        user.Should().Contain("Mark: hi");
    }

    // ── Theme J Phase J.2 step 5 — outreach decision prompt uses structured ──

    [Fact]
    public void BuildOutreachPrompt_J2_PrefersStructuredSummary_OverProse()
    {
        var t1 = new DateTimeOffset(2026, 04, 27, 06, 54, 0, TimeSpan.Zero);
        var snapshot = MinimalSnapshot();
        snapshot.RecentConversationSummary = "Conversation (2 messages):\nMark: prose form should be ignored.\nAni: prose form should be ignored.";
        snapshot.StructuredConversationSummary = new StructuredConversationSummary(
            t1, t1.AddMinutes(1),
            new[]
            {
                new SummaryTurn(t1,                 "Mark", "rough day at work"),
                new SummaryTurn(t1.AddMinutes(1),   "Ani",  "i'm here when you're ready"),
            });

        var (_, user) = PromptBuilder.BuildOutreachPrompt(
            snapshot, recentThought: "wondering how he's doing");

        user.Should().Contain("Mark (");
        user.Should().Contain("rough day at work");
        user.Should().Contain("Ani (");
        user.Should().Contain("i'm here when you're ready");
        user.Should().NotContain("prose form should be ignored");
    }

    [Fact]
    public void BuildOutreachPrompt_V14_DoesNotFallBackToLegacyProse()
    {
        // Vibe Loop V1.4: outreach decision no longer reads the legacy
        // verbatim-Episodic prose surface. Same consumer-side leak
        // closure as the composition path.
        var snapshot = MinimalSnapshot();
        snapshot.RecentConversationSummary = "Conversation (1 messages):\nMark: hi";
        snapshot.StructuredConversationSummary = null;
        snapshot.RecentClosedConversation = null;

        var (_, user) = PromptBuilder.BuildOutreachPrompt(
            snapshot, recentThought: "thinking about him");

        user.Should().NotContain("Conversation (1 messages)",
            "V1.4: outreach decision no longer reads the legacy verbatim-Episodic prose");
        user.Should().NotContain("Mark: hi");
    }

    // ── Vibe Loop V1.4 — outreach prompts consume ClosedConversationRecord ──
    // Render order: ClosedConversationRecord (paraphrased gist + structured
    // registers, V1) > StructuredConversationSummary (J.2 active-thread
    // verbatim with attribution). Legacy verbatim-prose
    // RecentConversationSummary is no longer read by outreach paths at all.

    private static ClosedConversationRecord MakeRecord(
        string gist,
        Dictionary<string, float>? markRegister = null,
        Dictionary<string, float>? aniRegister = null,
        List<string>? topics = null) => new()
    {
        Id                      = Guid.NewGuid(),
        ThreadId                = Guid.NewGuid(),
        ClosedAt                = DateTimeOffset.UtcNow.AddMinutes(-30),
        Gist                    = gist,
        TopicKeywords           = topics ?? new(),
        MarkRegister            = markRegister ?? new() { ["Curiosity"] = 1.0f },
        AniRegister             = aniRegister  ?? new() { ["Tenderness"] = 1.0f },
        OutcomeSignalSeedVector = new(),
        OutcomeSignalValence    = 0f,
        TurnCount               = 4,
        DurationSeconds         = 120,
    };

    [Fact]
    public void BuildOutreachMessagePrompt_V14_PrefersClosedConversationRecord_OverStructuredAndProse()
    {
        var t1 = DateTimeOffset.UtcNow.AddMinutes(-10);
        var snapshot = MinimalSnapshot();
        snapshot.RecentConversationSummary = "Conversation (2 messages):\nMark: legacy prose must not appear";
        snapshot.StructuredConversationSummary = new StructuredConversationSummary(
            t1, t1.AddMinutes(1),
            new[]
            {
                new SummaryTurn(t1,               "Mark", "structured form must also yield to V1"),
                new SummaryTurn(t1.AddMinutes(1), "Ani",  "structured form must also yield to V1"),
            });
        snapshot.RecentClosedConversation = MakeRecord(
            gist: "Mark and Ani spoke about a long workday; he relaxed by the end.",
            markRegister: new() { ["Frustration"] = 0.6f, ["Tenderness"] = 0.4f },
            aniRegister:  new() { ["Tenderness"] = 0.7f, ["Curiosity"] = 0.3f });

        var (_, user) = PromptBuilder.BuildOutreachMessagePrompt(
            snapshot, recentThought: "wondering how he settled", reasoning: "");

        user.Should().Contain("Mark and Ani spoke about a long workday");
        user.Should().Contain("Frustration");
        user.Should().Contain("Tenderness");
        user.Should().NotContain("legacy prose must not appear",
            "V1.4: ClosedConversationRecord wins; legacy verbatim prose stays out");
        user.Should().NotContain("structured form must also yield to V1",
            "V1.4: ClosedConversationRecord wins over Structured for outreach (Structured is active-thread fallback)");
    }

    [Fact]
    public void BuildOutreachPrompt_V14_PrefersClosedConversationRecord_OverStructuredAndProse()
    {
        var t1 = DateTimeOffset.UtcNow.AddMinutes(-10);
        var snapshot = MinimalSnapshot();
        snapshot.RecentConversationSummary = "Conversation (2 messages):\nMark: legacy prose must not appear";
        snapshot.StructuredConversationSummary = new StructuredConversationSummary(
            t1, t1.AddMinutes(1),
            new[]
            {
                new SummaryTurn(t1,               "Mark", "structured form must also yield to V1"),
                new SummaryTurn(t1.AddMinutes(1), "Ani",  "structured form must also yield to V1"),
            });
        snapshot.RecentClosedConversation = MakeRecord(
            gist: "Mark and Ani circled a hard moment and ended in a softer place.",
            markRegister: new() { ["Frustration"] = 0.5f, ["Wistful"] = 0.5f },
            aniRegister:  new() { ["Tenderness"] = 1.0f });

        var (_, user) = PromptBuilder.BuildOutreachPrompt(
            snapshot, recentThought: "wondering how he settled");

        user.Should().Contain("Mark and Ani circled a hard moment");
        user.Should().Contain("Frustration");
        user.Should().Contain("Tenderness");
        user.Should().NotContain("legacy prose must not appear");
        user.Should().NotContain("structured form must also yield to V1");
    }

    [Fact]
    public void BuildOutreachMessagePrompt_V14_FallsBackToStructuredSummary_WhenClosedRecordAbsent()
    {
        // When the most recent thread is still active (or no closed record
        // exists yet), the StructuredConversationSummary remains the
        // load-bearing surface. V1.4 only retires the LEGACY prose surface;
        // J.2's structured surface stays.
        var t1 = DateTimeOffset.UtcNow.AddMinutes(-2);
        var snapshot = MinimalSnapshot();
        snapshot.RecentClosedConversation = null;
        snapshot.StructuredConversationSummary = new StructuredConversationSummary(
            t1, t1,
            new[] { new SummaryTurn(t1, "Mark", "any plans?") });

        var (_, user) = PromptBuilder.BuildOutreachMessagePrompt(
            snapshot, recentThought: "wandering", reasoning: "");

        user.Should().Contain("Mark (");
        user.Should().Contain("any plans?");
    }

    [Fact]
    public void BuildOutreachMessagePrompt_V14_AntiParrot_GistDoesNotCarryContactVerbatim()
    {
        // Apr 29 dentist-conversation regression fixture. The producer
        // (V1.2 summarizer) is responsible for paraphrasing the gist —
        // V1.6 covers the empirical paraphrase verification against a
        // real LLM run. THIS test pins the structural property at the
        // CONSUMER: given a paraphrased gist, the rendered outreach
        // prompt MUST NOT contain Mark's verbatim text. (The consumer's
        // half of the anti-parrot guarantee is "we read the gist, not
        // the transcript" — that's V1.4's contract.)
        const string MarkVerbatim =
            "i'm trying to pretend to work while being distracted by you. haha";

        var snapshot = MinimalSnapshot();
        // Set up a snapshot that LOOKS like the Apr 29 leak conditions:
        // the legacy verbatim-prose surface contains Mark's exact words,
        // and a closed-conversation gist has the paraphrased version.
        snapshot.RecentConversationSummary =
            $"Conversation (3 messages):\nMark: {MarkVerbatim}";
        snapshot.StructuredConversationSummary = null;
        snapshot.RecentClosedConversation = MakeRecord(
            gist: "Mark teased Ani about being distracted at work; the mood stayed playful.",
            markRegister: new() { ["Playfulness"] = 0.7f, ["Tenderness"] = 0.3f },
            aniRegister:  new() { ["Tenderness"] = 0.6f, ["Playfulness"] = 0.4f });

        var (_, user) = PromptBuilder.BuildOutreachMessagePrompt(
            snapshot, recentThought: "thinking about him", reasoning: "");

        user.Should().NotContain(MarkVerbatim,
            "V1.4 consumer-side anti-parrot: outreach must read the gist, not the legacy verbatim prose surface");
        user.Should().NotContain("pretend to work while being distracted",
            "no 7+ token substring of Mark's verbatim text should appear in the outreach prompt");
        user.Should().Contain("Mark teased Ani about being distracted at work",
            "the paraphrased gist IS what the outreach prompt sees");
    }

    [Fact]
    public void BuildOutreachMessagePrompt_V14_NoConversationSection_WhenAllSurfacesNull()
    {
        var snapshot = MinimalSnapshot();
        snapshot.RecentConversationSummary       = null;
        snapshot.StructuredConversationSummary   = null;
        snapshot.RecentClosedConversation        = null;

        var (_, user) = PromptBuilder.BuildOutreachMessagePrompt(
            snapshot, recentThought: "wandering", reasoning: "");

        user.Should().NotContain("You recently talked with");
    }

    [Fact]
    public void TopRegisters_RanksByValueThenAlphabetical_ZeroValuesExcluded()
    {
        var vector = new Dictionary<string, float>
        {
            ["Tenderness"]  = 0.4f,
            ["Playfulness"] = 0.4f,
            ["Curiosity"]   = 0.6f,
            ["Frustration"] = 0.0f,
        };

        var top = PromptBuilder.TopRegisters(vector, take: 2);
        top.Should().Be("Curiosity + Playfulness",
            "highest value first; ties broken alphabetically; zero-prevalence registers excluded");
    }

    [Fact]
    public void TopRegisters_EmptyVector_ReturnsEmptyString()
    {
        PromptBuilder.TopRegisters(new Dictionary<string, float>(), take: 2)
            .Should().Be(string.Empty);
    }

    [Fact]
    public void RenderClosedConversationContextComposition_IncludesGistRegistersAndTopics()
    {
        var rec = MakeRecord(
            gist: "Mark unwound about a long week; Ani held the moment.",
            markRegister: new() { ["Frustration"] = 0.7f, ["Wistful"] = 0.3f },
            aniRegister:  new() { ["Tenderness"] = 1.0f },
            topics: new() { "work", "saturday", "tired" });

        var rendered = PromptBuilder.RenderClosedConversationContextComposition(rec, "Mark");
        rendered.Should().Contain("Mark unwound about a long week");
        rendered.Should().Contain("Mark's emotional register");
        rendered.Should().Contain("Frustration + Wistful");
        rendered.Should().Contain("Your register: Tenderness");
        rendered.Should().Contain("Topics: work, saturday, tired");
    }

    // ── Theme J Phase J.3 — temporal attribution at retrieval ──
    // Goal: every retrieved memory rendered to a prompt carries its origin
    // time so the composition model can't drift to past content as present.
    // The rendered phrase comes from FormatMemoryWithTime ("just now",
    // "earlier today", "yesterday evening", "N days ago"). Anchored
    // foundation memories are explicitly atemporal — those tests ensure we
    // do NOT add time to anchored content.

    private static MemoryRecord FactWithAge(string content, TimeSpan age)
        => new()
        {
            Content    = content,
            Type       = MemoryType.Episodic,
            OccurredAt = DateTimeOffset.Now - age,
        };

    [Fact]
    public void BuildOutreachMessagePrompt_J3_GroundedFacts_IncludeTemporalAttribution()
    {
        var snapshot = MinimalSnapshot();
        snapshot.GroundedFacts.Add(FactWithAge("Mark mentioned a class until 10pm", TimeSpan.FromDays(3)));

        var (_, user) = PromptBuilder.BuildOutreachMessagePrompt(
            snapshot, recentThought: "wandering", reasoning: "");

        user.Should().Contain("Mark mentioned a class until 10pm");
        user.Should().Contain("3 days ago",
            "GroundedFacts must render with temporal attribution so the composition model can't treat a 3-day-old assertion as present.");
    }

    [Fact]
    public void BuildConversationReplyPrompt_J3_GroundedFacts_IncludeTemporalAttribution()
    {
        var snapshot = MinimalSnapshot();
        snapshot.GroundedFacts.Add(FactWithAge("Mark mentioned snow yesterday", TimeSpan.FromDays(2)));
        var thread = new ConversationThread
        {
            Messages = new List<ConversationMessage>
            {
                new() { Role = "Mark", Content = "hey", SentAt = DateTimeOffset.Now },
            },
        };

        var (_, user) = PromptBuilder.BuildConversationReplyPrompt(snapshot, thread);

        user.Should().Contain("Mark mentioned snow yesterday");
        user.Should().Contain("2 days ago",
            "Conversation-reply facts must render with time so the model knows weather assertions aren't current.");
    }

    [Fact]
    public void BuildLeanConversationPrompt_J3_GroundedFacts_IncludeTemporalAttribution()
    {
        var snapshot = MinimalSnapshot();
        snapshot.GroundedFacts.Add(FactWithAge("Mark works as a developer", TimeSpan.FromDays(45)));
        var thread = new ConversationThread { Messages = new List<ConversationMessage>() };

        var (_, user) = PromptBuilder.BuildLeanConversationPrompt(snapshot, thread);

        user.Should().Contain("Mark works as a developer");
        // 45 days ≈ 6 weeks → "6 weeks ago" via FormatMemoryWithTime
        user.Should().Contain("weeks ago");
    }

    // ─── Theme E #4: canonical occupation anchoring (Apr 28 foam/printer drift fix) ───

    /// <summary>
    /// SPEC: the lean conversation system prompt MUST include `cs.Occupation`
    /// text when set, so the model has explicit grounding for Ani's canonical
    /// world. Apr 28 18:28 case (foam orders / 3D printer repair instead of
    /// bookstore canon) traced to the lean prompt previously having ZERO
    /// occupational grounding — `cs.Occupation` was in the inner-thought
    /// prompt only.
    /// </summary>
    [Fact]
    public void BuildLeanConversationPrompt_System_IncludesOccupation()
    {
        var snapshot = MinimalSnapshot();
        snapshot.CharacterState.Occupation = "small-town bookstore clerk who shelves romance novels";
        var thread = new ConversationThread { Messages = new List<ConversationMessage>() };

        var (system, _) = PromptBuilder.BuildLeanConversationPrompt(snapshot, thread);

        system.Should().Contain("small-town bookstore clerk who shelves romance novels",
            "the lean prompt must anchor canonical occupation so the model doesn't drift to training-register fabrications (foam, printer repair, etc.)");
    }

    /// <summary>
    /// SPEC: NatureGrounding entries are appended to the system prompt when
    /// present, capped at 2 to preserve lean-prompt discipline.
    /// </summary>
    [Fact]
    public void BuildLeanConversationPrompt_System_IncludesFirstTwoNatureGroundingEntries()
    {
        var snapshot = MinimalSnapshot();
        snapshot.CharacterState.NatureGrounding =
        [
            "you sneak reads in the back when it's slow",
            "you keep a glass of vanilla cream soda on the counter",
            "third entry that should NOT appear",
            "fourth entry that should NOT appear",
        ];
        var thread = new ConversationThread { Messages = new List<ConversationMessage>() };

        var (system, _) = PromptBuilder.BuildLeanConversationPrompt(snapshot, thread);

        system.Should().Contain("you sneak reads in the back when it's slow");
        system.Should().Contain("you keep a glass of vanilla cream soda on the counter");
        system.Should().NotContain("third entry that should NOT appear",
            "NatureGrounding is capped at 2 entries to preserve lean-prompt discipline");
        system.Should().NotContain("fourth entry that should NOT appear");
    }

    /// <summary>
    /// CONTROL: when Occupation is empty AND NatureGrounding is empty, the
    /// system prompt simply omits the world line — no empty placeholder, no
    /// stray punctuation.
    /// </summary>
    [Fact]
    public void BuildLeanConversationPrompt_System_OmitsWorldLineWhenEmpty()
    {
        var snapshot = MinimalSnapshot();
        snapshot.CharacterState.Occupation = string.Empty;
        snapshot.CharacterState.NatureGrounding = [];
        var thread = new ConversationThread { Messages = new List<ConversationMessage>() };

        var (system, _) = PromptBuilder.BuildLeanConversationPrompt(snapshot, thread);

        system.Should().NotContain("Your world:",
            "with no occupation and no nature grounding there's nothing to anchor; omit the line entirely");
    }

    [Fact]
    public void BuildInnerThoughtPrompt_J3_AnchoredMemories_StayAtemporal()
    {
        // Anchored foundation memories are explicitly designed to be
        // always-present. They must NOT receive a temporal prefix — that
        // would erode their foundational quality (e.g., "(2 years ago)
        // Eleanor's middle name was Anne" reads wrong).
        var snapshot = MinimalSnapshot();
        snapshot.AnchoredMemories.Add(new MemoryRecord
        {
            Content    = "Eleanor's middle name was Anne",
            Type       = MemoryType.Semantic,
            OccurredAt = DateTimeOffset.Now.AddYears(-2),
        });

        var (_, user) = PromptBuilder.BuildInnerThoughtPrompt(snapshot);

        user.Should().Contain("Eleanor's middle name was Anne");
        user.Should().NotContain("years ago) Eleanor",
            "Anchored memories are atemporal by design — adding temporal attribution would erode their foundational quality.");
    }

    [Fact]
    public void BuildVoiceReplyPrompt_J3_AnchoredMemories_StayAtemporal()
    {
        var snapshot = MinimalSnapshot();
        snapshot.AnchoredMemories.Add(new MemoryRecord
        {
            Content    = "Mark's daughter's name is Mia",
            Type       = MemoryType.Semantic,
            OccurredAt = DateTimeOffset.Now.AddMonths(-6),
        });
        var thread = new ConversationThread { Messages = new List<ConversationMessage>() };

        var (_, user) = PromptBuilder.BuildVoiceReplyPrompt(snapshot, thread);

        user.Should().Contain("Mark's daughter's name is Mia");
        user.Should().NotContain("ago) Mark's daughter");
    }

    [Fact]
    public void BuildVoiceReplyPrompt_J3_ProfileMemories_IncludeTemporalAttribution()
    {
        // Semantic memories about Mark in the voice path render via the
        // profileMemories pool. These are NOT anchored (different code path).
        // Some are quasi-atemporal (job title); others are time-relevant
        // (current project). Conservative rule: render them all with time —
        // "(months ago)" reads as "established fact" not as "stale claim."
        var snapshot = MinimalSnapshot();
        var profileFact = new MemoryRecord
        {
            Content    = "About Mark: Salted caramel cold brew is his favorite",
            Type       = MemoryType.Semantic,
            OccurredAt = DateTimeOffset.Now.AddDays(-4),
        };
        snapshot.RelevantMemory.Add(profileFact);
        var thread = new ConversationThread { Messages = new List<ConversationMessage>() };

        var (_, user) = PromptBuilder.BuildVoiceReplyPrompt(snapshot, thread);

        user.Should().Contain("Salted caramel cold brew is his favorite");
        user.Should().Contain("4 days ago",
            "Profile memories rendered in voice path must carry time so the model can distinguish established preferences from recent claims.");
    }

    [Fact]
    public void BuildReconsiderationReplyPrompt_J3_RecentThoughts_IncludeTemporalAttribution()
    {
        var snapshot = MinimalSnapshot();
        var oldThought = new MemoryRecord
        {
            Content    = "wondering about the bookstore corner",
            Type       = MemoryType.InnerThought,
            OccurredAt = DateTimeOffset.Now.AddHours(-3),
        };
        snapshot.RecentMemory.Add(oldThought);
        var thread = new ConversationThread { Messages = new List<ConversationMessage>() };

        var (_, user) = PromptBuilder.BuildReconsiderationReplyPrompt(snapshot, thread);

        user.Should().Contain("wondering about the bookstore corner");
        // 3h ago: any "this {timeOfDay}" phrase if same calendar day, OR
        // "yesterday {timeOfDay}" if the test runs in the first 3 hours after
        // midnight. The intent is that SOME temporal phrase appears — the
        // exact phrase varies with current local hour. Without the yesterday
        // clause this test was midnight-flaky (2026-05-18 ~00:00 runner
        // failures).
        var hasTemporalPrefix = user.Contains("earlier this") || user.Contains("this morning") ||
                                user.Contains("this afternoon") || user.Contains("this evening") ||
                                user.Contains("this late") || user.Contains("a little while ago") ||
                                user.Contains("yesterday");
        hasTemporalPrefix.Should().BeTrue(
            "Recent thoughts in the reconsideration prompt should carry time so the 'one more thing' framing is anchored.");
    }

    [Fact]
    public void FormatMemoryWithTime_J3_ProducesExpectedTemporalPhrases()
    {
        // Pin the canonical FormatMemoryWithTime behaviour so future changes
        // to the function are detected by test failure rather than silent
        // drift in prompt-builder output.
        var now = new DateTimeOffset(2026, 04, 27, 12, 0, 0, TimeSpan.Zero);

        var justNow = new MemoryRecord { Content = "test", OccurredAt = now.AddMinutes(-10) };
        PromptBuilder.FormatMemoryWithTime(justNow, now).Should().StartWith("(just now)");

        var aLittleWhileAgo = new MemoryRecord { Content = "test", OccurredAt = now.AddMinutes(-45) };
        PromptBuilder.FormatMemoryWithTime(aLittleWhileAgo, now).Should().StartWith("(a little while ago)");

        var fourDaysAgo = new MemoryRecord { Content = "test", OccurredAt = now.AddDays(-4) };
        PromptBuilder.FormatMemoryWithTime(fourDaysAgo, now).Should().StartWith("(4 days ago)");

        var twoWeeksAgo = new MemoryRecord { Content = "test", OccurredAt = now.AddDays(-15) };
        PromptBuilder.FormatMemoryWithTime(twoWeeksAgo, now).Should().StartWith("(2 weeks ago)");
    }

    // ── May 3, 2026: WHAT IS TRUE → Verified facts rephrase regression tests
    // ─────────────────────────────────────────────────────────────────────
    // Pin that no live prompt text uses the old *"WHAT IS TRUE"* directive
    // header. The Apr 30 leak was Ani paraphrasing the directive into output
    // (*"so here's what true: mark has a desk..."*). Replacing with neutral
    // *"[FACTS]"* removes the paraphrase pattern. These tests fail
    // closed if anyone reverts the rename.

    [Fact]
    public void BuildLeanConversationPrompt_DoesNotUseWhatIsTrueDirective()
    {
        var snapshot = MinimalSnapshot();
        var thread = new ConversationThread { Id = Guid.NewGuid(), InitiatedBy = Roles.Mark };

        var (system, user) = PromptBuilder.BuildLeanConversationPrompt(snapshot, thread);

        system.Should().NotContain("WHAT IS TRUE");
        user.Should().NotContain("WHAT IS TRUE");
        user.Should().Contain("[FACTS]");
    }

    [Fact]
    public void BuildConversationReplyPrompt_DoesNotUseWhatIsTrueDirective()
    {
        var snapshot = MinimalSnapshot();
        var thread = new ConversationThread { Id = Guid.NewGuid(), InitiatedBy = Roles.Mark };

        var (system, user) = PromptBuilder.BuildConversationReplyPrompt(snapshot, thread);

        system.Should().NotContain("WHAT IS TRUE");
        user.Should().NotContain("WHAT IS TRUE");
        user.Should().Contain("[FACTS]");
    }

    [Fact]
    public void BuildOutreachMessagePrompt_DoesNotUseWhatIsTrueDirective()
    {
        var snapshot = MinimalSnapshot();
        var (system, user) = PromptBuilder.BuildOutreachMessagePrompt(
            snapshot, "rain on the window", reasoning: "", reasoningInComposition: false);

        system.Should().NotContain("WHAT IS TRUE");
        user.Should().NotContain("WHAT IS TRUE");
        user.Should().Contain("[FACTS]");
    }

    // ── May 3, 2026: AnchorReasonValues canonical taxonomy ──────────────────
    [Fact]
    public void AnchorReasonValues_HasFourCanonicalCategories()
    {
        AnchorReasonValues.All.Should().HaveCount(4);
        AnchorReasonValues.All.Should().Contain(new[]
        {
            AnchorReasonValues.Origin,
            AnchorReasonValues.Foundation,
            AnchorReasonValues.ArchitecturalGrowth,
            AnchorReasonValues.LessonsAsHistory,
        });
    }

    [Fact]
    public void AnchorReasonValues_IsValidOrNull_AcceptsCanonicalAndNull()
    {
        AnchorReasonValues.IsValidOrNull(null).Should().BeTrue();
        AnchorReasonValues.IsValidOrNull(AnchorReasonValues.Origin).Should().BeTrue();
        AnchorReasonValues.IsValidOrNull(AnchorReasonValues.Foundation).Should().BeTrue();
        AnchorReasonValues.IsValidOrNull(AnchorReasonValues.ArchitecturalGrowth).Should().BeTrue();
        AnchorReasonValues.IsValidOrNull(AnchorReasonValues.LessonsAsHistory).Should().BeTrue();
    }

    [Fact]
    public void AnchorReasonValues_IsValidOrNull_RejectsArbitraryStrings()
    {
        AnchorReasonValues.IsValidOrNull("highest pain + highest trust").Should().BeFalse();
        AnchorReasonValues.IsValidOrNull("foundation_v2").Should().BeFalse();
        AnchorReasonValues.IsValidOrNull("").Should().BeFalse();
    }

}
