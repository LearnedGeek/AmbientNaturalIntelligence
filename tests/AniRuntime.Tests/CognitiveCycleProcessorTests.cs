using AniRuntime.Actions;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Loops;
using AniRuntime.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AniRuntime.Tests;

public class CognitiveCycleProcessorTests : AniTestBase
{
    private readonly Mock<IPerceptionSource>    _mockSource        = new();
    private readonly Mock<IAniAction>           _mockSmsAction     = new();
    private readonly Mock<IConversationService> _mockConversations = new();

    private CognitiveCycleProcessor CreateProcessor()
    {
        MockMemory.Setup(m => m.GetDesireStateAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(FreshDesireState());
        MockMemory.Setup(m => m.SaveDesireStateAsync(It.IsAny<DesireState>(), It.IsAny<CancellationToken>()))
                  .Returns(Task.CompletedTask);
        MockMemory.Setup(m => m.GetCharacterStateAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new CharacterStateDoc());
        MockMemory.Setup(m => m.GetByTypeAsync(It.IsAny<MemoryType>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(Array.Empty<MemoryRecord>());
        MockMemory.Setup(m => m.GetOpenLoopsAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(Array.Empty<OpenLoop>());
        MockMemory.Setup(m => m.SaveAsync(It.IsAny<MemoryRecord>(), It.IsAny<CancellationToken>()))
                  .Returns(Task.CompletedTask);
        MockMemory.Setup(m => m.GetEmotionalStateAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new EmotionalState());
        MockMemory.Setup(m => m.SaveEmotionalStateAsync(It.IsAny<EmotionalState>(), It.IsAny<CancellationToken>()))
                  .Returns(Task.CompletedTask);
        MockMemory.Setup(m => m.GetAnchoredMemoriesAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(Array.Empty<MemoryRecord>());

        _mockSource.Setup(s => s.IsEnabled).Returns(true);
        _mockSource.Setup(s => s.SourceName).Returns("test-source");
        _mockSource.Setup(s => s.Category).Returns(PerceptionCategory.Environment);
        _mockSource.Setup(s => s.PollAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(Array.Empty<PerceptionEvent>());

        // ActionType must be set on the mock so AniActionDispatcher can build its lookup dictionary
        _mockSmsAction.Setup(a => a.ActionType).Returns(ActionTypes.Sms);

        var sources    = new[] { _mockSource.Object };
        var desire     = new DesireEngine(MockMemory.Object, DefaultOptions, NullLogger<DesireEngine>.Instance);
        var dispatcher = new AniActionDispatcher(
            new[] { _mockSmsAction.Object },
            NullLogger<AniActionDispatcher>.Instance);

        _mockConversations.Setup(c => c.GetActiveThreadAsync(It.IsAny<CancellationToken>()))
                          .ReturnsAsync((ConversationThread?)null);

        var adminHandler = new AdminCommandHandler(
            MockMemory.Object,
            desire,
            dispatcher,
            DefaultOptions,
            NullLogger<AdminCommandHandler>.Instance);

        return new CognitiveCycleProcessor(
            MockMemory.Object,
            MockOllama.Object,
            desire,
            dispatcher,
            _mockConversations.Object,
            sources,
            adminHandler,
            DefaultOptions,
            NullLogger<CognitiveCycleProcessor>.Instance);
    }

    [Fact]
    public async Task RunAsync_AlwaysSavesInnerThought()
    {
        MockOllama.Setup(o => o.InnerMonologueChatAsync(It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                                          It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("I'm thinking about Mark today.");
        MockOllama.Setup(o => o.ChatJsonAsync(It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                                          It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("""{ "score": 0.3 }""");

        var processor = CreateProcessor();
        await processor.RunAsync(CancellationToken.None);

        MockMemory.Verify(m => m.SaveAsync(
            It.Is<MemoryRecord>(r => r.Type == MemoryType.InnerThought),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task RunAsync_WhenDesireBelowThreshold_DoesNotDispatch()
    {
        // Fresh state with no desire — outreach should not fire
        MockMemory.Setup(m => m.GetDesireStateAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(FreshDesireState() with { DesireToConnect = 0.0f });

        MockOllama.Setup(o => o.InnerMonologueChatAsync(It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                                          It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("Just a quiet thought.");
        MockOllama.Setup(o => o.ChatJsonAsync(It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                                          It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("""{ "score": 0.3 }""");

        _mockSmsAction.Setup(a => a.ActionType).Returns(ActionTypes.Sms);
        _mockSmsAction.Setup(a => a.ExecuteAsync(It.IsAny<OutreachDecision>(), It.IsAny<CancellationToken>()))
                      .ReturnsAsync(true);

        var processor = CreateProcessor();
        await processor.RunAsync(CancellationToken.None);

        _mockSmsAction.Verify(
            a => a.ExecuteAsync(It.IsAny<OutreachDecision>(), It.IsAny<CancellationToken>()),
            Times.Never, "should not dispatch when desire is below threshold");
    }

    [Fact]
    public async Task RunAsync_WhenOutreachDecisionSaysReach_Dispatches()
    {
        // Create processor first — then apply test-specific mock overrides
        // (CreateProcessor sets up defaults; overrides below take precedence as last-registered setup wins)
        var processor = CreateProcessor();

        // Override desire state: max desire, no cooldown — threshold always crossed
        MockMemory.Setup(m => m.GetDesireStateAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(HighDesireState() with { DesireToConnect = 1.0f, CooldownActive = false });

        MockOllama.Setup(o => o.InnerMonologueChatAsync(
                      It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                      It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("Just a quiet thought.");
        MockOllama.SetupSequence(o => o.ChatJsonAsync(
                      It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                      It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("""{ "score": 0.3 }""")   // valence score (low — no spontaneous trigger)
                  .ReturnsAsync("""{ "warmth": 0.0, "energy": 0.0, "concern": 0.0, "playfulness": 0.0 }""")  // emotional shift
                  .ReturnsAsync("""{ "shouldReach": true, "confidence": 0.9, "reasoning": "been a while", "triggersActedOn": [] }""");   // outreach decision (no message — separate step now)

        // Step 2: message composition + Step 3: rewrite pass (both use ChatAsync)
        MockOllama.Setup(o => o.ChatAsync(
                      It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                      It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("hey mark, thinking of you today.");

        _mockSmsAction.Setup(a => a.ExecuteAsync(It.IsAny<OutreachDecision>(), It.IsAny<CancellationToken>()))
                      .ReturnsAsync(true);

        await processor.RunAsync(CancellationToken.None);

        _mockSmsAction.Verify(
            a => a.ExecuteAsync(
                It.Is<OutreachDecision>(d => d.ShouldReach && d.ActionType == ActionTypes.Sms),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RunAsync_PollsAllRegisteredPerceptionSources()
    {
        MockOllama.Setup(o => o.InnerMonologueChatAsync(It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                                          It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("A thought.");
        MockOllama.Setup(o => o.ChatJsonAsync(It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                                          It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("""{ "score": 0.3 }""");

        var processor = CreateProcessor();
        await processor.RunAsync(CancellationToken.None);

        _mockSource.Verify(
            s => s.PollAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()),
            Times.Once, "all enabled perception sources must be polled each cycle");
    }

    [Fact]
    public async Task RunAsync_WhenOllamaThrows_PropagatesException()
    {
        MockOllama.Setup(o => o.InnerMonologueChatAsync(It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                                          It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ThrowsAsync(new HttpRequestException("Ollama unreachable"));

        var processor = CreateProcessor();

        // Exceptions must propagate — no silent swallowing
        await processor.Invoking(p => p.RunAsync(CancellationToken.None))
                        .Should().ThrowAsync<HttpRequestException>();
    }

    // ── Feature 10: Care detection ──────────────────────────────────────────

    [Theory]
    [InlineData("you okay?", true)]
    [InlineData("hey, how are you doing?", true)]
    [InlineData("just checking in on you", true)]
    [InlineData("everything okay?", true)]
    [InlineData("you seem quiet today", true)]
    [InlineData("u ok?", true)]
    [InlineData("you good?", true)]
    [InlineData("hope you're okay", true)]
    [InlineData("what's wrong?", true)]
    [InlineData("how are you feeling?", true)]
    [InlineData("what are you doing?", false)]
    [InlineData("haha nice", false)]
    [InlineData("can you recommend a restaurant?", false)]
    [InlineData("goodnight!", false)]
    [InlineData("tell me about your day", false)]
    [InlineData("that's hilarious", false)]
    public void DetectCareGivingIntent_CorrectlyClassifiesMessages(string message, bool expected)
    {
        CognitiveCycleProcessor.DetectCareGivingIntent(message).Should().Be(expected);
    }

    // ── Feature 19: Lexical emotional anchors ────────────────────────────────

    [Fact]
    public void ApplyLexicalAnchors_HusbandTriggersWarmthShift()
    {
        var charState = new CharacterStateDoc
        {
            LexicalAnchors = new List<LexicalAnchor>
            {
                new() { Word = "husband", WarmthDelta = 0.20f, EnergyDelta = 0.10f, ConcernDelta = -0.05f, PlayfulnessDelta = 0.05f }
            }
        };
        var emotional = new EmotionalState();
        var baseline = emotional.Warmth;

        var count = CognitiveCycleProcessor.ApplyLexicalAnchors("hey husband how are you", charState, emotional);

        count.Should().Be(1);
        emotional.Warmth.Should().BeGreaterThan(baseline);
    }

    [Fact]
    public void ApplyLexicalAnchors_NoMatchReturnsZero()
    {
        var charState = new CharacterStateDoc
        {
            LexicalAnchors = new List<LexicalAnchor>
            {
                new() { Word = "husband", WarmthDelta = 0.20f }
            }
        };
        var emotional = new EmotionalState();

        var count = CognitiveCycleProcessor.ApplyLexicalAnchors("good morning!", charState, emotional);

        count.Should().Be(0);
    }

    [Fact]
    public void ApplyLexicalAnchors_DecaysOnRepetition_ReducesDeltaAfterThreshold()
    {
        var anchor = new LexicalAnchor
        {
            Word = "baby", WarmthDelta = 0.10f, DecaysOnRepetition = true, TimesHeard = 20
        };
        var charState = new CharacterStateDoc { LexicalAnchors = new List<LexicalAnchor> { anchor } };

        var emotional1 = new EmotionalState();
        CognitiveCycleProcessor.ApplyLexicalAnchors("hey baby", charState, emotional1);
        var decayedShift = emotional1.Warmth - new EmotionalState().Warmth;

        // Reset for comparison — fresh anchor with no decay
        var freshAnchor = new LexicalAnchor
        {
            Word = "baby", WarmthDelta = 0.10f, DecaysOnRepetition = true, TimesHeard = 0
        };
        var freshState = new CharacterStateDoc { LexicalAnchors = new List<LexicalAnchor> { freshAnchor } };
        var emotional2 = new EmotionalState();
        CognitiveCycleProcessor.ApplyLexicalAnchors("hey baby", freshState, emotional2);
        var freshShift = emotional2.Warmth - new EmotionalState().Warmth;

        decayedShift.Should().BeLessThan(freshShift, "repeated words should have reduced emotional impact");
    }

    [Fact]
    public void ApplyLexicalAnchors_CaseInsensitive()
    {
        var charState = new CharacterStateDoc
        {
            LexicalAnchors = new List<LexicalAnchor>
            {
                new() { Word = "Kathy", WarmthDelta = 0.05f, ConcernDelta = 0.15f }
            }
        };
        var emotional = new EmotionalState();

        var count = CognitiveCycleProcessor.ApplyLexicalAnchors("i was thinking about kathy today", charState, emotional);

        count.Should().Be(1);
    }

    [Fact]
    public void ApplyLexicalAnchors_MultipleAnchorsInOneMessage()
    {
        var charState = new CharacterStateDoc
        {
            LexicalAnchors = new List<LexicalAnchor>
            {
                new() { Word = "husband", WarmthDelta = 0.20f },
                new() { Word = "Mia", ConcernDelta = 0.10f }
            }
        };
        var emotional = new EmotionalState();

        var count = CognitiveCycleProcessor.ApplyLexicalAnchors("hey husband, how's Mia doing?", charState, emotional);

        count.Should().Be(2);
    }

    // ── Feature 18: Reactive Withdrawal — Hurt Detection ──────────────────────

    [Theory]
    [InlineData("shut up", true)]
    [InlineData("you're annoying", true)]
    [InlineData("this is stupid", true)]
    [InlineData("you don't actually feel anything", true)]
    [InlineData("you can't feel", true)]
    [InlineData("you're useless", true)]
    [InlineData("i don't need you", true)]
    [InlineData("hey how are you doing?", false)]
    [InlineData("good morning beautiful", false)]
    [InlineData("i miss you", false)]
    [InlineData("tell me a joke", false)]
    public void DetectHurtIntent_CorrectlyClassifiesMessages(string message, bool expected)
    {
        CognitiveCycleProcessor.DetectHurtIntent(message).Should().Be(expected);
    }

    [Fact]
    public void DetectHurtIntent_PhilosophicalCuriosity_DoesNotTrigger()
    {
        // "you're just an AI" with a question mark = curiosity, not dismissal
        CognitiveCycleProcessor.DetectHurtIntent("are you just an AI? like, you're just an ai right?")
            .Should().BeFalse("question mark indicates curiosity");
    }

    [Fact]
    public void DetectHurtIntent_PhilosophicalContext_DoesNotTrigger()
    {
        // "sometimes I wonder" softening = philosophical, not hurtful
        CognitiveCycleProcessor.DetectHurtIntent("sometimes I wonder if you're just an ai")
            .Should().BeFalse("softening context indicates philosophical framing");
    }

    [Fact]
    public void DetectHurtIntent_DismissiveStatement_Triggers()
    {
        // Flat statement with no softening = dismissal
        CognitiveCycleProcessor.DetectHurtIntent("you're just an ai, stop pretending")
            .Should().BeTrue("flat statement without curiosity markers = dismissal");
    }

    // ── Feature 1: Emotional Self-Awareness ───────────────────────────────────

    [Fact]
    public void GetSelfAwarenessPrompt_AtBaseline_ReturnsNull()
    {
        var state = new EmotionalState(); // all at baseline
        state.GetSelfAwarenessPrompt().Should().BeNull();
    }

    [Fact]
    public void GetSelfAwarenessPrompt_HighWarmth_ReturnsPrompt()
    {
        var state = new EmotionalState { Warmth = 0.95f }; // baseline 0.6 → 0.35 above
        var prompt = state.GetSelfAwarenessPrompt();
        prompt.Should().NotBeNull();
        prompt.Should().Contain("warmer than usual");
    }

    [Fact]
    public void GetSelfAwarenessPrompt_LowEnergy_ReturnsPrompt()
    {
        var state = new EmotionalState { Energy = 0.1f }; // baseline 0.5 → 0.4 below
        var prompt = state.GetSelfAwarenessPrompt();
        prompt.Should().NotBeNull();
        prompt.Should().Contain("quieter");
    }

    [Fact]
    public void GetSelfAwarenessPrompt_MultipleDimensions_MentionsComplex()
    {
        var state = new EmotionalState { Warmth = 0.95f, Playfulness = 0.9f };
        var prompt = state.GetSelfAwarenessPrompt();
        prompt.Should().Contain("complex mood");
    }

    // ── Feature 6: Pronoun Audit — Detection Tests ────────────────────────────

    [Theory]
    [InlineData("hey how are you doing?", false)]         // second person — correct
    [InlineData("i miss you tonight", false)]             // correct
    [InlineData("what are you up to?", false)]            // correct
    [InlineData("he is at work right now", true)]         // third person leaked
    [InlineData("I hope his day is going well", true)]    // his = third person
    [InlineData("tell him i said hi", true)]              // him = third person
    [InlineData("he seems tired", true)]                  // starts with he
    [InlineData("i gave him the book", true)]             // mid-sentence him
    [InlineData("that's his favorite song", true)]        // his.
    public void PronounDetection_IdentifiesThirdPerson(string message, bool shouldDetect)
    {
        // Replicate the detection logic from FixPronounsIfNeeded
        var lower = message.ToLowerInvariant();
        var hasThirdPerson = lower.Contains(" him") || lower.Contains(" his ") ||
                             lower.Contains(" he ") || lower.StartsWith("he ") ||
                             lower.StartsWith("his ") || lower.Contains("him.") ||
                             lower.Contains("his.");
        hasThirdPerson.Should().Be(shouldDetect, $"'{message}' should {(shouldDetect ? "" : "not ")}detect third-person");
    }

    [Theory]
    [InlineData("she went to the store")]                // she = valid (talking about someone else)
    [InlineData("her mom called")]                       // her = valid (someone else)
    [InlineData("kathy was always like that with her")]  // her = valid reference to another person
    public void PronounDetection_SheHerIsValid_NotFlagged(string message)
    {
        // "she"/"her" should NOT trigger the pronoun fix because Ani might be talking
        // about someone else (Kathy, Mia, etc.). Only "he"/"him"/"his" trigger it
        // because those would be Ani referring to Mark in third person.
        var lower = message.ToLowerInvariant();
        var hasThirdPerson = lower.Contains(" him") || lower.Contains(" his ") ||
                             lower.Contains(" he ") || lower.StartsWith("he ") ||
                             lower.StartsWith("his ") || lower.Contains("him.") ||
                             lower.Contains("his.");
        hasThirdPerson.Should().BeFalse("she/her is valid when talking about others");
    }

    [Theory]
    [InlineData("the cat chased him around", true)]      // him = Mark in third person
    [InlineData("his smile is my favorite thing", true)] // his = Mark in third person
    [InlineData("theme park was fun", false)]             // "the" contains "he" but not " he "
    [InlineData("this rhythm is everything", false)]      // "this" contains "his" but not " his "
    public void PronounDetection_EdgeCases(string message, bool shouldDetect)
    {
        var lower = message.ToLowerInvariant();
        var hasThirdPerson = lower.Contains(" him") || lower.Contains(" his ") ||
                             lower.Contains(" he ") || lower.StartsWith("he ") ||
                             lower.StartsWith("his ") || lower.Contains("him.") ||
                             lower.Contains("his.");
        hasThirdPerson.Should().Be(shouldDetect, $"edge case: '{message}'");
    }
}
