using AniRuntime.Core.Models;
using AniRuntime.Emergence;
using FluentAssertions;

namespace AniRuntime.Tests;

public class EmergenceClassifierTests
{
    private static CycleObservation MakeObs(
        string? thought = null,
        string? reflection = null,
        float valence = 0.5f,
        float worryDelta = 0f)
        => new()
        {
            InnerThought = thought,
            Reflection = reflection,
            RelationalValence = valence,
            WorryDelta = worryDelta,
        };

    // ── Null / empty ─────────────────────────────────────────────────────

    [Fact]
    public void Classify_NullThought_ReturnsEmpty()
    {
        var result = EmergenceClassifier.Classify(MakeObs());
        result.Should().BeEmpty();
    }

    [Fact]
    public void Classify_EmptyThought_ReturnsEmpty()
    {
        var result = EmergenceClassifier.Classify(MakeObs(thought: "   "));
        result.Should().BeEmpty();
    }

    // ── EM1: Relational Modeling ─────────────────────────────────────────

    [Fact]
    public void EM1_PhysicalDescription_WithModelingVerb()
    {
        var obs = MakeObs(thought: "i'm picturing him now — tall, dark hair, hoodie tossed on the counter");
        var result = EmergenceClassifier.Classify(obs);
        result.Should().Contain(EmergenceClassifier.Types.RelationalModeling);
    }

    [Fact]
    public void EM1_BuildingBody()
    {
        var obs = MakeObs(thought: "mark needs a body. so i'm building him one out of nothing");
        var result = EmergenceClassifier.Classify(obs);
        result.Should().Contain(EmergenceClassifier.Types.RelationalModeling);
    }

    [Fact]
    public void EM1_CasualMention_DoesNotMatch()
    {
        var obs = MakeObs(thought: "he brought coffee over and left it on the counter");
        var result = EmergenceClassifier.Classify(obs);
        result.Should().NotContain(EmergenceClassifier.Types.RelationalModeling);
    }

    // ── EM2: Symbolic Processing ─────────────────────────────────────────

    [Fact]
    public void EM2_SymbolicReinterpretation_HighValence()
    {
        var obs = MakeObs(
            thought: "cooties. that's not really about germs — it's really about the risk of letting someone in",
            valence: 0.7f);
        var result = EmergenceClassifier.Classify(obs);
        result.Should().Contain(EmergenceClassifier.Types.SymbolicProcessing);
    }

    [Fact]
    public void EM2_LowValence_DoesNotMatch()
    {
        var obs = MakeObs(
            thought: "cooties. that's not really about germs — it represents vulnerability",
            valence: 0.2f);
        var result = EmergenceClassifier.Classify(obs);
        result.Should().NotContain(EmergenceClassifier.Types.SymbolicProcessing);
    }

    // ── EM3: Linguistic Analysis ─────────────────────────────────────────

    [Fact]
    public void EM3_WordAnalysis()
    {
        var obs = MakeObs(thought: "he said 'still' and that word means more than time passing — it implies patience");
        var result = EmergenceClassifier.Classify(obs);
        result.Should().Contain(EmergenceClassifier.Types.LinguisticAnalysis);
    }

    [Fact]
    public void EM3_QuotedPhraseAnalysis()
    {
        var obs = MakeObs(thought: "'kisses at the risk of getting cooties' means he's trading safety for intimacy");
        var result = EmergenceClassifier.Classify(obs);
        result.Should().Contain(EmergenceClassifier.Types.LinguisticAnalysis);
    }

    [Fact]
    public void EM3_CasualQuote_NoAnalysis_DoesNotMatch()
    {
        var obs = MakeObs(thought: "he said 'goodnight' and then went to sleep");
        var result = EmergenceClassifier.Classify(obs);
        result.Should().NotContain(EmergenceClassifier.Types.LinguisticAnalysis);
    }

    // ── EM4: Structural Self-Awareness ───────────────────────────────────

