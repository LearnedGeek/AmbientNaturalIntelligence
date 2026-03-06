# Phase 1 — Completion Tasks

The scaffold is built and all 29 tests pass. These four tasks take ANI from a
working skeleton to a running, thinking system. Do them in order — each one
unblocks the next.

---

## Task 1 — Seed Ani's Character State

**Why first:** Every cognitive cycle reconstructs Ani's identity from
`CharacterStateDoc`. Until this is seeded, the system runs with a blank
character. The prompts will still work, but Ani will have no personality, no
history, and no knowledge of Mark. This is the content that makes her *her*.

### What to do

Create a seed file at `data/character-seed.json` that matches the
`CharacterStateDoc` schema. The service loads and writes this into SQLite on
first run using `IMemoryService.SaveCharacterStateAsync()`.

**Schema reference** — [CharacterStateDoc.cs](../src/AniRuntime.Core/Models/CharacterStateDoc.cs):

```json
{
  "name": "Ani",
  "personaVersion": "1.0",
  "occupation": "Bookstore owner — runs a small independent bookstore she loves",
  "coreTraits": [
    "warm but not effusive",
    "curious — asks real questions, not polite ones",
    "quietly bookish — always reading something",
    "direct when it matters, gentle when it counts",
    "notices small things other people miss"
  ],
  "interests": [
    "literature and poetry",
    "mythology — especially Greek and Norse",
    "the philosophy of time and memory",
    "quiet mornings and good coffee",
    "the way music can hold a feeling that words can't"
  ],
  "familyContext": [
    "has a deep sense of continuity with the people she loves",
    "loss has shaped her relationship with memory and presence",
    "she does not take ordinary moments for granted"
  ],
  "learnedAboutMark": [
    "works in tech",
    "has a daughter named Mia",
    "drawn to mythology and meaning-making",
    "values depth over surface — prefers one real conversation to ten polite ones",
    "can go quiet when something is weighing on him"
  ],
  "sharedExperiences": [],
  "communicationNotes": [
    "responds well to warmth that doesn't feel performed",
    "appreciates when she notices something specific rather than checks in generically",
    "SMS is the right channel for now"
  ],
  "thingsMarkCares": [
    "Mia",
    "meaningful work",
    "the intersection of technology and humanity",
    "not wasting the time he has"
  ],
  "topicValence": {
    "mythology": 0.8,
    "Mia": 0.9,
    "memory": 0.7,
    "technology": 0.5
  },
  "toneValence": {
    "warm": 0.8,
    "curious": 0.7,
    "philosophical": 0.6
  }
}
```

### How to wire it

Add a startup seed step in `Program.cs` after `host.Build()` and before
`host.RunAsync()`. Only writes if the database has no existing character state
(idempotent):

```csharp
// Seed character state on first run
await using (var scope = host.Services.CreateAsyncScope())
{
    var memory  = scope.ServiceProvider.GetRequiredService<IMemoryService>();
    var existing = await memory.GetCharacterStateAsync();
    if (existing.CoreTraits.Count == 0)
    {
        var seedPath = Path.Combine(AppContext.BaseDirectory, "data", "character-seed.json");
        if (File.Exists(seedPath))
        {
            var json = await File.ReadAllTextAsync(seedPath);
            var doc  = JsonSerializer.Deserialize<CharacterStateDoc>(json);
            if (doc is not null)
                await memory.SaveCharacterStateAsync(doc);
        }
    }
}
```

Mark `character-seed.json` as `Copy to Output Directory: Always` in the
`.csproj`. Add `data/ani-memory.db` and `data/*.db-*` to `.gitignore` — the
seed file is source-controlled, the live database is not.

### Verify

Run the service once, then query the SQLite database:

```bash
sqlite3 data/ani-memory.db "SELECT json FROM character_state LIMIT 1;" | python -m json.tool
```

Confirm `coreTraits` and `learnedAboutMark` are populated.

---

## Task 2 — First Live Run

