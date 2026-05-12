namespace AniRuntime.LLM;

/// <summary>
/// Theme P Phase P.1 (May 11, 2026) — configuration for the Anthropic
/// cross-class verifier (the cloud half of the local-generator /
/// cloud-verifier split described in the Theme P plan §1, additive
/// defense-in-depth per §9.1).
///
/// Bound from the <c>"Anthropic"</c> section of appsettings. The API key
/// lives ONLY in <c>appsettings.Development.json</c> (mirrored on the
/// server with the real key — appsettings.json carries a placeholder).
/// Matches the secret-handling pattern in this codebase (Voice / Twilio /
/// ElevenLabs all follow the same shape — see
/// <c>memory/feedback_secrets_in_dev_json.md</c>).
/// </summary>
public sealed class AnthropicOptions
{
    /// <summary>
    /// Anthropic API key (sk-ant-...). Placeholder in committed
    /// configuration; real key set on ani-server only.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Model identifier. Theme P P.1 default = <c>claude-sonnet-4-6</c>
    /// matching the ml-intern survey + DrOk pattern (different training
    /// lineage than the local Llama generator — cross-class invariant
    /// per plan-doc §1).
    /// </summary>
    public string Model { get; set; } = "claude-sonnet-4-6";

    /// <summary>
    /// Hard cap on response tokens. Response is structured JSON with
    /// five short verdict objects — 1024 is generous.
    /// </summary>
    public int MaxTokens { get; set; } = 1024;

    /// <summary>
    /// Per-call timeout in milliseconds. The handler treats a timeout as
    /// a graceful-degradation trigger (returns <c>Continue</c> so local
    /// judgment gates remain active on the same dispatch) so this should
    /// be set tight enough that ANI's outreach cadence is not stalled by
    /// cloud latency, but loose enough that normal Sonnet response time
    /// fits comfortably.
    /// </summary>
    public int TimeoutMs { get; set; } = 30000;

    /// <summary>
    /// API base URL (override only for testing against a mock).
    /// </summary>
    public string BaseUrl { get; set; } = "https://api.anthropic.com";

    /// <summary>
    /// Anthropic API version header (<c>anthropic-version</c>).
    /// </summary>
    public string ApiVersion { get; set; } = "2023-06-01";
}
