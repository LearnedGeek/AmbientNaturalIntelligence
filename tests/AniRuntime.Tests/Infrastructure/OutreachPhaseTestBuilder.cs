using AniRuntime.Actions;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Emergence;
using AniRuntime.LLM;
using AniRuntime.Loops;
using AniRuntime.Loops.Coreference;
using AniRuntime.Loops.Outreach;
using LearnedGeek.ML;
using LearnedGeek.ML.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AniRuntime.Tests.Infrastructure;

/// <summary>
/// Wires up the full OutreachPhase tree (pipeline + reactive share +
/// silence recorder + thread recorder) from primitive collaborators. Test
/// classes that previously did <c>new OutreachPhase(memory, ollama, ...)</c>
/// now call this and pass the same primitive mocks they already build —
/// the rewiring is centralized so each test class's setup stays short.
///
/// <para>
/// Mirrors the pre-§5.3 OutreachPhase ctor's positional shape so updating
/// a test is mostly a search-and-replace from
/// <c>new OutreachPhase(...)</c> to <c>OutreachPhaseTestBuilder.Build(...)</c>.
/// </para>
/// </summary>
public static class OutreachPhaseTestBuilder
{
    public static OutreachPhase Build(
        IStateStore state,
        IMemoryPersistence persist,
        IMemorySearch search,
        IOllamaClient ollama,
        AniActionDispatcher dispatcher,
        DesireEngine desire,
        ClaimVerificationPhase claimVerifier,
        IConversationService conversations,
        IOptions<AniOptions> aniOptions,
        ITextClassificationService? mlClassifier = null,
        PersonaSummaryCache? personaCache = null,
        Em9Detector? em9Detector = null,
        ICognitiveOutputGate? outputGate = null,
        IVibeBiasService? vibeBias = null,
        IOutreachFrameSelector? frameSelector = null,
        IFrameCoherenceChecker? frameChecker = null,
        IClosedConversationStore? closedStore = null,
        IEpistemicSubstrateRenderer? epistemicRenderer = null)
    {
        var threadRecorder = new OutboundThreadRecorder(
            conversations, NullLogger<OutboundThreadRecorder>.Instance);

        var pipeline = new OutreachPipeline(
            state, persist, search, ollama, dispatcher, desire, claimVerifier,
            threadRecorder, aniOptions,
            NullLogger<OutreachPipeline>.Instance,
            mlClassifier, personaCache, em9Detector, outputGate, vibeBias,
            frameSelector, frameChecker, epistemicRenderer);

        var reactiveShare = new ReactiveShareService(
            state, ollama, dispatcher, desire, persist, threadRecorder, aniOptions,
            NullLogger<ReactiveShareService>.Instance,
            closedStore, frameSelector);

        var silenceRecorder = new SilenceChoiceRecorder(
            persist, NullLogger<SilenceChoiceRecorder>.Instance);

        return new OutreachPhase(
            pipeline, reactiveShare, silenceRecorder, threadRecorder, aniOptions,
            NullLogger<OutreachPhase>.Instance);
    }
}
