namespace AniRuntime.Core.Utilities;

/// <summary>
/// Theme R.1 (2026-05-24, Issue #64) — maps the 9-register taxonomy to a
/// small set of composition prompt-variant keys. Consumers
/// (OutreachMessagePromptCommand, LeanConversationPromptCommand) branch on
/// the returned key to select between system-prompt template variants.
///
/// <para>
/// The variant set is deliberately small — three variants — to keep the
/// initial wiring observable and the test set tight. Subsequent R-phases
/// (R.1.x) can expand the variant set if empirical observation shows
/// register variation is reaching production output but more nuance is
/// useful.
/// </para>
///
/// <para>
/// Per R.0 contract: this maps an architectural input (register) to a
/// code-path branch (variant key). Composers selecting different system
/// prompts based on the key is STRUCTURAL consumption — different code
/// paths run, different prompt text is emitted, behavior observably
/// differs across registers.
/// </para>
/// </summary>
public static class RegisterPromptVariant
{
    /// <summary>
    /// Default-warm shape. Existing prompts. Used when register is null
    /// (cold start, non-hybrid path) or when register is in the warm
    /// family (Tenderness, Playfulness, Delight, Curiosity, Desire).
    /// </summary>
    public const string DefaultWarm = "default_warm";

    /// <summary>
    /// Quieter, more reflective, less performative shape. Used when
    /// register is Wistful or Existential — registers that don't suit
    /// the warm-text default.
    /// </summary>
    public const string Reflective = "reflective";

    /// <summary>
    /// Honest hard-edge shape. Used when register is Frustration or
    /// Longing — registers where warm framing would feel false.
    /// </summary>
    public const string HonestEdge = "honest_edge";

    /// <summary>
    /// Map a register name (canonical, from
    /// <c>ClosedConversationSummarizer.Registers</c>) to a variant key.
    /// Null / unrecognized → <see cref="DefaultWarm"/>.
    /// </summary>
    public static string Select(string? register) => register switch
    {
        "Tenderness"  => DefaultWarm,
        "Playfulness" => DefaultWarm,
        "Delight"     => DefaultWarm,
        "Curiosity"   => DefaultWarm,
        "Desire"      => DefaultWarm,
        "Wistful"     => Reflective,
        "Existential" => Reflective,
        "Frustration" => HonestEdge,
        "Longing"     => HonestEdge,
        _             => DefaultWarm,
    };
}
