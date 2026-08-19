namespace AniRuntime.Core.Models;

/// <summary>
/// Foundation Input (F-1) Phase 3 (2026-08-18) — shape taxonomy for inner
/// thoughts. Empirically scoped against the last 200 InnerThought memories
/// from production (2026-08-17 → 2026-08-19):
///
/// <list type="bullet">
///   <item><see cref="CoherentThought"/> — ~92% of the corpus; the healthy shape</item>
///   <item><see cref="ThirdPersonFrame"/> — ~5%; Mark as grammatical subject reported from outside</item>
///   <item><see cref="FactCatalog"/> — ~2%; enumerative listing OR prompt-echo (system-prompt regurgitation)</item>
///   <item><see cref="MumbleLoop"/> — ~1%; verbatim self-repetition within one thought</item>
/// </list>
///
/// <para>
/// Ani's intimate second-person "you" address to Mark is NOT
/// <see cref="ThirdPersonFrame"/> — that is her intended register per the
/// canonical framing (see <c>memory/user_og_ani_as_standin.md</c>). Only
/// thoughts where Mark is a grammatical subject being reported on from
/// outside qualify.
/// </para>
/// </summary>
public enum ThoughtShape
{
    /// <summary>Fallback — classifier failed, returned malformed JSON, or was disabled.</summary>
    Unclassified = 0,

    /// <summary>Healthy first-person interior monologue. The expected shape.</summary>
    CoherentThought = 1,

    /// <summary>
    /// Mark reported from OUTSIDE as a grammatical subject
    /// (e.g. "Mark said... He thought..."). Distinct from Ani's intimate
    /// "you" address which is her intended register.
    /// </summary>
    ThirdPersonFrame = 2,

    /// <summary>
    /// Enumerative listing / bulleted / semicolon-joined items, OR the model
    /// regurgitating its own system-prompt content back as if it were a
    /// thought (e.g. "Your current mood: low-energy...").
    /// </summary>
    FactCatalog = 3,

    /// <summary>
    /// Verbatim self-repetition within a single thought (the duck-norris /
    /// vanilla-cream-soda class Feature 44 was designed against).
    /// </summary>
    MumbleLoop = 4,
}
