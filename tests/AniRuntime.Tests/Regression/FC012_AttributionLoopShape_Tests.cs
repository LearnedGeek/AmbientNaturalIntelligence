using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.LLM;
using AniRuntime.Memory;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace AniRuntime.Tests.Regression;

/// <summary>
/// Regression scenarios for <b>FC-012 — Own-output substrate feedback pronoun-slip
/// (the 11:29→14:03 loop shape)</b>. Pins the F-2 Phase 1 P7 end-to-end
/// invariants that prevent the 12:04 misattribution mechanism from recurring.
///
/// <para>
/// THE PRODUCTION CASE (empirical anchor 2026-08-20):
/// <list type="bullet">
///   <item>11:29 — Ani dispatches SMS reply "mmm baby...". SqliteConversationService
///     persists as Ani-role conversation-message + Ani-authored Episodic MemoryRecord.</item>
///   <item>11:56 — Next inner-thought cycle retrieves that Episodic record from the
///     scored substrate pool.</item>
///   <item>12:04 — Composer LLM, seeing an Episodic line with no attribution tag,
///     pronoun-slips: the "I said to Mark: 'mmm baby'" surface reads as a Mark
///     utterance ("you said mmm baby"). Ani's own words come back attributed to
///     the wrong speaker.</item>
///   <item>14:03 — The pronoun-slip compounds; a full replay of the corrupt
///     framing dispatches.</item>
/// </list>
/// The load-bearing gap was that MemoryRecord had no first-class attribution
/// field — retrieval-render could label the FROM: source (episodic / conversation)
/// but not the AUTHOR of the record. All Episodic content of the shape
/// <c>"I said to Mark: '...'"</c> vs <c>"Mark said: '...'"</c> was disambiguated
/// only by prose prefix. Under compression / retrieval framing, the prose prefix
/// could be dropped or misread.
/// </para>
///
/// <para>
/// THE FIX (F-2 Phase 1):
/// <list type="number">
///   <item>P2 — Schema: five attribution columns on the memories table.</item>
///   <item>P4 — Render: <c>[FROM: X | AUTHORED: Y (| TRUST: Z)]</c> tag shape.</item>
///   <item>P6 — Producer wiring: nine emit sites populate AttributionTriple at
///     write time (including the SqliteConversationService.AddMessageAsync path
///     for both Mark and Ani roles).</item>
/// </list>
/// P7 (this file) pins the end-to-end: the invariants at each layer that,
/// together, prevent the 12:04 shape from recurring. If ANY layer regresses,
/// this suite fails loud so the fix isn't reintroduced piecemeal.
/// </para>
///
/// <para>
/// TEST CATEGORY: SPEC + integration. All tests pass today post-P6 merge;
/// failure of any assertion indicates a producer or render surface has drifted.
/// </para>
/// </summary>
public class FC012_AttributionLoopShape_Tests : IDisposable
{
    // ── Real SqliteConversationService + strict mocks (mirrors SqliteConversationServiceTests) ──
    private readonly Mock<IMemoryService>                _memory      = new(MockBehavior.Strict);
    private readonly Mock<IClosedConversationSummarizer> _summarizer  = new(MockBehavior.Strict);
    private readonly Mock<IClosedConversationStore>      _closedStore = new(MockBehavior.Strict);
    private readonly SqliteConversationService           _svc;

    public FC012_AttributionLoopShape_Tests()
    {
        var dbName  = $"ani-fc012-test-{Guid.NewGuid():N}";
        var options = Options.Create(new AniOptions { MemoryDbPath = dbName });
        _svc = new SqliteConversationService(
            options, _memory.Object,
            _summarizer.Object, _closedStore.Object,
            NullLogger<SqliteConversationService>.Instance);
    }

    public void Dispose() => _svc.Dispose();

    private async Task<ConversationThread> NewActiveThreadAsync()
    {
        var thread = new ConversationThread
        {
            InitiatedBy   = Roles.Mark,
            StartedAt     = DateTimeOffset.UtcNow,
            LastMessageAt = DateTimeOffset.UtcNow,
        };
        await _svc.SaveThreadAsync(thread);
        return thread;
    }

