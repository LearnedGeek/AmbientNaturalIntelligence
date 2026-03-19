using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.LLM;
using Microsoft.Extensions.Logging;

namespace AniRuntime.Loops;

/// <summary>
/// Generates inner thoughts, reflections, and valence scores.
///
/// Extracted from CognitiveCycleProcessor (SRP) — inner thought generation
/// is a distinct responsibility from cycle orchestration. This class owns
/// the LLM calls for private monologue, reflection, and valence scoring.
/// </summary>
public class InnerThoughtPhase
{
    private readonly IOllamaClient _ollama;
    private readonly ILogger<InnerThoughtPhase> _log;

    public InnerThoughtPhase(IOllamaClient ollama, ILogger<InnerThoughtPhase> log)
    {
        _ollama = ollama;
        _log = log;
    }

    /// <summary>
    /// Generates an inner thought, scores its relational valence, and optionally
    /// produces a reflection (Park et al. generative agent reflection layer).
    /// </summary>
    public async Task<(string Thought, string? Reflection, float Valence)> RunAsync(
        ContextSnapshot snapshot, CancellationToken ct)
    {
        var thoughtPrompt = PromptBuilder.BuildInnerThoughtPrompt(snapshot);
        var thought = await _ollama.InnerMonologueChatAsync(
            thoughtPrompt.System, snapshot.RecentHistory, thoughtPrompt.User, ct)
            .ConfigureAwait(false);

        // Score the raw thought for valence BEFORE reflection
        var valence = await ScoreRelationalValenceAsync(thought, snapshot.CharacterState, ct)
            .ConfigureAwait(false);

        // Reflection layer (Park et al.)
        var reflection = await ReflectOnThoughtAsync(thought, snapshot, ct).ConfigureAwait(false);

        return (thought, reflection, valence);
    }

    private async Task<string?> ReflectOnThoughtAsync(
        string thought, ContextSnapshot snapshot, CancellationToken ct)
    {
        try
        {
            var reflectionPrompt = PromptBuilder.BuildReflectionPrompt(thought, snapshot);
            var reflection = await _ollama.InnerMonologueChatAsync(
                reflectionPrompt.System, Array.Empty<ChatMessage>(), reflectionPrompt.User, ct)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(reflection))
                return null;

            reflection = reflection.Trim();
            if (reflection.Length > 200)
                reflection = reflection[..200];

            _log.LogDebug("Reflection: {Reflection}", reflection);
            return reflection;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Reflection failed — continuing without");
            return null;
        }
    }

    private async Task<float> ScoreRelationalValenceAsync(
        string thought, CharacterStateDoc character, CancellationToken ct)
    {
        var prompt = PromptBuilder.BuildValenceScoringPrompt(thought, character);
        var raw = await _ollama.ChatJsonAsync(
            prompt.System, Array.Empty<ChatMessage>(), prompt.User, ct)
            .ConfigureAwait(false);

        return ParseValenceScore(raw);
    }

    internal static float ParseValenceScore(string raw)
    {
        try
        {
            var doc = System.Text.Json.JsonDocument.Parse(raw.Trim());
            var score = doc.RootElement.GetProperty("score").GetDouble();
            return (float)Math.Clamp(score, 0.0, 1.0);
        }
        catch
        {
            return 0.3f;
        }
    }
}
