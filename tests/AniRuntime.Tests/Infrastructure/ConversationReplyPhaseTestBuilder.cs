using AniRuntime.Actions;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Emergence;
using AniRuntime.LLM;
using AniRuntime.Loops;
using LearnedGeek.ML;
using LearnedGeek.ML.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AniRuntime.Tests.Infrastructure;

/// <summary>
/// Wires up the post-§5.4b ConversationReplyPhase facade by first building
/// a <see cref="ConversationReplyPipeline"/> from primitive collaborators
/// and then wrapping it in the facade. Mirrors the pre-§5.4b ctor's
/// positional shape so test classes only change <c>new ConversationReplyPhase(...)</c>
/// → <c>ConversationReplyPhaseTestBuilder.Build(...)</c>.
///
/// <para>
/// <see cref="BuildPipeline"/> returns the underlying pipeline directly —
/// used by tests that pin the internal evaluation helpers (e.g.
/// <c>EvaluateAndRemediateReplyAsync</c>) which moved off the facade in §5.4b.
/// </para>
/// </summary>
public static class ConversationReplyPhaseTestBuilder
{
    public static ConversationReplyPhase Build(
        IStateStore state,
        IMemoryPersistence persist,
        IMemorySearch search,
        IMemoryAnalytics analytics,
        IOllamaClient ollama,
        IConversationService conversations,
        IReplyChannelResolver channels,
        AniActionDispatcher dispatcher,
        DesireEngine desire,
        EmotionalProcessor emotional,
        ContextBuilder contextBuilder,
        KeywordExtractor keywords,
        IIntentExtractor intent,
        IConversationGateState gateState,
        ContextCompressor compressor,
        ClaimVerificationPhase claimVerifier,
        IWithdrawalStateTracker withdrawal,
        IOptions<AniOptions> aniOptions,
        IOptions<OllamaOptions> ollamaOptions,
        ITextClassificationService? mlClassifier = null,
        PersonaSummaryCache? personaCache = null,
        Em9Detector? em9Detector = null,
        ICognitiveOutputGate? outputGate = null,
        IVibeBiasService? vibeBias = null,
        IConsciousSubstrateGist? consciousGist = null,
        IRecentGateTripTracker? gateTripTracker = null,
        IEpistemicSubstrateRenderer? epistemicRenderer = null)
    {
        var pipeline = BuildPipeline(
            state, persist, search, analytics, ollama, conversations, channels,
            dispatcher, desire, emotional, contextBuilder, keywords, intent,
            gateState, compressor, claimVerifier, withdrawal, aniOptions, ollamaOptions,
            mlClassifier, personaCache, em9Detector, outputGate, vibeBias,
            consciousGist, gateTripTracker, epistemicRenderer);

        return new ConversationReplyPhase(pipeline, withdrawal);
    }

    public static ConversationReplyPipeline BuildPipeline(
        IStateStore state,
        IMemoryPersistence persist,
        IMemorySearch search,
        IMemoryAnalytics analytics,
        IOllamaClient ollama,
        IConversationService conversations,
        IReplyChannelResolver channels,
        AniActionDispatcher dispatcher,
        DesireEngine desire,
        EmotionalProcessor emotional,
        ContextBuilder contextBuilder,
        KeywordExtractor keywords,
        IIntentExtractor intent,
        IConversationGateState gateState,
        ContextCompressor compressor,
        ClaimVerificationPhase claimVerifier,
        IWithdrawalStateTracker withdrawal,
        IOptions<AniOptions> aniOptions,
        IOptions<OllamaOptions> ollamaOptions,
        ITextClassificationService? mlClassifier = null,
        PersonaSummaryCache? personaCache = null,
        Em9Detector? em9Detector = null,
        ICognitiveOutputGate? outputGate = null,
        IVibeBiasService? vibeBias = null,
        IConsciousSubstrateGist? consciousGist = null,
        IRecentGateTripTracker? gateTripTracker = null,
        IEpistemicSubstrateRenderer? epistemicRenderer = null)
    {
        var postReply = new PostReplyEmotionalProcessor(
            emotional, persist, analytics, withdrawal, aniOptions,
            NullLogger<PostReplyEmotionalProcessor>.Instance);

        return new ConversationReplyPipeline(
            state, persist, search, analytics, ollama, conversations, channels,
            dispatcher, desire, postReply, contextBuilder, keywords, intent,
            gateState, compressor, claimVerifier, withdrawal, aniOptions, ollamaOptions,
            NullLogger<ConversationReplyPipeline>.Instance,
            mlClassifier, personaCache, em9Detector, outputGate, vibeBias,
            consciousGist, gateTripTracker, epistemicRenderer);
    }
}
