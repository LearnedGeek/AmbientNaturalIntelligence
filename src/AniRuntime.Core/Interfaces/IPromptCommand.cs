namespace AniRuntime.Core.Interfaces;

/// <summary>
/// A typed-input prompt command — Option A of the §5 Command-pattern
/// migration plan for <c>PromptBuilder</c>. Each implementation owns
/// the construction of one System/User prompt pair from its
/// strongly-typed input.
///
/// <para>
/// Motivation (Mark, May 18 2026): the pre-existing <c>PromptBuilder</c>
/// is a 1980-line static utility with 16 <c>BuildXxxPrompt</c> methods.
/// Adding a new prompt currently requires editing that single class,
/// violating OCP. Migrating each prompt to <see cref="IPromptCommand{TInput}"/>
/// turns "add a new prompt" into "add a new class + DI registration"
/// — the existing prompt classes never change.
/// </para>
///
/// <para>
/// The migration is incremental: the static <c>PromptBuilder.BuildXxx</c>
/// methods stay in place as thin wrappers that delegate to the matching
/// command, so existing call sites don't change shape while the
/// migration is in flight.
/// </para>
/// </summary>
/// <typeparam name="TInput">
/// The typed input shape for this prompt — usually a record or POCO
/// gathering the data the prompt's body needs.
/// </typeparam>
public interface IPromptCommand<in TInput>
{
    /// <summary>
    /// Build the (System, User) prompt pair from the typed input.
    /// </summary>
    PromptPair Build(TInput input);
}

/// <summary>
/// A System+User prompt pair. Replaces the historic
/// <c>(string System, string User)</c> tuple return type so command-side
/// signatures don't repeat tuple-positional information.
/// </summary>
public readonly record struct PromptPair(string System, string User)
{
    /// <summary>Implicit conversion to the legacy tuple shape for migration ergonomics.</summary>
    public static implicit operator (string System, string User)(PromptPair p) => (p.System, p.User);
}
