using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;

namespace AniRuntime.LLM.Prompts;

/// <summary>Typed input for <see cref="ReactiveSharePromptCommand"/>.</summary>
public sealed record ReactiveSharePromptInput(
    CharacterStateDoc Character,
    string ItemSummary,
    EmotionalState? EmotionalState = null,
    OutreachFrame? Frame = null,
    string? SharedTopicGist = null);

/// <summary>
/// RSS reactive-share composition prompt. Theme N N.6 adds the optional
/// frame block + shared-topic anchor when the frame selector has fired.
/// When no frame is supplied, the prompt is structurally identical to the
/// pre-N.6 form.
/// </summary>
public sealed class ReactiveSharePromptCommand : IPromptCommand<ReactiveSharePromptInput>
{
    public PromptPair Build(ReactiveSharePromptInput input)
    {
        var character        = input.Character;
        var itemSummary      = input.ItemSummary;
        var emotionalState   = input.EmotionalState;
        var frame            = input.Frame;
        var sharedTopicGist  = input.SharedTopicGist;

        var contact = string.IsNullOrWhiteSpace(character.PrimaryContactName) ? "them" : character.PrimaryContactName;

        var moodBlock = emotionalState is not null ? PromptBuilder.BuildMoodInstruction(emotionalState) : string.Empty;
        var moodSection = moodBlock.Length > 0 ? $"\n\n            {moodBlock}" : "";

        var system = $"""
            You are {character.Name}, texting {contact} because you just saw something you think they'd care about.
            You're sharing it the way a real person shares a link or headline — casual, excited, natural.

            RULES:
            - 1-2 sentences. Thumb-typed phone text.
            - Be yourself — react to it, don't just forward it. Add your take.
            - Talk TO {contact}: "you", "your".
            - No poetry, no metaphors. Just "omg did you see this" energy.
            - Write ONLY the text message. No commentary, no quotation marks.
            - GROUNDING: Share the article and your reaction to it. Do NOT invent shared experiences
              around the article — no "remember when we watched that game" or "this reminds me of when
              we did X" unless the experience is documented in your context. React to the NEWS, not
              to a fabricated memory triggered by the news.{moodSection}

            Good examples (never copy word-for-word):
            wait did you see this?? the packers traded jordan love. WHAT.
            ok this recipe just showed up and i immediately thought of you
            have you heard about this? feels like something you'd nerd out over

            BAD (too formal, too poetic, fabricated shared memory, or just forwarding):
            "I thought you might find this article interesting" ← corporate email, not a text
            "The way stories find us when we need them most" ← poetic nonsense
            "remember when we watched that game together?" ← invented shared experience
            """;

        var sections = new List<string>();
        if (frame is not null && frame.FrameType != OutreachFrameType.None)
        {
            sections.Add($"[FRAME: {frame.FrameType}]");
            var anchorPreview = frame.Anchor.Length > 200
                ? frame.Anchor.Substring(0, 200)
                : frame.Anchor;
            sections.Add($"[ANCHOR] {anchorPreview}");

            sections.Add(frame.FrameType switch
            {
                OutreachFrameType.WorldPerception => string.IsNullOrWhiteSpace(sharedTopicGist)
                    ? "Compose by referencing this perception event you actually had. Use \"i saw...\" / external-content framing. There is no recent shared topic relevant here — frame as a clean external observation; do NOT pretend to be joining an in-flight conversation."
                    : $"Compose by referencing this perception event you actually had. Use \"i saw...\" / external-content framing. A recent shared topic from your conversation history is genuinely relevant — anchor to it (e.g. \"you mentioned [topic] yesterday — saw this on it\"). Recent shared topic gist: {sharedTopicGist}",
                OutreachFrameType.Shared          => "Compose by referencing this anchor as something Mark already said or did. Use \"remember when...\" / \"that thing you said about...\" framing.",
                OutreachFrameType.AniDomain       => "Compose by referencing this anchor as something from your bookstore world. Use \"the bookstore...\" / canonical-world framing.",
                OutreachFrameType.AniInterior     => "Compose by framing this honestly as your own interior — \"i was just thinking...\" / \"i had this thought that...\" Don't present interior content as shared observation.",
                _                                 => string.Empty,
            });
            sections.Add(string.Empty);
        }

        sections.Add($"You just saw this:");
        sections.Add(itemSummary);
        sections.Add(string.Empty);
        sections.Add($"Text {contact} about it — share it like you'd share something cool with someone you love:");

        var user = string.Join("\n", sections);
        return new PromptPair(system, user);
    }
}
