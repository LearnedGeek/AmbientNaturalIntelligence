using System.Text;

namespace AniRuntime.Voice;

/// <summary>
/// Accumulates LLM tokens and yields sentence-sized text chunks for TTS synthesis.
/// Balances TTS quality (needs enough context for natural prosody) with latency
/// (don't wait for the full reply before starting audio generation).
/// </summary>
public class TokenBuffer
{
    private readonly StringBuilder _buffer = new();
    private readonly int _maxWords;
    private int _wordCount;

    public TokenBuffer(int maxWords = 20)
    {
        _maxWords = maxWords;
    }

    /// <summary>
    /// Add a token to the buffer. Returns the accumulated sentence when a boundary
    /// is detected (. ! ? — or word overflow), otherwise returns null.
    /// </summary>
    public string? Add(string token)
    {
        if (string.IsNullOrEmpty(token))
            return null;

        _buffer.Append(token);

        // Count words (tokens containing whitespace often start a new word)
        if (token.Contains(' ') || token.Contains('\n'))
            _wordCount += token.Split(new[] { ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;
        else if (_buffer.Length == token.Length)
            _wordCount = 1; // first token

        // Check for sentence boundary at the end of the buffer
        var text = _buffer.ToString();
        var trimmed = text.TrimEnd();
        if (trimmed.Length == 0)
            return null;

        var lastChar = trimmed[^1];

        if (lastChar is '.' or '!' or '?')
        {
            // Skip ellipsis: don't trigger on "..." mid-sentence
            if (lastChar == '.' && trimmed.Length >= 3 && trimmed[^2] == '.' && trimmed[^3] == '.')
                return null;

            return FlushBuffer();
        }

        // Em-dash often signals a natural pause point
        if (trimmed.EndsWith("—") || trimmed.EndsWith("--"))
            return FlushBuffer();

        // Word overflow — flush even without punctuation to keep latency bounded
        if (_wordCount >= _maxWords)
            return FlushBuffer();

        return null;
    }

    /// <summary>
    /// Flush any remaining buffered text. Call at end of LLM generation.
    /// </summary>
    public string? Flush()
    {
        if (_buffer.Length == 0)
            return null;

        return FlushBuffer();
    }

    private string? FlushBuffer()
    {
        var text = _buffer.ToString().Trim();
        _buffer.Clear();
        _wordCount = 0;
        return text.Length > 0 ? text : null;
    }
}
