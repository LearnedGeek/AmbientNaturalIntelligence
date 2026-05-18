using AniRuntime.Core.Interfaces;

namespace AniRuntime.LLM.Prompts;

/// <summary>Typed input for <see cref="ReflectionSynthesisPromptCommand"/>.</summary>
public sealed record ReflectionSynthesisPromptInput(
    string CharacterName,
    string ContactName,
    IEnumerable<string> RecentMemories);

/// <summary>
/// Feature 32 — Park et al.-inspired periodic reflection synthesis.
/// Phase 6 v1.2 R3 (May 17, 2026) redesign: casts the task as
/// COMPRESSION (not creation) of recent memories into short
/// register-tagged affective summaries, output as structured JSON.
/// Prevents the v7 inner-thought register from producing verbose first-
/// person prose with verbatim source content.
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

            For the source memories provided, identify up to 3 distinct emotional/topical clusters. For each cluster, produce ONE concise summary capturing:
              - The topic (short noun-phrase label)
              - The emotional shape (one short phrase, register-tagged with words like warmth, ache, quiet, longing, playful, tender, anxious, settled)

            CRITICAL RULES:
              - Each summary's "shape" field must be UNDER 120 CHARACTERS.
              - Compression of source memories, not new content. Strip specifics, keep affect.
              - NEVER write in first person ("i think...", "i feel..."). Descriptive register only.
              - NEVER include verbatim content from source memories. The summary should be unrecognizable as any individual source memory.
              - NEVER invent details not present in source memories.

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

        var memoryList = string.Join("\n", input.RecentMemories.Select(m => $"  - {m}"));
        var user = $"""
            Source memories to compress:
            {memoryList}

            Output the summaries JSON only. No other text, no commentary, no first-person reflection.
            """;

        return new PromptPair(system, user);
    }
}