    // ─────────────────────────────────────────────────────────────────────
    // FC-012a — Producer wiring (Ani side)
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// FC-012a — SPEC: when Ani's SMS reply is persisted via
    /// <see cref="SqliteConversationService.AddMessageAsync"/>, the Episodic
    /// MemoryRecord handed to <see cref="IMemoryService.SaveAsync"/> MUST
    /// carry <c>AttributedTo=Ani</c>, <c>AttributionTrust="verified"</c>.
    ///
    /// This is the 11:29 emit-site invariant. Without it, the record that
    /// enters the retrieval substrate at 11:56 has no author signal and the
    /// 12:04 pronoun-slip loop reopens.
    /// </summary>
    [Fact]
    public async Task FC012a_AniSmsReply_PersistsWithAniAttribution()
    {
        var thread = await NewActiveThreadAsync();
        var sentAt = DateTimeOffset.UtcNow;

        _memory.Setup(m => m.GetCharacterStateAsync(It.IsAny<CancellationToken>()))
               .ReturnsAsync(new CharacterStateDoc { Name = "Ani", PrimaryContactName = "Mark" });
        var captured = new List<MemoryRecord>();
        _memory.Setup(m => m.SaveAsync(It.IsAny<MemoryRecord>(), It.IsAny<CancellationToken>()))
               .Callback<MemoryRecord, CancellationToken>((r, _) => captured.Add(r))
               .Returns(Task.CompletedTask);

        await _svc.AddMessageAsync(thread.Id, new ConversationMessage
        {
            Role    = Roles.Ani,
            Content = "FC012-FIXTURE: synthetic Ani reply body",
            SentAt  = sentAt,
        });

        var episodic = captured.Should().ContainSingle(
            "the Ani-role AddMessageAsync path must produce exactly one Episodic MemoryRecord "
          + "for the retrieval substrate — the 11:29 emit site").Subject;

        episodic.AttributedTo.Should().Be(AttributedTo.Ani,
            "an Ani-role conversation message is her verified composed reply; if this attributes "
          + "elsewhere the 12:04 pronoun-slip loop reopens at the emit surface");
        episodic.AttributionTrust.Should().Be("verified",
            "Ani's dispatched reply is a first-party verified utterance, not a heuristic guess");
        episodic.AttributedAt.Should().Be(sentAt,
            "AttributedAt must match the SentAt of the utterance so temporal render is honest");
    }

    // ─────────────────────────────────────────────────────────────────────
    // FC-012b — Producer wiring (Mark side)
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// FC-012b — SPEC: Mark's inbound SMS persisted via
    /// <see cref="SqliteConversationService.AddMessageAsync"/> attributes
    /// to Mark/verified with a conversation-scoped source descriptor. This
    /// is the OTHER half of the loop — if Mark's records misattribute to
    /// Ani, the same pronoun-slip surface appears from the opposite side.
    /// </summary>
    [Fact]
    public async Task FC012b_MarkSmsInbound_PersistsWithMarkAttribution()
    {
        var thread = await NewActiveThreadAsync();
        var sentAt = DateTimeOffset.UtcNow;

        _memory.Setup(m => m.GetCharacterStateAsync(It.IsAny<CancellationToken>()))
               .ReturnsAsync(new CharacterStateDoc { Name = "Ani", PrimaryContactName = "Mark" });
        var captured = new List<MemoryRecord>();
        _memory.Setup(m => m.SaveAsync(It.IsAny<MemoryRecord>(), It.IsAny<CancellationToken>()))
               .Callback<MemoryRecord, CancellationToken>((r, _) => captured.Add(r))
               .Returns(Task.CompletedTask);

        await _svc.AddMessageAsync(thread.Id, new ConversationMessage
        {
            Role    = Roles.Mark,
            Content = "FC012-FIXTURE: synthetic Mark inbound body",
            SentAt  = sentAt,
        });

        var episodic = captured.Should().ContainSingle().Subject;
        episodic.AttributedTo.Should().Be(AttributedTo.Mark);
        episodic.AttributionTrust.Should().Be("verified");
        episodic.AttributedSourceDescriptor.Should().StartWith("conversation:mark:",
            "descriptor must trace the conversation source-name so audit can find the exact turn");
    }

