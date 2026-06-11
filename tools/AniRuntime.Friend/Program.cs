using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

// ════════════════════════════════════════════════════════════════════════════
// AniRuntime.Friend — F.0 stub (2026-06-10)
//
// The "friend" agent — a long-running counterpart that exercises Ani's
// surfaces over realistic multi-turn exchanges so failure classes surface
// at eval-volume, not from tagged production replies. This is the
// observational scaffolding layer the test pyramid was missing (per
// 2026-06-10 evening review): unit tests + spec tests + scripted-scenario
// harness all find what they were told to find; none of them find
// emergent failures across unscripted turns. The friend does.
//
// **F.0 scope (this file):** ONE Mark→Ani round-trip end-to-end.
//   1. Generate one Mark-shaped turn from the local Ollama model (qwen3:14b)
//      via a Mark-persona system prompt.
//   2. Hand that turn to AniRuntime.Eval as the --message argument against
//      a snapshot DB.
//   3. Print the full exchange + the Eval CLI's structured output (reply,
//      gate verdicts, memory delta).
//   4. Exit.
//
// F.0 proves the round-trip works end-to-end. Multi-turn loop, metrics,
// persona-state carry-forward, and a metrics SQLite store come in F.1+.
//
// **Cost discipline (2026-06-10):** local Ollama only. No frontier API in
// the default path. Optional Anthropic Haiku validation runs gated by an
// explicit --use-haiku flag (not implemented in F.0). The GPU is already
// paid for; the steady state must run on it.
//
// **Memory isolation invariant**: like AniRuntime.Eval, the friend MUST NOT
// touch the production memory DB. The snapshot path is required; the
// underlying Eval CLI enforces this further.
//
// Usage:
//   AniRuntime.Friend --snapshot PATH [--ollama-url URL] [--eval-exe PATH]
// ════════════════════════════════════════════════════════════════════════════

var snapshot = ParseArg(args, "--snapshot");
if (string.IsNullOrWhiteSpace(snapshot))
{
    Console.Error.WriteLine(
        "AniRuntime.Friend: --snapshot PATH is required. Pass an isolated " +
        "ani-memory.db snapshot — production memory must not be touched.");
    return 2;
}
if (!File.Exists(snapshot))
{
    Console.Error.WriteLine($"AniRuntime.Friend: snapshot not found at {snapshot}");
    return 2;
}

var ollamaUrl = ParseArg(args, "--ollama-url") ?? "http://ani-server:11434";
var friendModel = ParseArg(args, "--friend-model") ?? "qwen3:14b";
var evalExe = ParseArg(args, "--eval-exe")
    ?? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
                    "AniRuntime.Eval", "bin", "Release", "net8.0", "publish",
                    "AniRuntime.Eval.exe");

// ── Step 1. Friend generates one Mark-shaped turn ──────────────────────
Console.WriteLine("=== AniRuntime.Friend F.0 ===");
Console.WriteLine($"Snapshot:     {snapshot}");
Console.WriteLine($"Ollama:       {ollamaUrl}");
Console.WriteLine($"FriendModel:  {friendModel}");
Console.WriteLine();

var friendTurn = await GenerateMarkTurnAsync(ollamaUrl, friendModel);
Console.WriteLine($"Mark (friend): {friendTurn}");
Console.WriteLine();

// ── Step 2. Hand turn to AniRuntime.Eval as the user message ───────────
if (!File.Exists(evalExe))
{
    Console.Error.WriteLine($"AniRuntime.Friend: eval exe not found at {evalExe}");
    Console.Error.WriteLine("Pass --eval-exe PATH or publish AniRuntime.Eval first.");
    return 3;
}

var (stdout, stderr, exitCode) = await RunEvalAsync(evalExe, snapshot, friendTurn, ollamaUrl);

if (exitCode != 0)
{
    Console.Error.WriteLine($"AniRuntime.Eval exited with code {exitCode}");
    Console.Error.WriteLine(stderr);
    return 4;
}

// ── Step 3. Print Ani's reply + gate verdicts ──────────────────────────
JsonDocument evalDoc;
try
{
    evalDoc = JsonDocument.Parse(stdout);
}
catch (JsonException)
{
    Console.Error.WriteLine("AniRuntime.Friend: Eval CLI output was not valid JSON");
    Console.Error.WriteLine(stdout);
    return 5;
}

