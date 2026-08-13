using AniRuntime.Core.Interfaces;

namespace AniRuntime.Tests.Infrastructure;

/// <summary>
/// Deterministic stub of <see cref="IRegisterClassifier"/> for tests that
/// exercise <see cref="AniRuntime.Loops.EmotionalProcessor"/> or
/// <see cref="AniRuntime.Loops.InnerThoughtPhase"/> without a real Ollama
/// endpoint. Returns whatever the constructor was given (defaults to
/// <c>"Unclassified"</c>). Tests that need to verify register-conditional
/// downstream behavior can inject a different constant.
/// </summary>
public sealed class StubRegisterClassifier : IRegisterClassifier
{
    private readonly string _register;
    public StubRegisterClassifier(string register = "Unclassified") => _register = register;
    public Task<string> ClassifyAsync(string content, CancellationToken ct) => Task.FromResult(_register);
}
