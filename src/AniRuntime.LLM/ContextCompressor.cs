using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;

namespace AniRuntime.LLM;

/// <summary>
/// Feature 34 (Packer et al. 2023 — MemGPT): Context compression for long conversations.
///
/// When a conversation exceeds the history window, older messages are summarized
/// into a brief recap that preserves key context without consuming full token budget.
/// This replaces the current approach of silently dropping middle messages.
///
/// The summary is generated once and cached on the ConversationThread, only
/// regenerated when new messages push beyond the window again.
/// </summary>
public class ContextCompressor
{
    private readonly IOllamaClient _ollama;
    private readonly ILogger<ContextCompressor> _log;

    /// <summary>Minimum messages before compression kicks in.</summary>
    private const int CompressionThreshold = 6;

    public ContextCompressor(IOllamaClient ollama, ILogger<ContextCompressor> log)
    {
        _ollama = ollama;
        _log = log;
    }

    /// <summary>
    /// Compresses conversation history when it exceeds the window size.
    /// Returns a list of ChatMessages with older turns replaced by a summary message.
    ///
    /// Layout: [summary of turns 1..N-windowSize] + [last windowSize messages]
    ///
    /// The summary is cached on the thread to avoid regenerating every cycle.
    /// </summary>
    public async Task<List<ChatMessage>> CompressIfNeededAsync(
        ConversationThread thread, int windowSize, CancellationToken ct)
    {
        var messages = thread.Messages;

        if (messages.Count <= windowSize)
        {
            // No compression needed — return all messages as-is
            return messages.Select(m => new ChatMessage(
                m.Role == Roles.Ani ? "assistant" : "user",
                m.Content)).ToList();
        }

        // How many messages need summarizing?
        var keepCount = windowSize - 1; // reserve 1 slot for the summary
        var summarizeCount = messages.Count - keepCount;
        var toSummarize = messages.Take(summarizeCount).ToList();
        var toKeep = messages.Skip(summarizeCount).ToList();

        // Check if we already have a valid cached summary for this range
        if (thread.CompressedSummary is not null &&
            thread.CompressedSummaryUpToIndex >= summarizeCount)
        {
            _log.LogDebug("Context compression: using cached summary (covers {Count} messages)",
                thread.CompressedSummaryUpToIndex);
        }
        else
        {
            // Generate new summary
            var transcript = string.Join("\n", toSummarize.Select(m =>
                $"{(m.Role == Roles.Ani ? "Ani" : "Mark")}: {m.Content}"));

            var summary = await SummarizeAsync(transcript, ct).ConfigureAwait(false);
            thread.CompressedSummary = summary;
            thread.CompressedSummaryUpToIndex = summarizeCount;

            _log.LogDebug("Context compression: summarized {Count} messages into {Len} chars",
                summarizeCount, summary.Length);
        }

        // Build the result: summary as a system-style user message + recent messages
        var result = new List<ChatMessage>
        {
            new("user", $"[Earlier in this conversation: {thread.CompressedSummary}]")
        };
        result.AddRange(toKeep.Select(m => new ChatMessage(
            m.Role == Roles.Ani ? "assistant" : "user",
            m.Content)));

        return result;
    }

    private async Task<string> SummarizeAsync(string transcript, CancellationToken ct)
    {
        var system = """
            Summarize this conversation in 2-3 sentences. Focus on:
            - What topics were discussed
            - Key facts or details shared
            - The emotional tone
            Keep it brief and factual. Write in third person ("They discussed...").
            """;

        var user = transcript;

        var summary = await _ollama.InnerMonologueChatAsync(
            system, Array.Empty<ChatMessage>(), user, ct).ConfigureAwait(false);

        // Trim to reasonable length
        if (summary.Length > 300)
            summary = summary[..300];

        return summary.Trim();
    }
}