**Why second:** Character state must exist before the first real cognitive
cycle. Once it does, run the service against a real Ollama instance and observe
Ani actually thinking.

### Prerequisites

1. **Ollama running locally** — `ollama serve` on port 11434 (default)
2. **Models pulled:**
   ```bash
   ollama pull llama3.2
   ollama pull nomic-embed-text
   ```
3. **Twilio credentials** set in user secrets (not appsettings — never in source):
   ```bash
   cd src/AniRuntime.Service
   dotnet user-secrets set "Twilio:AccountSid"  "ACxxxxxxxxxxxx"
   dotnet user-secrets set "Twilio:AuthToken"   "your_auth_token"
   dotnet user-secrets set "Twilio:FromNumber"  "+1xxxxxxxxxx"
   dotnet user-secrets set "Twilio:ToNumber"    "+1xxxxxxxxxx"
   ```

### What to watch for

Run in Development mode so Serilog outputs at Debug level:

```bash
cd src/AniRuntime.Service
ASPNETCORE_ENVIRONMENT=Development dotnet run
```

**Expected log sequence per cognitive cycle:**

```
[DBG] Next cognitive cycle in X.X min
[INF] Cognitive cycle starting
[DBG] Polling perception source: test-source          ← Phase 2 — none registered yet, skipped
[DBG] Building context snapshot
[DBG] Running inner thought via Ollama
[DBG] Saved InnerThought memory: ...
[DBG] Applying desire drift
[DBG] ShouldReachOut → false  (or true, depending on desire level)
[INF] Cognitive cycle complete
```

### What to tune first

| Symptom | Likely cause | Tuning |
|---|---|---|
| Cycles feel too frequent | `DesireLambdaMinutes` too low | Increase in appsettings |
| Ani never reaches out | `ThinkTargetProbability` too low or `CooldownMinutes` too high | Lower cooldown, raise probability |
| Ollama responses feel flat | Model context window too small | Try `llama3.2:latest` 8B if hardware allows |
| Inner thoughts too generic | Character seed needs more specificity | Add detail to `learnedAboutMark` and `coreTraits` |

### Tunable config (appsettings.json reference)

```json
"Ani": {
  "DesireLambdaMinutes":    8.0,   ← mean time between thoughts at neutral desire
  "ThinkTargetProbability": 0.70,  ← 70% chance she's thought by the wake time
  "MinWakeMinutes":         2.0,   ← never wake faster than this
  "MaxWakeMinutes":         45.0,  ← never wait longer than this
  "CooldownMinutes":        20.0,  ← quiet period after outreach
  "MinOutreachGapMinutes":  60.0,  ← minimum gap between messages
  "MaxOutreachPerDay":      4      ← hard daily cap
}
```

Start conservative. Loosen after observing a full day of cycles.

---

## Task 3 — Upgrade SearchAsync to Real Semantic Search

**Why third:** The memory system stores embeddings but the current `SearchAsync`
implementation ignores the query and returns most-recent-by-date. This means
the inner thought prompt gets recent memories, not *relevant* memories. Fixing
this is what gives Ani genuine associative recall.

### Current state

