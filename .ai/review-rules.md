# AmbientNaturalIntelligence — AI review rules (for Serge)

Repo-owned review policy consumed by [Serge](https://huggingface.co/blog/huggingface/serge) via `.github/workflows/ai-review.yml`. Trigger a review by commenting `@askserge please review` on any PR.

Structured as **priority-ordered hunt list** and **explicit don't-flag categories** — the same shape used across LearnedGeek repos. Tuned for ANI's stack: C# .NET 8 runtime service + MAUI Android client + Eval CLI, sibling `learnedgeek-libs` for shared ML pieces, xunit tests with a regression-class harness, SQLite for local persistence, SonarCloud + coverage gate.

## What to hunt for (in priority order)

**1. Cross-file semantic races.** Highest-value catch, easiest to miss.
- Two or more independent entry points (endpoints, event handlers, background loops, UI submit paths) that call the same service method doing read-then-write on a shared row / counter / cache key. Ask: what happens if BOTH entry points fire concurrently on the same subject?
- `Max(x) + 1` / `SELECT then INSERT` / `SELECT then UPDATE` patterns without a unique constraint, transaction guard, or explicit ordering.
- Cache-key derivations where two callers compute the same key from stale input.
- Background loop tick + user-triggered path racing on the same emergence / memory / perception state.

**2. Contract mismatches** between interface docstrings/comments and implementation. Especially: empty-input contracts, null returns, cancellation propagation, "returns X on failure" claims the code doesn't honor.

**3. HttpClient / Task timeout propagation.** `HttpClient.Timeout` fires as `TaskCanceledException` with a **null** token — not the caller's. Any interface that promises "timeout → OperationCanceledException with caller's token" must map this case explicitly before the generic OCE rethrow.

**4. Exception catch specificity.** Never `catch (Exception ex)` broadly except in genuine log-and-rethrow. `InvalidOperationException` catches are especially dangerous — services throw IOE for BOTH user errors AND internal faults; catching broadly hides real failures as user-input errors.

**5. Async / concurrency footguns.**
- `async void` on anything other than event handlers (exceptions vanish silently).
- `.Result` / `.Wait()` on async methods (deadlock risk).
- Fire-and-forget `_ = SomeAsync()` in code that must not silently swallow failures.
- Loop tick methods that don't honor the cancellation token they were handed.

**6. Test category discipline.**
- `[Trait("Category", "RegressionOpen")]` marks a DESIGNED-TO-FAIL SPEC test tracking an open failure class (see CI's `--filter "Category!=RegressionOpen"`). Removing that trait or "fixing" the test without the corresponding production change silently closes a failure-class tripwire. Flag any PR that removes `RegressionOpen` from a test AND doesn't also touch the production code path the trait was guarding.
- New tests that assert on generated LLM output should either be deterministic (mocked LLM) or opt into a non-CI category — don't add nondeterministic tests to the default gate.

**7. Cross-repo API breaking changes** — csproj files reference the sibling `learnedgeek-libs` via `..\..\..\learnedgeek-libs\LearnedGeek.ML\...`. Renaming / moving / removing a public API in a `LearnedGeek.ML` type that ANI consumes is a breaking change even if the ANI compile succeeds against the current sibling checkout. Flag PRs that touch `learnedgeek-libs` public surface without a note about downstream consumers.

**8. MAUI cross-platform assumptions.**
- Platform-specific APIs (`DeviceInfo`, `SecureStorage`, `MediaPicker`, file paths) must be wrapped behind injectable interfaces for testability.
- Windows-only paths (`C:\...`, backslash separators) in code shared with `AniRuntime.MauiClient` (net10.0-android) — will break at runtime on the Android target.

**9. Log format placeholder promises a value the argument doesn't deliver.** Only flag when the label CLAIMS something the arg doesn't (e.g. label says `{NormalizedEmbedding}`, arg is the un-normalized value). Do NOT flag when a placeholder name simply differs from source variable name — that's the whole point of structured logging.

**10. Test assertions that don't verify what the test name promises.** `try/catch` around the SUT call can hide the exact behavior the test claims to verify.

## What NOT to flag

- **Defensive null checks on framework-guaranteed values** — DI-injected services, `HttpContext.Request` inside a middleware, `IHostApplicationLifetime.ApplicationStopping` token. Framework guarantees these exist; the null check is dead code.
- **Missing type annotations** the language doesn't require.
- **Naming preferences** ("could be renamed to be more descriptive"). Only flag names that are actively misleading.
- **Comment length or reformatting.** Only flag comments that promise behavior the code doesn't deliver.
- **Rewriting `foreach + if` to LINQ** unless the current form is genuinely harder to read.
- **Adding null checks that a fresh read of the code shows would never trigger.**
- **Style-only concerns not called out in this file.**

Trust internal code guarantees. Validate only at genuine trust boundaries (user input, external APIs, filesystem, cross-repo). A finding that would be a NOOP if applied is worse than no finding at all.

## Repo-specific notes

- **Stack:** C# .NET 8, xunit tests, ASP.NET Core (`AniRuntime.Dashboard`), Windows Service (`AniRuntime.Service`), MAUI Android (`AniRuntime.MauiClient`), CLI (`tools/AniRuntime.Eval`, `tools/AniRuntime.Figures`, `tools/AniRuntime.Friend`), PowerShell orchestration scripts under `tools/anichat-*.ps1`, SQLite for local persistence.
- **Deploys:** `deploy-ani.yml` targets the self-hosted `ani-server` runner and publishes into `C:\dev\repos\AmbientNaturalIntelligence\publish\AniRuntime` (Windows Service `AniRuntime`) and the parallel Eval publish dir. No staging environment — main = prod.
- **Sibling repo:** `learnedgeek-libs/LearnedGeek.ML` — cross-repo csproj references via relative paths. Breaking changes there flow into ANI on next build.
- **SonarCloud coverage** is a CI gate; coverage exclusions live in `ci.yml` (`tests/**`, `Program.cs`, `Migrations/**`, `MauiClient/**`, `Dashboard/**`).
- **Regression harness:** SPEC tests marked `Category=RegressionOpen` are designed-to-fail markers for currently-open issues; CI filters them out. Removing the trait is a deliberate act.

## Severity guide

- **BLOCKER** — bug, security issue, contract violation, will break in production
- **IMPORTANT** — real correctness issue that should be fixed before merge
- **MINOR** — style, comment, or naming issue that this file explicitly calls out

If the PR is clean, say so. Empty findings on a well-written PR is a valid and common outcome — do NOT invent findings to fill space.
