namespace AniRuntime.Core.Interfaces;

public interface ISpeechToTextService
{
    Task<string> TranscribeAsync(Stream audio, CancellationToken ct = default);
}