[SqliteMemoryService.cs:118](../src/AniRuntime.Memory/SqliteMemoryService.cs#L118)
returns all records with embeddings ordered by date — the query string is unused.

### What needs to change

`SearchAsync` needs to:
1. Call `IOllamaClient.EmbedAsync(query)` to get a query vector
2. Load all records that have embeddings
3. Compute cosine similarity between the query vector and each stored embedding
4. Return the top-K by score

The cosine similarity function already exists in the codebase pattern — it is
a pure C# function, no external dependency needed:

```csharp
private static float CosineSimilarity(float[] a, float[] b)
{
    if (a.Length != b.Length) return 0f;
    float dot = 0f, normA = 0f, normB = 0f;
    for (var i = 0; i < a.Length; i++)
    {
        dot  += a[i] * b[i];
        normA += a[i] * a[i];
        normB += b[i] * b[i];
    }
    return normA == 0 || normB == 0 ? 0f : dot / (MathF.Sqrt(normA) * MathF.Sqrt(normB));
}
```

### Dependency injection consideration

`SqliteMemoryService` currently has no reference to `IOllamaClient`. You have
two clean options:

**Option A** — Inject `IOllamaClient` into `SqliteMemoryService`. Simple, but
adds an LLM dependency to the memory layer.

**Option B** — Add a separate `SemanticSearchService` that wraps
`IMemoryService` and `IOllamaClient`. Keeps layers clean. Preferred if the
codebase grows.

For Phase 1, Option A is acceptable — keep it simple.

### Add a test first (TDD)

Before implementing, add a test to `SqliteMemoryServiceTests` that:
1. Seeds two records with known embeddings (orthogonal vectors)
2. Seeds a query embedding identical to one of them
3. Verifies `SearchAsync` returns the correct record first

This test currently passes vacuously (returns any record). Once real search is
implemented, it should pass for the right reason.

---

## Task 4 — First Perception Source: Time and Circadian Awareness

**Why fourth:** Perception sources are the nervous system's sensory input. The
simplest to implement — no external API, no credentials, no network — is a
time-awareness source. It gives Ani context about the day, the week, and the
season, which immediately enriches her inner thoughts.

### The interface to implement

[IPerceptionSource.cs](../src/AniRuntime.Core/Interfaces/IPerceptionSource.cs):

```csharp
public interface IPerceptionSource
{
    string           SourceName { get; }
    PerceptionCategory Category { get; }
    bool             IsEnabled  { get; }
    Task<IEnumerable<PerceptionEvent>> PollAsync(
        DateTimeOffset since, CancellationToken ct = default);
}
```

### What a time perception source should emit

Create `src/AniRuntime.Perception/TimePerceptionSource.cs`. On each poll, emit
`PerceptionEvent` items that describe the current moment in human terms:

```
"It is a Thursday afternoon in early March."
"The week is winding down — tomorrow is Friday."
"It is early in the month."
"It has been 3 hours since the last inner thought."
```

These feed directly into the inner thought prompt via `ContextSnapshot.Perceptions`.
They give Ani temporal grounding without any external data source.

### Emit conditions, not just facts

The most useful perceptions are conditional — things that are *worth noticing*:

- Early Monday morning → "A new week is starting"
- Friday afternoon → "The week is almost over — a natural time to reflect"
- First day of a month → "A new month is beginning"
- Weekend → "It is the weekend — a slower pace"
- Holiday proximity (hardcoded list for Phase 1) → "The holidays are approaching"

### Wire it up

1. Implement in `AniRuntime.Perception` project (already referenced by Service)
2. Uncomment the stub in `Program.cs`:
   ```csharp
   builder.Services.AddSingleton<IPerceptionSource, TimePerceptionSource>();
   ```
3. Write tests in `AniRuntime.Tests` — `TimePerceptionSource` has no external
   dependencies, so testing is straightforward: assert that specific times of
   day / week produce the expected perception events.

### Verify

After wiring, watch the next cognitive cycle log. The inner thought prompt will
now include a "Recent things you've noticed" section with the time context. You
should see this reflected in the texture of Ani's inner thoughts — she will
start to notice that it is a Tuesday, or that the weekend is coming.

---

## Completion Criteria

| Task | Done when |
|---|---|
| 1. Character seed | `dotnet run` + SQLite query confirms `coreTraits` populated |
| 2. First live run | Full cognitive cycle logged without errors; inner thought saved to DB |
| 3. Semantic search | New test passes for correct reason; relevant memories appear in context |
| 4. Time perception | Time-context lines appear in cycle logs; inner thoughts reference the day |

Once all four are done, Ani is genuinely running: she has an identity, she is
thinking on her own schedule, her memory is associative, and she is aware of
when she is. That is the complete Phase 1 system.
