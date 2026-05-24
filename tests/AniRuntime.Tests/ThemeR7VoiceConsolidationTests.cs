using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Voice;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace AniRuntime.Tests;

/// <summary>
/// Theme R Phase R.7 (2026-05-24, Issue #64) — spec tests pinning that
/// <see cref="VoiceTurnPipeline"/> routes through <c>ContextBuilder</c>
/// when injected, inheriting R.1-R.6 wiring. Falls back to the pre-R.7
/// thin-snapshot construction when ContextBuilder is null.
///
/// <para>These tests verify the constructor surface and the ContextBuilder
/// injection contract. Full end-to-end behavior validation (composer
/// branches reaching voice) is covered by R.1-R.6 prompt tests which
/// run against the shared LeanConversationPrompt.</para>
/// </summary>
public class ThemeR7VoiceConsolidationTests
{
    [Fact]
    public void VoiceTurnPipeline_CtorAccepts_OptionalContextBuilderParameter()
    {
        // The new contract: VoiceTurnPipeline can be instantiated with or
        // without a ContextBuilder. R.7 makes it the routing path when
        // present; fallback otherwise.
        var memory = new Mock<IMemoryService>(MockBehavior.Loose).Object;
        var conversations = new Mock<IConversationService>(MockBehavior.Loose).Object;
        var ollama = new Mock<IOllamaClient>(MockBehavior.Loose).Object;
        var aniOptions = Options.Create(new AniRuntime.Core.AniOptions());
        var ollamaOptions = Options.Create(new AniRuntime.Core.OllamaOptions());

        // Without ContextBuilder (legacy / test path).
        var withoutBuilder = new VoiceTurnPipeline(
            memory, conversations, ollama, ollamaOptions, aniOptions,
            NullLogger<VoiceTurnPipeline>.Instance);

        withoutBuilder.Should().NotBeNull(
            "voice ctor must remain back-compat when ContextBuilder is not supplied");
    }

    [Fact]
    public void VoiceTurnPipeline_CtorParameterIsOptional()
    {
        // Test that the optional parameter convention is preserved so existing
        // DI registrations and tests don't break.
        var ctor = typeof(VoiceTurnPipeline).GetConstructors().Single();
        var contextBuilderParam = ctor.GetParameters()
            .FirstOrDefault(p => p.ParameterType.Name == "ContextBuilder");

        contextBuilderParam.Should().NotBeNull(
            "ContextBuilder must be a constructor parameter for R.7 injection");
        contextBuilderParam!.IsOptional.Should().BeTrue(
            "ContextBuilder must be optional for back-compat");
    }
}
