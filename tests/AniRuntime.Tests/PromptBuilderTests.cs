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
    public void BuildOutreachMessagePrompt_J2_FallsBackToProse_WhenStructuredAbsent()
    {
        // During the additive deploy window or on conversation-service failure
        // the structured form may be null. The prose form remains the
        // load-bearing surface in that case.
        var snapshot = MinimalSnapshot();
        snapshot.RecentConversationSummary = "Conversation (1 messages):\nMark: hi";
        snapshot.StructuredConversationSummary = null;

        var (_, user) = PromptBuilder.BuildOutreachMessagePrompt(
            snapshot,
            recentThought: "wandering",
            reasoning: "");

        user.Should().Contain("Conversation (1 messages)");
        user.Should().Contain("Mark: hi");
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
    public void BuildOutreachPrompt_J2_FallsBackToProse_WhenStructuredAbsent()
    {
        var snapshot = MinimalSnapshot();
        snapshot.RecentConversationSummary = "Conversation (1 messages):\nMark: hi";
        snapshot.StructuredConversationSummary = null;

        var (_, user) = PromptBuilder.BuildOutreachPrompt(
            snapshot, recentThought: "thinking about him");

        user.Should().Contain("You recently talked with Mark");
        user.Should().Contain("Conversation (1 messages)");
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
        // Kathy's middle name was Ann" reads wrong).
        var snapshot = MinimalSnapshot();
        snapshot.AnchoredMemories.Add(new MemoryRecord
        {
            Content    = "Kathy's middle name was Ann",
            Type       = MemoryType.Semantic,
            OccurredAt = DateTimeOffset.Now.AddYears(-2),
        });

        var (_, user) = PromptBuilder.BuildInnerThoughtPrompt(snapshot);

        user.Should().Contain("Kathy's middle name was Ann");
        user.Should().NotContain("years ago) Kathy",
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
        // 3h ago on the same day → "earlier this {timeOfDay}" or "this {timeOfDay}"
        // — either way the temporal prefix appears.
        var hasTemporalPrefix = user.Contains("earlier this") || user.Contains("this morning") ||
                                user.Contains("this afternoon") || user.Contains("this evening") ||
                                user.Contains("this late") || user.Contains("a little while ago");
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

}