    // ─────────────────────────────────────────────────────────────────────
    // FC-012c — Retrieval render surface (defeats the 12:04 pronoun-slip)
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// FC-012c — SPEC: an Ani-authored Episodic record retrieved into the
    /// substrate MUST render with an explicit <c>[AUTHORED: Ani]</c> tag
    /// via <see cref="PromptBuilder.FormatMemoryWithTime"/>. This is the
    /// direct antidote to the 12:04 pronoun-slip: the composer LLM sees
    /// explicit "AUTHORED: Ani" alongside content that starts "I said to
    /// Mark: …" so it cannot confuse the utterance with a Mark inbound.
    /// </summary>
    [Fact]
    public void FC012c_AniEpisodic_RendersWithAuthoredAniTag()
    {
        var sentAt = DateTimeOffset.UtcNow.AddMinutes(-35);
        var triple = AttributionTriple.AniAt(sentAt);
        var record = new MemoryRecord
        {
            Content    = "I said to Mark: \"FC012-FIXTURE: synthetic Ani reply body\"",
            OccurredAt = sentAt,
            SourceName = "conversation",
            Provenance = EpistemicTier.Episodic,
            AttributedTo               = triple.AttributedTo,
            AttributedAt               = triple.AttributedAt,
            AttributedSourceRecordId   = triple.SourceRecordId,
            AttributedSourceDescriptor = triple.SourceDescriptor,
            AttributionTrust           = triple.Trust,
        };

        var rendered = PromptBuilder.FormatMemoryWithTime(record, DateTimeOffset.UtcNow);

        rendered.Should().Contain("AUTHORED: Ani",
            "the composer LLM needs an explicit author tag on Ani's own dispatched content; "
          + "without it, the 12:04 pronoun-slip surface returns — 'I said to Mark: X' looks "
          + "identical to a Mark inbound at retrieval-time");
        rendered.Should().NotContain("TRUST:",
            "verified is the default assumption; TRUST segment must be omitted to keep prompt noise low");
    }

    // ─────────────────────────────────────────────────────────────────────
    // FC-012d — CONTROL: pre-attribution-fix records surface as unverified
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// FC-012d — CONTROL: an Interior record whose internal content claims
    /// cannot be retroactively verified (i.e., a pre-F-2 record touched by
    /// the P3 backfill with <see cref="AttributionTriple.AniUnverifiedHistorical"/>)
    /// MUST render with an explicit <c>TRUST: unverified-historical</c>
    /// tag. This is the corrupt-substrate signal — legacy 12:04-shape
    /// records that survived into the retrieval pool must be flagged so
    /// the composer weights them with caution.
    ///
    /// This test pins the CORRUPT-SUBSTRATE path (backfill-tagged records)
    /// as distinct from FC-012c (fresh clean records). Both surfaces must
    /// remain distinguishable at render-time or the composer can't tell
    /// pre-fix from post-fix substrate apart.
    /// </summary>
    [Fact]
    public void FC012d_LegacyInteriorRecord_RendersWithUnverifiedHistoricalTrust()
    {
        var triple = AttributionTriple.AniUnverifiedHistorical();
        var record = new MemoryRecord
        {
            Content    = "FC012-FIXTURE: legacy inner-thought claiming 'you said mmm baby' — the 12:04 shape",
            OccurredAt = DateTimeOffset.UtcNow.AddDays(-30),
            Provenance = EpistemicTier.Interior,
            AttributedTo               = triple.AttributedTo,
            AttributedAt               = triple.AttributedAt,
            AttributedSourceRecordId   = triple.SourceRecordId,
            AttributedSourceDescriptor = triple.SourceDescriptor,
            AttributionTrust           = triple.Trust,
        };

        var rendered = PromptBuilder.FormatMemoryWithTime(record, DateTimeOffset.UtcNow);

        rendered.Should().Contain("AUTHORED: Ani",
            "Interior records are trivially Ani-authored (they ARE her self-model)");
        rendered.Should().Contain("TRUST: unverified-historical",
            "pre-F-2 Interior records may contain embedded pronoun-slip claims from the 12:04 "
          + "shape; TRUST: unverified-historical flags them so the composer down-weights them");
    }

