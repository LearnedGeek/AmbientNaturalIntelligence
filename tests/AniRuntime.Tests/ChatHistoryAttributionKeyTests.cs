using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.LLM;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// Foundation Attribution (F-2) Phase 1 P5 (2026-08-22) — verifies the
/// <see cref="ChatMessage"/> attribution fields and the
/// <see cref="PromptBuilder.BuildChatHistoryAttributionKey"/> invocation-time
/// framing block.
///
/// <para>
/// The framing block is the load-bearing intervention for the 12:04-shape
/// misattribution class from the 2026-08-20 substrate-feedback finding:
/// prepended to the system prompt whenever a chat-history array carries
/// per-turn attribution, it competes with the raw role signal at the LLM
/// input and tells the model NOT to reason from quoted material inside
/// <c>unverified-historical</c> Ani turns as if verified.
/// </para>
/// </summary>
public class ChatHistoryAttributionKeyTests
{
    // ── ChatMessage default fields (backward-compat) ─────────────────

    [Fact]
    public void ChatMessage_ConstructedWithRoleAndContentOnly_DefaultsToUnknownUnverified()
    {
        // Backward-compat: existing `new ChatMessage(role, content)`
        // calls throughout the codebase must continue to work; they get
        // the schema-default attribution state.
        var msg = new ChatMessage("user", "hey");

        msg.Role.Should().Be("user");
        msg.Content.Should().Be("hey");
        msg.AttributedTo.Should().Be(AttributedTo.Unknown);
        msg.AttributionTrust.Should().Be("unverified");
        msg.AttributedSourceRecordId.Should().BeNull();
    }

    [Fact]
    public void ChatMessage_InitializedWithAttribution_CarriesAllFields()
    {
        var sourceId = Guid.NewGuid();
        var msg = new ChatMessage("assistant", "mmm baby you're back")
        {
            AttributedTo             = AttributedTo.Ani,
            AttributionTrust         = "verified",
            AttributedSourceRecordId = sourceId,
        };

        msg.AttributedTo.Should().Be(AttributedTo.Ani);
        msg.AttributionTrust.Should().Be("verified");
        msg.AttributedSourceRecordId.Should().Be(sourceId);
    }

    // ── BuildChatHistoryAttributionKey ───────────────────────────────

    [Fact]
    public void BuildKey_EmptyHistory_ReturnsEmptyString()
    {
        PromptBuilder.BuildChatHistoryAttributionKey(Array.Empty<ChatMessage>())
            .Should().BeEmpty("no history → no key to inject");
    }

    [Fact]
    public void BuildKey_AllUnattributedTurns_ReturnsEmptyString()
    {
        // No point injecting an attribution key if none of the turns
        // actually carry attribution — the framing would be misleading.
        var history = new[]
        {
            new ChatMessage("user", "hey"),
            new ChatMessage("assistant", "hi"),
        };

        PromptBuilder.BuildChatHistoryAttributionKey(history)
            .Should().BeEmpty("no attributed turns → no framing key");
    }

    [Fact]
    public void BuildKey_HistoryHasAttribution_ReturnsFramingBlockWithRoleGuide()
    {
        var history = new[]
        {
            new ChatMessage("user", "hey") { AttributedTo = AttributedTo.Mark, AttributionTrust = "verified" },
            new ChatMessage("assistant", "hi babe") { AttributedTo = AttributedTo.Ani, AttributionTrust = "verified" },
        };

        var key = PromptBuilder.BuildChatHistoryAttributionKey(history);

        key.Should().Contain("CHAT-HISTORY ATTRIBUTION KEY");
        key.Should().Contain("role=user");
        key.Should().Contain("role=assistant");
        // PR #129 review-fix (Devin 🔍): key text now describes only what
        // IS in the payload — role→author deterministic mapping + general
        // warning about quoted material in prior Ani turns. Per-turn
        // AUTHORED/TRUST labels are NOT in Ollama's serialized wire
        // format so the key must not claim they are.
        key.Should().Contain("Mark", "role=user is the contact (Mark)");
        key.Should().Contain("your own prior output", "role=assistant is Ani's own output");
        key.Should().Contain("NOT be reasoned about",
            "loop-warning always present when history is attributed — closes 12:04 reasoning path");
    }

