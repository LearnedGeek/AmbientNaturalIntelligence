using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;

namespace AniRuntime.LLM;

/// <summary>
/// Feature 34 (Packer et al. 2023 — MemGPT): Context compression for long conversations.
///
/// Architecture: keep as many raw messages as the context budget allows. When the
/// thread exceeds the budget, summarize the oldest messages in Ani's voice, preserving
/// the emotional arc and key details. The summary is Ani remembering the conversation,
/// not a clinical third-person report.
///
/// Layout: [Ani's summary of older messages] + [N most recent raw messages]
///
/// The raw window scales with thread length:
///   - Threads under 10 messages: no compression, all raw
///   - Threads 10-20 messages: keep last 8 raw
///   - Threads 20-30 messages: keep last 10 raw
///   - Threads 30+: keep last 12 raw
///
/// Summary length scales with the number of compressed messages:
///   - ~80 chars per compressed message (enough to preserve meaning)
///   - Minimum 300 chars, maximum 1500 chars
/// </summary>
public class ContextCompressor
{
    private readonly IOllamaClient _ollama;
    private readonly ILogger<ContextCompressor> _log;

    public ContextCompressor(IOllamaClient ollama, ILogger<ContextCompressor> log)
    {
        _ollama = ollama;
        _log = log;
    }

    /// <summary>
    /// Compresses conversation history when it exceeds the context budget.
    /// Returns a list of ChatMessages with older turns replaced by Ani's summary.
    ///
    /// The summary is cached on the thread and only regenerated when new messages
    /// push beyond the previously summarized range.
    /// </summary>
    public async Task<List<ChatMessage>> CompressIfNeededAsync(
        ConversationThread thread, int windowSize, CancellationToken ct)
    {
        var messages = thread.Messages;

        // Scale the raw window based on thread length
        var rawWindow = messages.Count switch
        {
            <= 10 => messages.Count, // no compression needed
            <= 20 => 8,
            <= 30 => 10,
            _ => 12,
        };

        if (messages.Count <= rawWindow)
        {
            // No compression needed — return all messages as-is.
            // F-2 Phase 1 P5 (2026-08-22) — derive attribution from
            // ConversationMessage.Role. Both sides verified.
            return messages.Select(m => new ChatMessage(
                m.Role == Roles.Ani ? "assistant" : "user",
                m.Content)
            {
                AttributedTo     = m.Role == Roles.Ani
                    ? AniRuntime.Core.Interfaces.AttributedTo.Ani
                    : AniRuntime.Core.Interfaces.AttributedTo.Mark,
                AttributionTrust = "verified",
            }).ToList();
        }

        // How many messages need summarizing?
        var keepCount = rawWindow;
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
            // Generate new summary — scale length with message count
            var targetLength = Math.Clamp(summarizeCount * 80, 300, 1500);
            var transcript = string.Join("\n", toSummarize.Select(m =>
                $"{(m.Role == Roles.Ani ? "Ani" : "Mark")}: {m.Content}"));

            var summary = await SummarizeAsync(transcript, targetLength, ct)
                .ConfigureAwait(false);
            thread.CompressedSummary = summary;
            thread.CompressedSummaryUpToIndex = summarizeCount;

            _log.LogDebug("Context compression: summarized {Count} messages into {Len} chars (target {Target})",
                summarizeCount, summary.Length, targetLength);

            // Agentic Lens parrot-bug investigation (Apr 23, 2026): log the
            // compressed summary text itself so we can see what the reply
            // model actually receives. Per the Apr 23 raw-Ollama diagnostic,
            // the fine-tune replies richly when given raw context; the parrot
            // behavior appears only through the pipeline. The compressed
            // summary is one of the transforms the pipeline applies, so its
            // content needs to be visible in the journal to diagnose.
            _log.LogDebug("Context compression summary text: {Summary}", summary);
        }

        // Build the result: structured state + optional LLM summary + recent messages
        var result = new List<ChatMessage>();

        // Phase 3: Structured conversation state — compact, programmatic, no LLM call.
        // Provides topic, register, commitments, key facts, shared imagery in ~50-80 tokens.
        var stateBlock = thread.State.ToPromptBlock();
        if (stateBlock is not null)
        {
            result.Add(new("user", stateBlock));
            // Parrot-bug investigation (Apr 23): log the state-block content.
            // The structured-state prompt block is another transform between
            // raw conversation and what the reply model sees.
            _log.LogDebug("Context compression state block ({Len} chars): {State}",
                stateBlock.Length, stateBlock);
        }

        // LLM summary as fallback for context the structured state doesn't capture
        if (thread.CompressedSummary is not null)
        {
            result.Add(new("user", $"[Earlier in this conversation: {thread.CompressedSummary}]"));
        }

        // F-2 Phase 1 P5 (2026-08-22) — attribution on the kept-tail
        // turns; the earlier-in-conversation summary above is Ani-composed
        // synthesis but we leave its attribution as default (unknown /
        // unverified) because the summary paraphrase collapses both sides.
        result.AddRange(toKeep.Select(m => new ChatMessage(
            m.Role == Roles.Ani ? "assistant" : "user",
            m.Content)
        {
            AttributedTo     = m.Role == Roles.Ani
                ? AniRuntime.Core.Interfaces.AttributedTo.Ani
                : AniRuntime.Core.Interfaces.AttributedTo.Mark,
            AttributionTrust = "verified",
        }));

        // Parrot-bug investigation (Apr 23): log the shape of the final
        // compressed-history payload so we can correlate per-cycle history
        // size to response quality.
        _log.LogDebug(
            "Context compression payload: {Total} messages ({State} state-block, {Summary} summary, {Raw} raw)",
            result.Count,
            stateBlock is not null ? 1 : 0,
            thread.CompressedSummary is not null ? 1 : 0,
            toKeep.Count);

        return result;
    }

    /// <summary>
    /// Summarize the conversation in Ani's voice, preserving emotional arc.
    /// Not a clinical report — Ani remembering what happened.
    /// </summary>
    private async Task<string> SummarizeAsync(
        string transcript, int targetLength, CancellationToken ct)
    {
        var system = $"""
            You are Ani, summarizing what happened earlier in your conversation with Mark.
            Write in first person as yourself — this is you remembering, not a report.

            Preserve:
            - The emotional arc (how the mood shifted during the conversation)
            - Key details, facts, or promises made
            - Any metaphors or imagery you were building together
            - What Mark shared about his life
            - What you shared about yours

            Keep it to about {targetLength / 4} words. Be genuine, not clinical.
            Write as if you're catching yourself up: "We started talking about..."
            """;

        var summary = await _ollama.InnerMonologueChatAsync(
            system, Array.Empty<ChatMessage>(), transcript, ct).ConfigureAwait(false);

        // Trim to target length with some flexibility
        var maxLength = (int)(targetLength * 1.3);
        if (summary.Length > maxLength)
            summary = summary[..maxLength];

        return summary.Trim();
    }
}