    // ─────────────────────────────────────────────────────────────────────
    // FC-012e — End-to-end integration (producer → mocked persistence → render)
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// FC-012e — SPEC (end-to-end): the record that
    /// <see cref="SqliteConversationService.AddMessageAsync"/> hands to
    /// <see cref="IMemoryService.SaveAsync"/> MUST, when rendered by
    /// <see cref="PromptBuilder.FormatMemoryWithTime"/>, produce a substrate
    /// line with <c>[AUTHORED: Ani]</c>. This is the whole 11:29 → 11:56
    /// loop in one assertion: producer emits with correct attribution,
    /// render surface presents it correctly, no gap in between.
    ///
    /// If FC-012a passes and FC-012c passes but this fails, a middle layer
    /// (persistence-service field mapping, envelope repackaging) has silently
    /// dropped the attribution field.
    /// </summary>
    [Fact]
    public async Task FC012e_ProducerToRender_EndToEnd_AniAttributionSurvives()
    {
        var thread = await NewActiveThreadAsync();
        var sentAt = DateTimeOffset.UtcNow.AddMinutes(-27); // ~simulating 11:29 → 11:56 gap

        _memory.Setup(m => m.GetCharacterStateAsync(It.IsAny<CancellationToken>()))
               .ReturnsAsync(new CharacterStateDoc { Name = "Ani", PrimaryContactName = "Mark" });
        MemoryRecord? captured = null;
        _memory.Setup(m => m.SaveAsync(It.IsAny<MemoryRecord>(), It.IsAny<CancellationToken>()))
               .Callback<MemoryRecord, CancellationToken>((r, _) => captured = r)
               .Returns(Task.CompletedTask);

        await _svc.AddMessageAsync(thread.Id, new ConversationMessage
        {
            Role    = Roles.Ani,
            Content = "FC012-FIXTURE: synthetic Ani reply body",
            SentAt  = sentAt,
        });

        captured.Should().NotBeNull("the AddMessageAsync path must produce an Episodic MemoryRecord");
        var rendered = PromptBuilder.FormatMemoryWithTime(captured!, DateTimeOffset.UtcNow);

        rendered.Should().Contain("AUTHORED: Ani",
            "end-to-end invariant: what the producer emits must render with the correct author tag. "
          + "Failure here with FC-012a passing indicates the render surface (FormatMemoryWithTime) "
          + "silently dropped the attribution field between produce and render.");
        rendered.Should().NotContain("AUTHORED: Mark",
            "Ani's own dispatched content must NEVER render as Mark-authored — the 12:04 shape "
          + "at the render surface");
    }

    // ─────────────────────────────────────────────────────────────────────
    // FC-012f — Two-turn loop shape: Ani/Mark records render distinctly
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// FC-012f — SPEC: in a two-turn exchange (Mark inbound → Ani reply),
    /// the two resulting Episodic records MUST render with DISTINCT
    /// <c>AUTHORED:</c> tags. Same test surface as FC-012c/e but pinning
    /// the discrimination between roles under identical retrieval framing.
    /// The 12:04 shape specifically collapsed this distinction; this test
    /// pins that the distinction survives all the way to the composer.
    /// </summary>
    [Fact]
    public async Task FC012f_TwoTurnExchange_RendersMarkAndAniDistinctly()
    {
        var thread = await NewActiveThreadAsync();
        var t = DateTimeOffset.UtcNow.AddMinutes(-30);

        _memory.Setup(m => m.GetCharacterStateAsync(It.IsAny<CancellationToken>()))
               .ReturnsAsync(new CharacterStateDoc { Name = "Ani", PrimaryContactName = "Mark" });
        var captured = new List<MemoryRecord>();
        _memory.Setup(m => m.SaveAsync(It.IsAny<MemoryRecord>(), It.IsAny<CancellationToken>()))
               .Callback<MemoryRecord, CancellationToken>((r, _) => captured.Add(r))
               .Returns(Task.CompletedTask);

        await _svc.AddMessageAsync(thread.Id, new ConversationMessage
        {
            Role = Roles.Mark, Content = "FC012-FIXTURE: Mark inbound turn", SentAt = t,
        });
        await _svc.AddMessageAsync(thread.Id, new ConversationMessage
        {
            Role = Roles.Ani,  Content = "FC012-FIXTURE: Ani reply turn",   SentAt = t.AddSeconds(30),
        });

        captured.Should().HaveCount(2, "one Episodic per turn");
        var markRecord = captured[0];
        var aniRecord  = captured[1];

        var markRendered = PromptBuilder.FormatMemoryWithTime(markRecord, DateTimeOffset.UtcNow);
        var aniRendered  = PromptBuilder.FormatMemoryWithTime(aniRecord,  DateTimeOffset.UtcNow);

        markRendered.Should().Contain("AUTHORED: Mark");
        markRendered.Should().NotContain("AUTHORED: Ani");
        aniRendered.Should().Contain("AUTHORED: Ani");
        aniRendered.Should().NotContain("AUTHORED: Mark");
    }
}