    [Fact]
    public void BuildKey_LoopWarning_AlwaysPresent_NotConditionalOnUnverifiedHistorical()
    {
        // PR #129 review-fix (Devin 🔍): after reworking the key to not
        // claim per-turn labels, the loop-warning applies uniformly —
        // any prior Ani turn might contain misattribution quotes regardless
        // of the individual trust value (which the payload can't carry
        // per-turn anyway). Warning covers the reasoning path in all cases.
        var history = new[]
        {
            new ChatMessage("user", "hey") { AttributedTo = AttributedTo.Mark, AttributionTrust = "verified" },
            new ChatMessage("assistant", "hi") { AttributedTo = AttributedTo.Ani, AttributionTrust = "verified" },
        };

        var key = PromptBuilder.BuildChatHistoryAttributionKey(history);

        key.Should().Contain("NOT be reasoned about",
            "loop-warning present even for verified-only history — closes reasoning path uniformly");
    }

    [Fact]
    public void BuildKey_MixedAttribution_HistoryHasSomeAttributedSomeNot_ProducesKey()
    {
        // Real-world case: some turns carry attribution (from ConversationMessage
        // conversion) and others don't (test fixtures, legacy code paths).
        // As long as ANY turn is attributed, the key is emitted.
        var history = new[]
        {
            new ChatMessage("user", "hey") { AttributedTo = AttributedTo.Mark, AttributionTrust = "verified" },
            new ChatMessage("assistant", "hi"),  // unattributed
        };

        var key = PromptBuilder.BuildChatHistoryAttributionKey(history);

        key.Should().NotBeEmpty("at least one attributed turn → key applies");
        key.Should().Contain("CHAT-HISTORY ATTRIBUTION KEY");
    }

    // ── WithAttributionKey — persona-preserve safety (PR #129 🔴 fix) ─

    [Fact]
    public void WithAttributionKey_EmptyBaseSystem_ReturnedUnchanged_PreservesModelfilePersona()
    {
        // Load-bearing fix: Phase K.1 lean composer path passes
        // System=string.Empty so Ollama falls back to the fine-tune's
        // baked Modelfile SYSTEM persona. A naive prepend would inject
        // a non-empty system message, REPLACING the baked persona with
        // just the framing key — stripping Ani's character from every
        // default conversation reply. This test guards the fix.
        var history = new[]
        {
            new ChatMessage("user", "hey") { AttributedTo = AttributedTo.Mark, AttributionTrust = "verified" },
            new ChatMessage("assistant", "hi") { AttributedTo = AttributedTo.Ani, AttributionTrust = "verified" },
        };

        var result = PromptBuilder.WithAttributionKey(string.Empty, history);
        result.Should().BeEmpty("empty base system MUST stay empty — Modelfile SYSTEM fallback is load-bearing");
    }

    [Fact]
    public void WithAttributionKey_WhitespaceBaseSystem_ReturnedUnchanged()
    {
        var history = new[]
        {
            new ChatMessage("user", "hey") { AttributedTo = AttributedTo.Mark, AttributionTrust = "verified" },
        };

        var result = PromptBuilder.WithAttributionKey("   ", history);
        result.Should().Be("   ", "whitespace base treated same as empty — never clobber baked persona");
    }

    [Fact]
    public void WithAttributionKey_NonEmptyBaseSystem_UnattributedHistory_ReturnedUnchanged()
    {
        var history = new[]
        {
            new ChatMessage("user", "hey"),
            new ChatMessage("assistant", "hi"),
        };

        var result = PromptBuilder.WithAttributionKey("You are Ani.", history);
        result.Should().Be("You are Ani.", "no attribution → no key injected → base returned unchanged");
    }

    [Fact]
    public void WithAttributionKey_NonEmptyBaseSystem_AttributedHistory_PrependsKey()
    {
        var history = new[]
        {
            new ChatMessage("user", "hey") { AttributedTo = AttributedTo.Mark, AttributionTrust = "verified" },
            new ChatMessage("assistant", "hi") { AttributedTo = AttributedTo.Ani, AttributionTrust = "verified" },
        };

        var result = PromptBuilder.WithAttributionKey("You are Ani.", history);

        result.Should().Contain("CHAT-HISTORY ATTRIBUTION KEY",
            "attributed history + non-empty base → key prepended");
        result.Should().EndWith("You are Ani.", "base system preserved after the key");
        result.Should().StartWith("[CHAT-HISTORY ATTRIBUTION KEY]",
            "key goes first, then blank line separator, then base");
    }
}