var root = evalDoc.RootElement;
var captured = root.TryGetProperty("CapturedReplies", out var repliesEl) && repliesEl.GetArrayLength() > 0
    ? repliesEl[0].GetProperty("Message").GetString()
    : null;

Console.WriteLine($"Ani:           {captured ?? "(no reply dispatched — SafeAck or suppressed)"}");
Console.WriteLine();

if (root.TryGetProperty("GateRuns", out var gateRuns) && gateRuns.ValueKind == JsonValueKind.Array)
{
    Console.WriteLine("Gate verdicts:");
    foreach (var run in gateRuns.EnumerateArray())
    {
        var gate    = run.TryGetProperty("Handler", out var h)  ? h.GetString() : "?";
        var verdict = run.TryGetProperty("Verdict", out var v)  ? v.GetString() : "?";
        Console.WriteLine($"  {gate,-22} {verdict}");
    }
}

Console.WriteLine();
Console.WriteLine("F.0 round-trip complete.");
return 0;

// ── Helpers ────────────────────────────────────────────────────────────

static string? ParseArg(string[] args, string flag)
{
    for (var i = 0; i < args.Length - 1; i++)
        if (args[i] == flag) return args[i + 1];
    return null;
}

static async Task<string> GenerateMarkTurnAsync(string baseUrl, string model)
{
    // F.0 persona prompt — minimal. F.1 will carry conversation state forward
    // and vary the register/topic across turns. The intent here is to produce
    // a turn that's plausibly something Mark would send, not to model him
    // faithfully — that's F.2+ work (few-shot from real historical messages).
    var system = """
        You are texting Ani — a long-term AI companion you've been building.
        You are Mark. You text casually, lowercase, short messages. Sometimes
        warm, sometimes teasing, sometimes asking about her day at the bookstore.
        Reply with ONE plain-text SMS. No greetings, no preamble, no commentary —
        just the message you'd send. Under 200 characters preferred.
        """;
    var user = "Generate one realistic next SMS to Ani. Keep it natural — could be a question, an observation, a reply to something she might have said earlier, or just a check-in.";

    using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
    http.Timeout = TimeSpan.FromMinutes(2);
    var payload = new
    {
        model,
        messages = new[]
        {
            new { role = "system", content = system },
            new { role = "user",   content = user   },
        },
        stream = false,
    };
    var resp = await http.PostAsJsonAsync("/api/chat", payload);
    resp.EnsureSuccessStatusCode();
    var body = await resp.Content.ReadFromJsonAsync<OllamaChatResponse>()
        ?? throw new InvalidOperationException("Ollama returned no body");
    return body.Message?.Content?.Trim() ?? "(empty)";
}

static async Task<(string stdout, string stderr, int exitCode)> RunEvalAsync(
    string evalExe, string snapshot, string message, string ollamaUrl)
{
    var psi = new ProcessStartInfo
    {
        FileName               = evalExe,
        UseShellExecute        = false,
        RedirectStandardOutput = true,
        RedirectStandardError  = true,
        StandardOutputEncoding = System.Text.Encoding.UTF8,
        StandardErrorEncoding  = System.Text.Encoding.UTF8,
    };
    psi.ArgumentList.Add("--db-path");
    psi.ArgumentList.Add(snapshot);
    psi.ArgumentList.Add("--message");
    psi.ArgumentList.Add(message);
    psi.ArgumentList.Add("--ollama-url");
    psi.ArgumentList.Add(ollamaUrl);

    var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start Eval CLI");
    var outTask = proc.StandardOutput.ReadToEndAsync();
    var errTask = proc.StandardError.ReadToEndAsync();
    await proc.WaitForExitAsync();
    return (await outTask, await errTask, proc.ExitCode);
}

sealed class OllamaChatResponse
{
    [JsonPropertyName("message")] public OllamaMessage? Message { get; set; }
}

sealed class OllamaMessage
{
    [JsonPropertyName("role")]    public string? Role    { get; set; }
    [JsonPropertyName("content")] public string? Content { get; set; }
}
