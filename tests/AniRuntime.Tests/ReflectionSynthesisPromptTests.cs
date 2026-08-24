using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.LLM;
using AniRuntime.LLM.Prompts;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// F-2 Phase 2 W2 (2026-08-23) — verifies that
/// <see cref="ReflectionSynthesisPromptCommand"/> renders source records
/// with the F-2 Phase 1 P4 attribution-tag shape.
///
/// <para>
/// Pre-W2 the caller (ReflectionPhase) projected
/// <c>recentMemories.Select(m =&gt; m.Content)</c> which stripped
/// attribution before the compression composer ever saw it. That was the
/// C4 "compression pipe" finding from the F-2 Phase 2 audit — the largest
/// known upstream surface where attribution disappeared between substrate
/// and composer. W2 restores per-source attribution by rendering each
/// source via <see cref="PromptBuilder.FormatMemoryWithTime"/>.
/// </para>
/// </summary>
public class ReflectionSynthesisPromptTests
{
    [Fact]
    public void Build_RendersSourceMemoriesWithP4AttributionTags()
    {
        var markAttr = AttributionTriple.MarkAt(
            DateTimeOffset.UtcNow.AddHours(-2), "twilio-inbound:SM_test");
        var markRecord = new MemoryRecord
        {
            Content                    = "W2-FIXTURE: Mark inbound content",
            Provenance                 = EpistemicTier.Facts,
            SourceName                 = "twilio-inbound",
            OccurredAt                 = DateTimeOffset.UtcNow.AddHours(-2),
            AttributedTo               = markAttr.AttributedTo,
            AttributedAt               = markAttr.AttributedAt,
            AttributedSourceRecordId   = markAttr.SourceRecordId,
            AttributedSourceDescriptor = markAttr.SourceDescriptor,
            AttributionTrust           = markAttr.Trust,
        };

        var aniAttr = AttributionTriple.AniAt(DateTimeOffset.UtcNow.AddHours(-1));
        var aniRecord = new MemoryRecord
        {
            Content                    = "W2-FIXTURE: Ani interior content",
            Provenance                 = EpistemicTier.Interior,
            OccurredAt                 = DateTimeOffset.UtcNow.AddHours(-1),
            AttributedTo               = aniAttr.AttributedTo,
            AttributedAt               = aniAttr.AttributedAt,
            AttributedSourceRecordId   = aniAttr.SourceRecordId,
            AttributedSourceDescriptor = aniAttr.SourceDescriptor,
            AttributionTrust           = aniAttr.Trust,
        };

        var input = new ReflectionSynthesisPromptInput(
            CharacterName:  "Ani",
            ContactName:    "Mark",
            RecentMemories: new[] { markRecord, aniRecord });

        var prompt = new ReflectionSynthesisPromptCommand().Build(input);

        // The user prompt is the source-memory dump. Both records must appear
        // with the P4 attribution-tag boundary.
        prompt.User.Should().Contain("[FROM:",
            "each source-memory line must open with the P4 attribution-tag boundary — pre-W2 they rendered as raw content bullets");
        prompt.User.Should().Contain("AUTHORED: Mark",
            "Mark-authored source records must carry AUTHORED: Mark so the composer can preserve authorship in the gist output");
        prompt.User.Should().Contain("AUTHORED: Ani",
            "Ani-authored source records must carry AUTHORED: Ani so the composer can preserve authorship in the gist output");

        // Content still present after the tag boundary.
        prompt.User.Should().Contain("W2-FIXTURE: Mark inbound content");
        prompt.User.Should().Contain("W2-FIXTURE: Ani interior content");
    }

    [Fact]
    public void Build_SystemPromptContainsAttributionDisciplineNote()
    {
        // W2 also updates the system prompt so the composer is told what
        // the AUTHORED tag means and that it must preserve per-source
        // authorship. Pin the discipline note so a future prompt-edit
        // pass can't silently drop it.
        var input = new ReflectionSynthesisPromptInput(
            CharacterName:  "Ani",
            ContactName:    "Mark",
            RecentMemories: Array.Empty<MemoryRecord>());

        var prompt = new ReflectionSynthesisPromptCommand().Build(input);

        prompt.System.Should().Contain("AUTHORED",
            "the system prompt must explain the AUTHORED tag so the composer respects the boundary");
        prompt.System.Should().Contain("PRESERVE per-source authorship",
            "the CRITICAL RULES block must call out authorship preservation as an explicit constraint");
    }

    [Fact]
    public void Build_LegacyRecordWithoutAttribution_RendersAuthoredUnknown()
    {
        // CONTROL: a pre-F-2 record with no attribution set defaults to
        // AttributedTo.Unknown / trust=unverified. Renders explicitly so
        // the composer doesn't fabricate an attribution for it. Same
        // pattern as W1 — legacy records carry no author signal, which
        // is the honest reading, not a defect.
        var legacyRecord = new MemoryRecord
        {
            Content    = "W2-FIXTURE: legacy content pre-F-2",
            Provenance = EpistemicTier.Facts,
            OccurredAt = DateTimeOffset.UtcNow.AddDays(-30),
            // No AttributedTo set → default Unknown
            // No AttributionTrust set → default "unverified"
        };

        var input = new ReflectionSynthesisPromptInput(
            CharacterName:  "Ani",
            ContactName:    "Mark",
            RecentMemories: new[] { legacyRecord });

        var prompt = new ReflectionSynthesisPromptCommand().Build(input);

        prompt.User.Should().Contain("AUTHORED: unknown",
            "pre-F-2 records with no attribution must render as AUTHORED: unknown so the composer does not fabricate authorship");
        prompt.User.Should().Contain("TRUST: unverified",
            "non-verified trust renders explicitly so the composer down-weights the source");
    }
}
