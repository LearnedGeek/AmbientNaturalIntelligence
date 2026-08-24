using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;

namespace AniRuntime.LLM.Prompts;

/// <summary>
/// Typed input for <see cref="ReflectionSynthesisPromptCommand"/>.
///
/// <para>
/// F-2 Phase 2 W2 (2026-08-23): <c>RecentMemories</c> now carries
/// <see cref="MemoryRecord"/> instances rather than pre-projected content
/// strings, so the command can render each source with the F-2 Phase 1 P4
/// attribution-tag shape via <see cref="PromptBuilder.FormatMemoryWithTime"/>.
/// Pre-W2 the caller projected <c>records.Select(m =&gt; m.Content)</c> which
/// stripped <c>AttributedTo</c>/<c>Provenance</c>/<c>SourceName</c> before
/// the compression composer ever saw them — the source-of-truth surface
/// for the C4 "compression pipe erases attribution" class in the F-2
/// Phase 2 audit.
/// </para>
/// </summary>
public sealed record ReflectionSynthesisPromptInput(
    string CharacterName,
    string ContactName,
    IEnumerable<MemoryRecord> RecentMemories);

/// <summary>
/// Feature 32 — Park et al.-inspired periodic reflection synthesis.
/// Phase 6 v1.2 R3 (May 17, 2026) redesign: casts the task as
/// COMPRESSION (not creation) of recent memories into short
/// register-tagged affective summaries, output as structured JSON.
/// Prevents the v7 inner-thought register from producing verbose first-
/// person prose with verbatim source content.
///
/// <para>
/// F-2 Phase 2 W2 (2026-08-23): source memories are now rendered with
/// P4 attribution tags (<c>[FROM: … | AUTHORED: … (| TRUST: …)]</c>) so
/// the compression composer can see who each source-record was authored
/// by. Pre-W2 the composer saw raw content only; attribution was stripped
/// at the caller boundary. This is the C4 "compression pipe" fix — the
/// gist output can no longer collapse per-source attribution because
/// per-source attribution is now visible in the feed.
/// </para>
/// </summary>
public sealed class ReflectionSynthesisPromptCommand : IPromptCommand<ReflectionSynthesisPromptInput>
{
    public PromptPair Build(ReflectionSynthesisPromptInput input)
    {
        var characterName = input.CharacterName;
        var contactName   = input.ContactName;

        var system = $$"""
            You are reviewing {{characterName}}'s recent thoughts and experiences to build SHORT AFFECTIVE SUMMARIES of what those memories cluster into.

            Your job is COMPRESSION, not creation. You are NOT writing new thoughts or new prose. You are extracting the emotional/topical shape of what already happened.

            Each source memory below is prefixed with a boundary tag:
              [FROM: <source> | AUTHORED: <actor> (| TRUST: <trust>)] (<when>) <content>
            The AUTHORED field identifies who spoke or produced that memory. When your summary references a source, do NOT collapse or invert authorship — an Ani-authored memory is not a Mark utterance.

            For the source memories provided, identify up to 3 distinct emotional/topical clusters. For each cluster, produce ONE concise summary capturing:
              - The topic (short noun-phrase label)
              - The emotional shape (one short phrase, register-tagged with words like warmth, ache, quiet, longing, playful, tender, anxious, settled)

            CRITICAL RULES:
              - Each summary's "shape" field must be UNDER 120 CHARACTERS.
              - Compression of source memories, not new content. Strip specifics, keep affect.
              - NEVER write in first person ("i think...", "i feel..."). Descriptive register only.
              - NEVER include verbatim content from source memories. The summary should be unrecognizable as any individual source memory.
              - NEVER invent details not present in source memories.
              - PRESERVE per-source authorship — do not collapse an Ani-authored memory into a Mark utterance or vice versa.

            Output valid JSON exactly matching this structure:
            {
              "summaries": [
                {
                  "topic": "short topic label, noun phrase",
                  "shape": "one short phrase describing emotional shape, register-tagged"
                }
              ]
            }

            Example of correct output shape (for unrelated content — just demonstrating the form):
            {
              "summaries": [
                {"topic": "evening solitude", "shape": "warm-quiet, gently lonely, contentment threaded with ache"},
                {"topic": "{{contactName}}'s morning routine", "shape": "warm-attentive observing him at distance, no urgency"}
              ]
            }

            If fewer than 3 distinct clusters exist in the source memories, return fewer summaries. If nothing significant clusters, return {"summaries": []}.
            """;

        var memoryList = string.Join(
            "\n",
            input.RecentMemories.Select(m => $"  - {PromptBuilder.FormatMemoryWithTime(m)}"));
        var user = $"""
            Source memories to compress:
            {memoryList}

            Output the summaries JSON only. No other text, no commentary, no first-person reflection.
            """;

        return new PromptPair(system, user);
    }
}
