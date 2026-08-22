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
        key.Should().Contain("AUTHORED=Ani");
        key.Should().Contain("TRUST=verified");
    }

    [Fact]
    public void BuildKey_HistoryHasUnverifiedHistoricalTurn_IncludesLoopWarning()
    {
        // The 12:04-shape corruption class. Framing block must warn the
        // LLM against treating quoted material inside these turns as
        // verified Mark utterances (the exact slip mechanism).
        var history = new[]
        {
            new ChatMessage("user", "hey")
            {
                AttributedTo = AttributedTo.Mark, AttributionTrust = "verified",
            },
            new ChatMessage("assistant", "I keep replaying how you said 'mmm baby you're back'")
            {
                AttributedTo = AttributedTo.Ani, AttributionTrust = "unverified-historical",
            },
        };

        var key = PromptBuilder.BuildChatHistoryAttributionKey(history);

        key.Should().Contain("unverified-historical",
            "the 12:04-shape trust class must be named explicitly in the framing");
        key.Should().Contain("DO NOT reason",
            "the loop-warning must instruct the model not to trust quoted material inside these turns");
    }

    [Fact]
    public void BuildKey_HistoryVerifiedOnly_OmitsLoopWarning()
    {
        // When no turn is unverified-historical, don't include the
        // loop-warning paragraph — reduces prompt noise for the common case.
        var history = new[]
        {
            new ChatMessage("user", "hey") { AttributedTo = AttributedTo.Mark, AttributionTrust = "verified" },
            new ChatMessage("assistant", "hi") { AttributedTo = AttributedTo.Ani, AttributionTrust = "verified" },
        };

        var key = PromptBuilder.BuildChatHistoryAttributionKey(history);

        key.Should().NotContain("unverified-historical",
            "conditional inclusion: only surfaces when at least one turn IS unverified-historical");
        key.Should().NotContain("DO NOT reason",
            "loop-warning is only for unverified-historical case");
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
}
