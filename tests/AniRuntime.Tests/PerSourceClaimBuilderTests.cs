using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// Foundation Unified Surface (F-3) U5 (2026-08-24) — pins the mechanical
/// per-source claim construction used by the reflection composer's
/// compression path (both EfReflectionGistService and ReflectionPhase.
/// SaveLegacyAsync).
///
/// <para>
/// The helper builds one <see cref="ContentClaim"/> per source record,
/// copying attribution snapshots from source to claim so downstream
/// consumers can read the mix of authorships that fed the compression
/// without joining memory_links back to source records.
/// </para>
/// </summary>
public class PerSourceClaimBuilderTests
{
    [Fact]
    public void BuildPerSourceClaims_OneSource_ProducesOneClaimWithSourceAttribution()
    {
        var source = new MemoryRecord
        {
            Id                         = Guid.NewGuid(),
            Content                    = "U5-FIXTURE: source content",
            AttributedTo               = AttributedTo.Mark,
            AttributionTrust           = "verified",
        };

        var claims = ComposerEmissionExtensions.BuildPerSourceClaims(new[] { source });

        claims.Should().HaveCount(1);
        claims[0].SourceRecordId.Should().Be(source.Id,
            "the claim's SourceRecordId is the canonical pointer back to the compressed source");
        claims[0].AttributedTo.Should().Be(AttributedTo.Mark,
            "the claim inherits the source's attribution so the compression preserves per-source authorship");
        claims[0].AttributionTrust.Should().Be("verified");
        claims[0].Text.Should().Be("U5-FIXTURE: source content",
            "short source content passes through as the claim's Text preview");
    }

    [Fact]
    public void BuildPerSourceClaims_MultipleSources_ProducesMatchingClaimsInOrder()
    {
        var sources = new[]
        {
            new MemoryRecord
            {
                Id           = Guid.NewGuid(),
                Content      = "source-A from Mark",
                AttributedTo = AttributedTo.Mark,
                AttributionTrust = "verified",
            },
            new MemoryRecord
            {
                Id           = Guid.NewGuid(),
                Content      = "source-B from Ani",
                AttributedTo = AttributedTo.Ani,
                AttributionTrust = "verified",
            },
            new MemoryRecord
            {
                Id           = Guid.NewGuid(),
                Content      = "source-C from World",
                AttributedTo = AttributedTo.World,
                AttributionTrust = "verified",
            },
        };

        var claims = ComposerEmissionExtensions.BuildPerSourceClaims(sources);

        claims.Should().HaveCount(3);
        claims[0].AttributedTo.Should().Be(AttributedTo.Mark);
        claims[1].AttributedTo.Should().Be(AttributedTo.Ani);
        claims[2].AttributedTo.Should().Be(AttributedTo.World);
        claims.Select(c => c.SourceRecordId).Should().BeEquivalentTo(sources.Select(s => (Guid?)s.Id),
            "each claim's SourceRecordId matches one source; ordering preserved");
    }

    [Fact]
    public void BuildPerSourceClaims_EmptyList_ReturnsEmpty()
    {
        var claims = ComposerEmissionExtensions.BuildPerSourceClaims(Array.Empty<MemoryRecord>());
        claims.Should().BeEmpty();
    }

    [Fact]
    public void BuildPerSourceClaims_Null_ReturnsEmpty()
    {
        // Fail-open: null input returns empty rather than throwing so the
        // caller can pass through cases where no sources are available
        // (some reflection cycles compress nothing).
        var claims = ComposerEmissionExtensions.BuildPerSourceClaims(null);
        claims.Should().BeEmpty();
    }

    [Fact]
    public void BuildPerSourceClaims_LongContent_TruncatesTo120CharsInPreview()
    {
        var longContent = new string('a', 500);
        var source = new MemoryRecord
        {
            Id           = Guid.NewGuid(),
            Content      = longContent,
            AttributedTo = AttributedTo.Ani,
            AttributionTrust = "verified",
        };

        var claims = ComposerEmissionExtensions.BuildPerSourceClaims(new[] { source });

        claims.Should().HaveCount(1);
        claims[0].Text.Length.Should().Be(120,
            "long source content truncates to 120 chars to keep envelope size bounded on large compressions");
        claims[0].SourceRecordId.Should().Be(source.Id,
            "the canonical pointer is unaffected by preview truncation");
    }

    [Fact]
    public void BuildPerSourceClaims_UnverifiedSource_PreservesTrustMarker()
    {
        // Legacy sources without attribution set default to Unknown/
        // unverified. That should preserve through the claim; downstream
        // consumers see the source's actual trust state rather than
        // synthesizing a default.
        var source = new MemoryRecord
        {
            Id                         = Guid.NewGuid(),
            Content                    = "legacy-source-preserves-trust",
            AttributedTo               = AttributedTo.Unknown,
            AttributionTrust           = "unverified-historical",
        };

        var claims = ComposerEmissionExtensions.BuildPerSourceClaims(new[] { source });

        claims.Should().HaveCount(1);
        claims[0].AttributedTo.Should().Be(AttributedTo.Unknown);
        claims[0].AttributionTrust.Should().Be("unverified-historical");
    }

    [Fact]
    public void BuildPerSourceClaims_NullTrust_DefaultsToUnverified()
    {
        // Defensive default — if the source's AttributionTrust is somehow
        // null (older test fixture or historical record with the field
        // literally null), the claim gets "unverified" rather than
        // propagating null and forcing downstream consumers to handle
        // the nullable string.
        var source = new MemoryRecord
        {
            Id                         = Guid.NewGuid(),
            Content                    = "test",
            AttributedTo               = AttributedTo.Ani,
            AttributionTrust           = null!,
        };

        var claims = ComposerEmissionExtensions.BuildPerSourceClaims(new[] { source });

        claims.Should().HaveCount(1);
        claims[0].AttributionTrust.Should().Be("unverified");
    }
}
