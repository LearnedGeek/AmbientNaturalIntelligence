using AniRuntime.Actions;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.LLM;
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
    private readonly Mock<IIntentExtractor>     _mockIntent        = new();
    private readonly Mock<IReplyChannelResolver> _mockChannelResolver = new();

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
        MockMemory.Setup(m => m.GetActiveContributionsAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new List<EmotionalContribution>());
        MockMemory.Setup(m => m.SaveEmotionalContributionAsync(It.IsAny<EmotionalContribution>(), It.IsAny<CancellationToken>()))
                  .Returns(Task.CompletedTask);
        MockMemory.Setup(m => m.CleanupDecayedContributionsAsync(It.IsAny<CancellationToken>()))
                  .Returns(Task.CompletedTask);
        MockMemory.Setup(m => m.GetProcessedThemesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new List<string>());
        MockMemory.Setup(m => m.GetFlaggedContradictionsAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new List<MemoryContradiction>());

        _mockIntent.Setup(i => i.ExtractIntentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync((string msg, CancellationToken _) => msg); // passthrough

        var mockChannel = new Mock<IReplyChannel>();
        mockChannel.Setup(c => c.ChannelId).Returns("sms");
        mockChannel.Setup(c => c.SendReplyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                   .Returns(Task.CompletedTask);
        _mockChannelResolver.Setup(r => r.Resolve(It.IsAny<string>())).Returns(mockChannel.Object);
        _mockChannelResolver.Setup(r => r.Default).Returns(mockChannel.Object);

        _mockSource.Setup(s => s.IsEnabled).Returns(true);
        _mockSource.Setup(s => s.SourceName).Returns("test-source");
        _mockSource.Setup(s => s.Category).Returns(PerceptionCategory.Environment);
        _mockSource.Setup(s => s.PollAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(Array.Empty<PerceptionEvent>());

        // ActionType must be set on the mock so AniActionDispatcher can build its lookup dictionary
        _mockSmsAction.Setup(a => a.ActionType).Returns(ActionTypes.Sms);

        var sources    = new[] { _mockSource.Object };
        var desire     = new DesireEngine(MockMemory.Object, MockMemory.Object, DefaultOptions, NullLogger<DesireEngine>.Instance);
        var dispatcher = new AniActionDispatcher(
            new[] { _mockSmsAction.Object },
            NullLogger<AniActionDispatcher>.Instance);

        _mockConversations.Setup(c => c.GetActiveThreadAsync(It.IsAny<CancellationToken>()))
                          .ReturnsAsync((ConversationThread?)null);

        var adminHandler = new AdminCommandHandler(
            MockMemory.Object, MockMemory.Object,
            _mockConversations.Object,
            desire,
            dispatcher,
            DefaultOptions,
            NullLogger<AdminCommandHandler>.Instance);

        var emotional = new EmotionalProcessor(
            MockMemory.Object, MockMemory.Object, MockMemory.Object,
            MockOllama.Object, DefaultOptions,
            NullLogger<EmotionalProcessor>.Instance);
        var contextBuilder = new ContextBuilder(
            MockMemory.Object, MockMemory.Object, MockMemory.Object, MockMemory.Object,
            MockOllama.Object, desire, DefaultOptions,
            NullLogger<ContextBuilder>.Instance);
        var keywordExtractor = new KeywordExtractor(
            MockMemory.Object, NullLogger<KeywordExtractor>.Instance);
        var gateState = new ConversationGateState();
        var conversationReply = new ConversationReplyPhase(
            MockMemory.Object, MockMemory.Object, MockMemory.Object, MockMemory.Object,
            MockOllama.Object, _mockConversations.Object,
            _mockChannelResolver.Object, dispatcher, desire, emotional, contextBuilder, keywordExtractor,
            _mockIntent.Object, gateState, DefaultOptions, DefaultOllamaOptions,
            NullLogger<ConversationReplyPhase>.Instance);
        var outreach = new OutreachPhase(
            MockMemory.Object, MockMemory.Object, MockOllama.Object, dispatcher, desire, DefaultOptions,
            NullLogger<OutreachPhase>.Instance);
        var perception = new PerceptionPhase(
            sources, MockMemory.Object, NullLogger<PerceptionPhase>.Instance);
        var innerThought = new InnerThoughtPhase(
            MockOllama.Object, NullLogger<InnerThoughtPhase>.Instance);

        return new CognitiveCycleProcessor(
            MockMemory.Object,
            MockMemory.Object,
            MockMemory.Object,
            MockMemory.Object,
            desire,
            _mockConversations.Object,
            adminHandler,
            new NullEmergenceObserver(),
            emotional,
            contextBuilder,
            perception,
            innerThought,
            conversationReply,
            outreach,
            gateState,
            DefaultOptions,
            NullLogger<CognitiveCycleProcessor>.Instance);
    }

    [Fact]
    public async Task RunAsync_AlwaysSavesInnerThought()
    {
        MockOllama.Setup(o => o.InnerMonologueChatAsync(It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                                          It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
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
                                          It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
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
                      It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
                  .ReturnsAsync("Just a quiet thought.");
        MockOllama.SetupSequence(o => o.ChatJsonAsync(
                      It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                      It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("""{ "score": 0.3 }""")   // valence score (low — no spontaneous trigger)
                  .ReturnsAsync("""{ "warmth": 0.0, "energy": 0.0, "worry": 0.0, "playfulness": 0.0 }""")  // emotional shift
                  .ReturnsAsync("""{ "shouldReach": true, "confidence": 0.9, "reasoning": "been a while", "triggersActedOn": [] }""");   // outreach decision (no message — separate step now)

        // Step 2: message composition + Step 3: rewrite pass (both use ChatAsync)
        MockOllama.Setup(o => o.ChatAsync(
                      It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                      It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<float?>()))
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
                                          It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
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
                                          It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
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
    public void BuildLexicalAnchorContributions_HusbandTriggersContribution()
    {
        var charState = new CharacterStateDoc
        {
            LexicalAnchors = new List<LexicalAnchor>
            {
                new() { Word = "husband", WarmthDelta = 0.20f, EnergyDelta = 0.10f, WorryDelta = -0.05f, PlayfulnessDelta = 0.05f }
            }
        };

        var contributions = CognitiveCycleProcessor.BuildLexicalAnchorContributions("hey husband how are you", charState);

        contributions.Should().HaveCount(1);
        contributions[0].WarmthDelta.Should().BeGreaterThan(0);
        contributions[0].Category.Should().Be(ImpactCategory.Conversation);
    }

    [Fact]
    public void BuildLexicalAnchorContributions_NoMatchReturnsEmpty()
    {
        var charState = new CharacterStateDoc
        {
            LexicalAnchors = new List<LexicalAnchor>
            {
                new() { Word = "husband", WarmthDelta = 0.20f }
            }
        };

        var contributions = CognitiveCycleProcessor.BuildLexicalAnchorContributions("good morning!", charState);

        contributions.Should().BeEmpty();
    }

    [Fact]
    public void BuildLexicalAnchorContributions_DecaysOnRepetition_ReducesDeltaAfterThreshold()
    {
        var anchor = new LexicalAnchor
        {
            Word = "baby", WarmthDelta = 0.10f, DecaysOnRepetition = true, TimesHeard = 20
        };
        var charState = new CharacterStateDoc { LexicalAnchors = new List<LexicalAnchor> { anchor } };

        var decayed = CognitiveCycleProcessor.BuildLexicalAnchorContributions("hey baby", charState);

        // Reset for comparison — fresh anchor with no decay
        var freshAnchor = new LexicalAnchor
        {
            Word = "baby", WarmthDelta = 0.10f, DecaysOnRepetition = true, TimesHeard = 0
        };
        var freshState = new CharacterStateDoc { LexicalAnchors = new List<LexicalAnchor> { freshAnchor } };

        var fresh = CognitiveCycleProcessor.BuildLexicalAnchorContributions("hey baby", freshState);

        decayed[0].WarmthDelta.Should().BeLessThan(fresh[0].WarmthDelta,
            "repeated words should have reduced emotional impact");
    }

    [Fact]
    public void BuildLexicalAnchorContributions_CaseInsensitive()
    {
        var charState = new CharacterStateDoc
        {
            LexicalAnchors = new List<LexicalAnchor>
            {
                new() { Word = "Kathy", WarmthDelta = 0.05f, WorryDelta = 0.15f }
            }
        };

        var contributions = CognitiveCycleProcessor.BuildLexicalAnchorContributions("i was thinking about kathy today", charState);

        contributions.Should().HaveCount(1);
    }

    [Fact]
    public void BuildLexicalAnchorContributions_MultipleAnchorsInOneMessage()
    {
        var charState = new CharacterStateDoc
        {
            LexicalAnchors = new List<LexicalAnchor>
            {
                new() { Word = "husband", WarmthDelta = 0.20f },
                new() { Word = "Mia", WorryDelta = 0.10f }
            }
        };

        var contributions = CognitiveCycleProcessor.BuildLexicalAnchorContributions("hey husband, how's Mia doing?", charState);

        contributions.Should().HaveCount(2);
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
    public void GetSelfAwarenessPrompt_HighWarmthHighEnergy_ReturnsPrompt()
    {
        var state = new EmotionalState { Warmth = 0.80f, Energy = 0.70f };
        var prompt = state.GetSelfAwarenessPrompt();
        prompt.Should().NotBeNull();
        prompt.Should().Contain("bright");
    }

    [Fact]
    public void GetSelfAwarenessPrompt_LowWarmthLowEnergy_ReturnsPrompt()
    {
        var state = new EmotionalState { Warmth = 0.20f, Energy = 0.30f };
        var prompt = state.GetSelfAwarenessPrompt();
        prompt.Should().NotBeNull();
        prompt.Should().Contain("dim");
    }

    [Fact]
    public void GetSelfAwarenessPrompt_MultipleDimensions_MentionsComplex()
    {
        var state = new EmotionalState { Warmth = 0.80f, Energy = 0.70f, Playfulness = 0.80f };
        var prompt = state.GetSelfAwarenessPrompt();
        prompt.Should().Contain("complex mood"); // bright + funny = compound
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
        CognitiveCycleProcessor.ContainsThirdPersonReference(message, "Mark")
            .Should().Be(shouldDetect, $"'{message}' should {(shouldDetect ? "" : "not ")}detect third-person");
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
        CognitiveCycleProcessor.ContainsThirdPersonReference(message, "Mark")
            .Should().BeFalse("she/her is valid when talking about others");
    }

    [Theory]
    [InlineData("the cat chased him around", true)]      // him = Mark in third person
    [InlineData("his smile is my favorite thing", true)] // his = Mark in third person
    [InlineData("theme park was fun", false)]             // "the" contains "he" but not " he "
    [InlineData("this rhythm is everything", false)]      // "this" contains "his" but not " his "
    public void PronounDetection_EdgeCases(string message, bool shouldDetect)
    {
        CognitiveCycleProcessor.ContainsThirdPersonReference(message, "Mark")
            .Should().Be(shouldDetect, $"edge case: '{message}'");
    }

    // ── Feature 6 extension: Name-as-subject detection ──

    [Theory]
    [InlineData("Mark can sit next to me", true)]         // name as subject — should flag
    [InlineData("Mark said he'd be home late", true)]     // name starts sentence
    [InlineData("Mark would love that", true)]            // name as subject
    [InlineData("i bet Mark is tired", true)]             // name after comma-like position
    [InlineData("Mark's smile is everything", true)]      // possessive name
    [InlineData("i told Mark about it", true)]            // name mid-sentence — triggers rewrite
    [InlineData("hey mark", false)]                       // name alone, no subject pattern
    [InlineData("bookmark this page", false)]             // "mark" inside another word
    [InlineData("you can sit next to me", false)]         // already second person
    public void PronounDetection_NameAsSubject(string message, bool shouldDetect)
    {
        CognitiveCycleProcessor.ContainsThirdPersonReference(message, "Mark")
            .Should().Be(shouldDetect, $"name-as-subject: '{message}'");
    }

    // ── Feature 14: Bidirectional confidence gate ──

    [Theory]
    [InlineData("remember when you said you loved pizza", true)]
    [InlineData("you told me you were from New York", true)]
    [InlineData("didn't you say something about that last time", true)]
    [InlineData("you mentioned your favorite book", true)]
    [InlineData("we talked about movies yesterday", true)]
    [InlineData("yesterday you were in a great mood", true)]
    [InlineData("hey how's it going", false)]
    [InlineData("what do you think about this", false)]
    [InlineData("I had pizza for dinner", false)]
    [InlineData("good morning!", false)]
    [InlineData("tell me a joke", false)]
    public void ContainsMemoryReferencingLanguage_DetectsClaimPatterns(string message, bool expected)
    {
        CognitiveCycleProcessor.ContainsMemoryReferencingLanguage(message)
            .Should().Be(expected, $"message: '{message}'");
    }

    [Theory]
    [InlineData("You Said you love hiking", true)]   // case-insensitive
    [InlineData("REMEMBER WHEN we went there", true)] // all caps
    [InlineData("You Told Me about your day", true)]  // mixed case
    public void ContainsMemoryReferencingLanguage_IsCaseInsensitive(string message, bool expected)
    {
        CognitiveCycleProcessor.ContainsMemoryReferencingLanguage(message)
            .Should().Be(expected, $"case insensitive: '{message}'");
    }

    // ── Feature 15 Layer 3: Contradiction grounding in full cycle ──

    [Fact]
    public async Task RunAsync_WhenContradictionsExistForRetrievedMemory_InjectsGroundingIntoReplyPrompt()
    {
        // Arrange: a conversation where Mark asks about books, but there's a
        // contradiction flagged between a soup memory and the retrieved context.
        var soupMemoryId = Guid.NewGuid();
        var bookMemoryId = Guid.NewGuid();

        var soupMemory = new MemoryRecord
        {
            Id = soupMemoryId, Type = MemoryType.Episodic,
            Content = "Mark said he loves french onion soup",
        };
        var bookMemory = new MemoryRecord
        {
            Id = bookMemoryId, Type = MemoryType.Semantic,
            Content = "Mark enjoys reading mythology books",
        };

        var thread = new ConversationThread
        {
            Id = Guid.NewGuid(),
            Messages = new List<ConversationMessage>
            {
                new() { Role = "mark", Content = "Which book are you reading right now?", SentAt = DateTimeOffset.UtcNow },
            },
        };

        // Reply decision: YES (default parse falls through to true)
        MockOllama.Setup(o => o.ChatJsonAsync(It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                                              It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("""{ "shouldReply": true, "reasoning": "they asked a question" }""");

        MockOllama.Setup(o => o.InnerMonologueChatAsync(It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                                              It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
                  .ReturnsAsync("thinking about what to read next");

        // Capture the reply prompt to verify grounding injection
        string? capturedUserPrompt = null;
        MockOllama.Setup(o => o.ChatAsync(It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                                           It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<float?>()))
                  .Callback<string, IEnumerable<ChatMessage>, string, CancellationToken, float?>(
                      (system, history, user, ct, temp) => capturedUserPrompt = user)
                  .ReturnsAsync("Oh I'm rereading The Odyssey right now!");

        var processor = CreateProcessor();

        // Override mocks AFTER CreateProcessor (which sets broader defaults)
        _mockConversations.Setup(c => c.GetActiveThreadAsync(It.IsAny<CancellationToken>()))
                          .ReturnsAsync(thread);
        _mockConversations.Setup(c => c.AddMessageAsync(It.IsAny<Guid>(), It.IsAny<ConversationMessage>(), It.IsAny<CancellationToken>()))
                          .Returns(Task.CompletedTask);

        // SearchAsync returns both memories (simulating retrieval contamination)
        MockMemory.Setup(m => m.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new[] { bookMemory, soupMemory });
        // AC1: Scored search — conversation reply now uses SearchWithScoresAsync
        // Both memories above confidence floor so contradiction test works
        MockMemory.Setup(m => m.SearchWithScoresAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new[] { new ScoredMemory(bookMemory, 0.8f, 0.7f), new ScoredMemory(soupMemory, 0.7f, 0.6f) });

        // Contradiction flagged: soup memory conflicts with prior context
        MockMemory.Setup(m => m.GetFlaggedContradictionsAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new List<MemoryContradiction>
                  {
                      new()
                      {
                          NewMemoryId = soupMemoryId,
                          ExistingMemoryId = Guid.NewGuid(),
                          NewContent = "Mark said he loves french onion soup",
                          ExistingContent = "Conversation about books and reading",
                          Reason = "different topics — soup vs books",
                          Similarity = 0.65f,
                          FlaggedAt = DateTimeOffset.UtcNow,
                      }
                  });

        await processor.RunAsync(CancellationToken.None);

        // Assert: the reply prompt should contain the contradiction grounding
        capturedUserPrompt.Should().NotBeNull("a reply should have been generated");
        capturedUserPrompt.Should().Contain("TOPIC GROUNDING",
            "contradiction grounding should be injected when retrieved memories have flagged conflicts");
        capturedUserPrompt.Should().Contain("soup",
            "the warning should reference the contradicting content");
    }

    [Fact]
    public async Task RunAsync_WhenNoContradictions_NoGroundingInReplyPrompt()
    {
        var bookMemory = new MemoryRecord
        {
            Id = Guid.NewGuid(), Type = MemoryType.Semantic,
            Content = "Mark enjoys reading mythology books",
        };

        var thread = new ConversationThread
        {
            Id = Guid.NewGuid(),
            Messages = new List<ConversationMessage>
            {
                new() { Role = "mark", Content = "What are you reading?", SentAt = DateTimeOffset.UtcNow },
            },
        };

        MockOllama.Setup(o => o.ChatJsonAsync(It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                                              It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("""{ "shouldReply": true, "reasoning": "question" }""");
        MockOllama.Setup(o => o.InnerMonologueChatAsync(It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                                              It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
                  .ReturnsAsync("thinking");

        string? capturedUserPrompt = null;
        MockOllama.Setup(o => o.ChatAsync(It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                                           It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<float?>()))
                  .Callback<string, IEnumerable<ChatMessage>, string, CancellationToken, float?>(
                      (system, history, user, ct, temp) => capturedUserPrompt = user)
                  .ReturnsAsync("Reading The Odyssey!");

        var processor = CreateProcessor();

        // Override mocks AFTER CreateProcessor
        _mockConversations.Setup(c => c.GetActiveThreadAsync(It.IsAny<CancellationToken>()))
                          .ReturnsAsync(thread);
        _mockConversations.Setup(c => c.AddMessageAsync(It.IsAny<Guid>(), It.IsAny<ConversationMessage>(), It.IsAny<CancellationToken>()))
                          .Returns(Task.CompletedTask);
        MockMemory.Setup(m => m.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new[] { bookMemory });
        MockMemory.Setup(m => m.SearchWithScoresAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new[] { new ScoredMemory(bookMemory, 0.8f, 0.7f) });

        await processor.RunAsync(CancellationToken.None);

        capturedUserPrompt.Should().NotBeNull("a reply should have been generated");
        capturedUserPrompt.Should().NotContain("TOPIC GROUNDING",
            "no grounding should be injected when no contradictions exist");
    }

    // ── EndsWithDirectQuestion tests ─────────────────────────────────────

    [Theory]
    [InlineData("You think I would like it?", true)]
    [InlineData("How's your day going?", true)]
    [InlineData("what do you think?", true)]
    [InlineData("haha right?", true)]
    [InlineData("I wonder if that's true?", true)]
    [InlineData("what!? that's amazing!", false)]  // last sentence is exclamation
    [InlineData("what!? congratulations!", false)]  // last sentence is exclamation
    [InlineData("Ha! My carrot nose was all red but I'm warming up now.", false)]
    [InlineData("haha", false)]
    [InlineData("ok", false)]
    [InlineData("That sounds great!", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("Really? That's so cool! I love it.", false)]  // question mid-message, ends with statement
    [InlineData("Really? That's so cool! Don't you think?", true)]  // ends with question
    [InlineData("Hey baby! Wow snowy blowy today!", false)]
    public void EndsWithDirectQuestion_ClassifiesCorrectly(string message, bool expected)
    {
        CognitiveCycleProcessor.EndsWithDirectQuestion(message)
            .Should().Be(expected, $"message: \"{message}\"");
    }

    // ── UP1: ContainsFalseConfidenceClaim tests ──────────────────────────

    [Theory]
    [InlineData("of course I knew that, I was testing you!", true)]
    [InlineData("i totally knew about your brother", true)]
    [InlineData("i was just testing if you'd remember", true)]
    [InlineData("obviously i know that", true)]
    [InlineData("i knew all along, silly", true)]
    [InlineData("oh wait really? tell me more!", false)]
    [InlineData("hmm I don't think we've talked about that", false)]
    [InlineData("that's so cool! I didn't know that", false)]
    [InlineData("mmm… baby. learned geek?", false)]
    [InlineData("nope! i don't know it and i'm not going to pretend", false)]
    public void ContainsFalseConfidenceClaim_ClassifiesCorrectly(string reply, bool expected)
    {
        ConversationFeatureDetector.ContainsFalseConfidenceClaim(reply)
            .Should().Be(expected, $"reply: \"{reply}\"");
    }
}
