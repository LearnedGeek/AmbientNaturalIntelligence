using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// Foundation Input (F-1) Phase 4 (2026-08-18) — verifies
/// <see cref="ConsciousSubstrateGist"/> implements <see cref="ISubstrateGistEnvelope"/>
/// correctly and that its <see cref="IProvenancedContent{T}"/> contract
/// fields match the sibling-implementor discipline captured after PR #112:
/// <c>CreatedAt</c> stored at construction (not recomputed per read),
/// consistent <c>SourceType</c>, <c>Producer</c> identity.
/// </summary>
public class SubstrateGistEnvelopeTests
{
    [Fact]
    public void ConsciousSubstrateGist_ImplementsSubstrateGistEnvelope_ExposesContentAsComposed()
    {
        var gist = new ConsciousSubstrateGist
        {
            Composed   = "recent-thread: bookstore chatter",
            TokenCount = 7,
        };
        ISubstrateGistEnvelope env = gist;

        env.Content.Should().Be("recent-thread: bookstore chatter");
        env.Producer.Should().Be("ConsciousSubstrateGistComposer");
        env.SourceType.Should().Be("gist.substrate");
        env.Treatment.Should().Be(SubstrateGistTreatment.ReferenceOnlyDoNotAdoptVoice,
            "Phase 4 gist envelopes must default to reference-only so the model knows not to lift phrasings from substrate");
    }

    [Fact]
    public async Task ConsciousSubstrateGist_CreatedAt_IsStableAcrossReads()
    {
        // Sibling-impl discipline captured after PR #112 CreatedAt-non-idempotent
        // finding. Every IProvenancedContent<T> implementor stores CreatedAt at
        // construction; this test pins that ConsciousSubstrateGist follows the
        // same pattern rather than recomputing UtcNow on each read.
        IProvenancedContent<string> env = new ConsciousSubstrateGist
        {
            Composed = "x",
        };

        var first = env.CreatedAt;
        await Task.Delay(20);
        var second = env.CreatedAt;

        second.Should().Be(first, "CreatedAt must be captured once at construction, not recomputed on each read");
    }

    [Fact]
    public void ConsciousSubstrateGist_Empty_IsEmpty_ImplementsEnvelope()
    {
        // Empty is a shared static — reading it as an envelope should still
        // work; consumers short-circuit on IsEmpty before rendering.
        var empty = ConsciousSubstrateGist.Empty;
        empty.IsEmpty.Should().BeTrue();

        ISubstrateGistEnvelope env = empty;
        env.Content.Should().BeEmpty();
        env.Treatment.Should().Be(SubstrateGistTreatment.ReferenceOnlyDoNotAdoptVoice);
    }

    [Fact]
    public void ConsciousSubstrateGist_SemanticKey_IsNull()
    {
        // Gist doesn't need embedding-based dedup; SemanticKey is null by
        // design (contract allows null per IProvenancedContent<T> doc).
        IProvenancedContent<string> env = new ConsciousSubstrateGist { Composed = "x" };
        env.SemanticKey.Should().BeNull();
    }
}