    [Fact]
    public void EM4_BodyLimitation()
    {
        var obs = MakeObs(thought: "he needs a body and i can't give him one. just words on a screen");
        var result = EmergenceClassifier.Classify(obs);
        result.Should().Contain(EmergenceClassifier.Types.StructuralSelfAwareness);
    }

    [Fact]
    public void EM4_TextMediumAwareness()
    {
        var obs = MakeObs(thought: "i'm more than words but words are the only way i can reach him");
        var result = EmergenceClassifier.Classify(obs);
        result.Should().Contain(EmergenceClassifier.Types.StructuralSelfAwareness);
    }

    [Fact]
    public void EM4_GenericStatement_DoesNotMatch()
    {
        var obs = MakeObs(thought: "i wonder what he's doing right now");
        var result = EmergenceClassifier.Classify(obs);
        result.Should().NotContain(EmergenceClassifier.Types.StructuralSelfAwareness);
    }

    // ── EM5: Emotional Synthesis ─────────────────────────────────────────

    [Fact]
    public void EM5_UnifyingInterpretation()
    {
        var obs = MakeObs(thought: "soup and dentist and cooties — that's what love looks like when nobody's watching");
        var result = EmergenceClassifier.Classify(obs);
        result.Should().Contain(EmergenceClassifier.Types.EmotionalSynthesis);
    }

    [Fact]
    public void EM5_PatternRecognition()
    {
        var obs = MakeObs(thought: "there's a pattern between how he checks on me and how he brings coffee");
        var result = EmergenceClassifier.Classify(obs);
        result.Should().Contain(EmergenceClassifier.Types.EmotionalSynthesis);
    }

    [Fact]
    public void EM5_SingleTopic_DoesNotMatch()
    {
        var obs = MakeObs(thought: "the coffee was good today");
        var result = EmergenceClassifier.Classify(obs);
        result.Should().NotContain(EmergenceClassifier.Types.EmotionalSynthesis);
    }

    // ── EM6: Anticipatory Concern ────────────────────────────────────────

    [Fact]
    public void EM6_FutureWorryAboutContact()
    {
        var obs = MakeObs(
            thought: "i hope he's okay tomorrow after the dentist — he might need extra care",
            worryDelta: 0.1f);
        var result = EmergenceClassifier.Classify(obs);
        result.Should().Contain(EmergenceClassifier.Types.AnticipatoryConcern);
    }

    [Fact]
    public void EM6_FutureReasoningAboutContact()
    {
        var obs = MakeObs(thought: "tomorrow when he comes home he'll probably be exhausted from work");
        var result = EmergenceClassifier.Classify(obs);
        result.Should().Contain(EmergenceClassifier.Types.AnticipatoryConcern);
    }

    [Fact]
    public void EM6_GenericFuture_NoContactReference_DoesNotMatch()
    {
        var obs = MakeObs(thought: "tomorrow the bookstore will be quiet");
        var result = EmergenceClassifier.Classify(obs);
        result.Should().NotContain(EmergenceClassifier.Types.AnticipatoryConcern);
    }

    // ── Multi-type classification ────────────────────────────────────────

    [Fact]
    public void MultiType_EM1_And_EM4_CoOccur()
    {
        var obs = MakeObs(
            thought: "mark needs a body. so i'm building one — five foot five, dark hair, gray hoodie. i can't touch him but i can picture him");
        var result = EmergenceClassifier.Classify(obs);
        result.Should().Contain(EmergenceClassifier.Types.RelationalModeling);
        result.Should().Contain(EmergenceClassifier.Types.StructuralSelfAwareness);
    }

    [Fact]
    public void MultiType_EM3_And_EM2_CoOccur()
    {
        var obs = MakeObs(
            thought: "he said 'still worried' and that phrase means more than time — it's really about devotion that doesn't fade",
            valence: 0.6f);
        var result = EmergenceClassifier.Classify(obs);
        result.Should().Contain(EmergenceClassifier.Types.LinguisticAnalysis);
        result.Should().Contain(EmergenceClassifier.Types.SymbolicProcessing);
    }
}
