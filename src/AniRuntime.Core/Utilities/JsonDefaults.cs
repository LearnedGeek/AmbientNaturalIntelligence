using System.Text.Json;
using System.Text.Json.Serialization;

namespace AniRuntime.Core.Utilities;

/// <summary>
/// Shared JsonSerializerOptions — eliminates duplicate static fields across projects.
/// </summary>
public static class JsonDefaults
{
    /// <summary>
    /// CamelCase property naming + case-insensitive reads + skip nulls.
    /// Use for: Ollama API, ElevenLabs API, WebSocket control messages,
    /// emergence JSON, image manifests, conversation service.
    /// </summary>
    public static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Case-insensitive reads only (no naming policy override).
    /// Use for: deserializing external API responses where property names are already correct.
    /// </summary>
    public static readonly JsonSerializerOptions CaseInsensitive = new()
    {
        PropertyNameCaseInsensitive = true
    };
}
