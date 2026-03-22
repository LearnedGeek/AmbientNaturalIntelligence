using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;

namespace AniRuntime.LLM;

/// <summary>
/// Uses the 3B inner monologue model for lightweight semantic extraction.
/// Two capabilities:
/// 1. Intent extraction: "What is this message about?" → short topic phrase
/// 2. Relevance scoring: "Is this article related to these interests?" → 0.0-1.0
///
/// The 3B model is chosen because it's the cheapest LLM call and is
/// most likely already loaded in VRAM from cognitive cycle inner thoughts.
/// </summary>
public class IntentExtractor : IIntentExtractor
{
    private readonly IOllamaClient _ollama;
    private readonly ILogger<IntentExtractor> _log;

    private const string IntentSystemPrompt =
        "Extract the core topic of this message in 5-10 words. " +
        "Focus on WHAT the person is talking about, not greetings or filler. " +
        "Reply with only the topic phrase, nothing else.";

    private const string RelevanceSystemPrompt =
        "You are evaluating whether a news article is personally relevant to someone. " +
        "Score from 0.0 to 1.0 where:\n" +
        "- 0.0-0.3: Generic news, no personal connection\n" +
        "- 0.4-0.6: Tangentially related to their interests\n" +
        "- 0.7-0.8: Directly relates to something they care about\n" +
        "- 0.9-1.0: Would genuinely make them stop and read\n" +
        "Reply with ONLY the numeric score, nothing else.";

    public IntentExtractor(IOllamaClient ollama, ILogger<IntentExtractor> log)
    {
        _ollama = ollama;
        _log = log;
    }

    public async Task<string> ExtractIntentAsync(string message, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(message) || message.Length < 10)
            return message; // Too short to extract from — use as-is

        try
        {
            var intent = await _ollama.InnerMonologueChatAsync(
                IntentSystemPrompt,
                Array.Empty<ChatMessage>(),
                message,
                ct).ConfigureAwait(false);

            intent = intent.Trim().Trim('"', '\'', '.').Trim();

            // Guard against the model returning something longer than the input
            // or returning empty/unhelpful responses
            if (string.IsNullOrWhiteSpace(intent) || intent.Length > message.Length)
            {
                _log.LogDebug("Intent extraction returned unusable result — falling back to raw message");
                return message;
            }

            _log.LogDebug("Intent extraction: \"{Message}\" → \"{Intent}\"",
                message.Length > 60 ? message[..60] + "..." : message,
                intent);

            return intent;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Intent extraction failed — falling back to raw message");
            return message;
        }
    }

    public async Task<float> ScoreRelevanceAsync(
        string itemSummary, IReadOnlyList<string> interestDescriptions, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(itemSummary) || interestDescriptions.Count == 0)
            return 0.2f;

        try
        {
            var interests = string.Join("\n", interestDescriptions.Take(10));
            var userPrompt = $"ARTICLE: {itemSummary}\n\nPERSON'S INTERESTS:\n{interests}\n\nRelevance score:";

            var response = await _ollama.InnerMonologueChatAsync(
                RelevanceSystemPrompt,
                Array.Empty<ChatMessage>(),
                userPrompt,
                ct).ConfigureAwait(false);

            response = response.Trim();

            // Parse the numeric score — the model should return just a number
            if (float.TryParse(response, out var score))
            {
                score = Math.Clamp(score, 0f, 1f);
                _log.LogDebug("Relevance score for \"{Summary}\": {Score:F2}",
                    itemSummary.Length > 60 ? itemSummary[..60] + "..." : itemSummary,
                    score);
                return score;
            }

            // Try to extract a number from a longer response
            var words = response.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var word in words)
            {
                if (float.TryParse(word.Trim('.', ',', ':'), out var extracted))
                    return Math.Clamp(extracted, 0f, 1f);
            }

            _log.LogDebug("Relevance scoring returned non-numeric: \"{Response}\" — defaulting to 0.2", response);
            return 0.2f;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Relevance scoring failed — defaulting to 0.2");
            return 0.2f;
        }
    }
}
