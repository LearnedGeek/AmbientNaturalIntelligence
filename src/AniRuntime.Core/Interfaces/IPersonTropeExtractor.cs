using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// F-4 Phase 6 (2026-11-02) — scans a window of recent inner thoughts for
/// mentions of canonical-cast people (non-Mark) and returns short general
/// <see cref="PersonTrope"/>s. The caller merges tropes into
/// <see cref="CharacterStateDoc.KnownPeople"/>.
///
/// <para>
/// <b>Confabulation discipline:</b> extractor is told the canonical cast
/// prose + the primary contact name to exclude; it must emit names only
/// from that cast, and tropes must be short general patterns (not events).
/// Caller does defense-in-depth: emitted names are filtered against a
/// derived allowlist before any state write, so an LLM slippage can't
/// land a fabricated person on a canonical slot. See issue #185.
/// </para>
///
/// <para>
/// <b>Failure contract:</b> transport/parse/timeout errors return an empty
/// list with a WARN log — fail-open, matching the discipline of
/// <see cref="IArcExtractor"/> and the Phase 5a provider.
/// </para>
/// </summary>
public interface IPersonTropeExtractor
{
    /// <summary>
    /// Extract person-trope mentions from <paramref name="recentThoughts"/>.
    /// </summary>
    /// <param name="recentThoughts">The reflection-window inner thoughts.</param>
    /// <param name="canonicalCast">
    /// Prose entries from <c>CharacterStateDoc.FamilyContext</c> plus
    /// <c>CanonicalContacts</c> names. Hands the extractor the raw lore
    /// so it recognizes the cast; the caller still filters emitted names
    /// against a derived allowlist for defense-in-depth.
    /// </param>
    /// <param name="primaryContactName">
    /// The primary contact's name (Mark). Excluded from the cast handed
    /// to the extractor and from the caller-side allowlist, since Phase 6
    /// is explicitly non-Mark relationships.
    /// </param>
    /// <param name="existingPeopleNames">
    /// Names already present in <c>KnownPeople</c>. Lets the extractor
    /// prefer an existing slot name over minting a casing variant
    /// ("Lila" vs "lila"). Case-insensitive match on the caller side.
    /// </param>
    Task<IReadOnlyList<PersonTrope>> ExtractAsync(
        IReadOnlyList<MemoryRecord> recentThoughts,
        IReadOnlyList<string>       canonicalCast,
        string                      primaryContactName,
        IReadOnlyList<string>       existingPeopleNames,
        CancellationToken           ct);
}

/// <summary>
/// One extracted mention of a canonical-cast person. The caller is
/// responsible for allowlist-filtering <see cref="Name"/> before writing
/// the trope to canonical substrate.
/// </summary>
/// <param name="Name">Canonical name of the person (must match the cast).</param>
/// <param name="Trope">Short general pattern (not an event).</param>
public sealed record PersonTrope(string Name, string Trope);
