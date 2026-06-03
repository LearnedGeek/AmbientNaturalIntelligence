#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Run a JSON scenarios file through the PRODUCTION ANI Runtime pipeline
  via the AniRuntime.Eval CLI driver, against an isolated snapshot of
  ani-server's memory DB. Captures replies + gate telemetry per turn.

.DESCRIPTION
  Sibling to tools/anichat-sweep.ps1 (which hits Ollama directly with
  bare-pipeline persona). This wrapper drives the full production pipeline
  end-to-end: perception -> context -> inner thought -> desire -> reply
  composition -> J.5a invariants -> frontier verifier -> dispatch.

  Each scenario gets its own copy of the base snapshot so memory state
  persists across turns within a scenario but doesn't leak between
  scenarios. Production memory is NEVER touched — only the local snapshot
  copies see writes.

  Sweep flow:
    1. Snapshot ani-server's ani-memory.db once (or use --base-snapshot
       if you already have one)
    2. For each scenario: copy snapshot -> per-scenario eval DB
    3. For each turn in scenario: invoke AniRuntime.Eval against the
       per-scenario DB, capture the JSON output
    4. Write transcripts as JSON + markdown per scenario, plus a sweep
       summary

.PARAMETER ScenariosFile
  Path to JSON scenarios file (same shape as tools/scenarios/baseline.json).
  Default: tools/scenarios/baseline.json.

.PARAMETER BaseSnapshot
  Path to a pre-existing ani-memory.db snapshot. If omitted, this script
  takes a fresh snapshot from ani-server via SSH.

.PARAMETER OutputDir
  Where to write transcripts. Default: tools/scenarios/results-prod/<timestamp>/.

.PARAMETER OnlyScenario
  Run a single scenario by name.

.PARAMETER EvalExe
  Path to the AniRuntime.Eval binary. Default: tools/AniRuntime.Eval/bin/Release/net8.0/publish/AniRuntime.Eval.exe.
  If missing, the script publishes it.

.PARAMETER OllamaUrl
  Forwarded to the Eval driver. Default: http://ani-server:11434.

.EXAMPLE
  ./tools/anichat-sweep-prod.ps1
  ./tools/anichat-sweep-prod.ps1 -ScenariosFile tools/scenarios/baseline.json
  ./tools/anichat-sweep-prod.ps1 -BaseSnapshot E:/tmp/eval-snapshots/eval-snapshot-20260602-170928.db
  ./tools/anichat-sweep-prod.ps1 -OnlyScenario baseline-day
#>

[CmdletBinding()]
param(
    [string]$ScenariosFile = "tools/scenarios/baseline.json",
    [string]$BaseSnapshot,
    [string]$OutputDir,
    [string]$OnlyScenario,
    [string]$EvalExe = "tools/AniRuntime.Eval/bin/Release/net8.0/publish/AniRuntime.Eval.exe",
    [string]$OllamaUrl = "http://ani-server:11434",
    [int]$TurnTimeoutSeconds = 300
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $ScenariosFile)) {
    Write-Error "Scenarios file not found: $ScenariosFile"
    exit 1
}

if (-not (Test-Path $EvalExe)) {
    Write-Host "Eval binary not found at $EvalExe — publishing..." -ForegroundColor Yellow
    dotnet publish tools/AniRuntime.Eval/AniRuntime.Eval.csproj --nologo --verbosity quiet -c Release | Out-Null
    if (-not (Test-Path $EvalExe)) {
        Write-Error "Publish failed; eval binary still not at $EvalExe"
        exit 1
    }
}

if (-not $OutputDir) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $OutputDir = Join-Path (Split-Path $ScenariosFile -Parent) "results-prod/$stamp"
}

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

# ── Base snapshot ──────────────────────────────────────────────────────
if (-not $BaseSnapshot) {
    $snapshotStamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $remoteSnap = "C:/dev/ani-data/backups/eval-snapshot-$snapshotStamp.db"
    $BaseSnapshot = "/e/tmp/eval-snapshots/eval-snapshot-$snapshotStamp.db"

    Write-Host "Snapshotting ani-server -> $BaseSnapshot" -ForegroundColor Cyan
    New-Item -ItemType Directory -Path (Split-Path $BaseSnapshot -Parent) -Force | Out-Null

    ssh ani-server "C:\Tools\sqlite\sqlite3.exe C:/dev/ani-data/ani-memory.db `".backup $remoteSnap`""
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Remote snapshot failed."
        exit 1
    }

    scp "ani-server:$remoteSnap" $BaseSnapshot
    if ($LASTEXITCODE -ne 0) {
        Write-Error "scp failed."
        exit 1
    }
    Write-Host "Snapshot ready: $BaseSnapshot" -ForegroundColor Green
} elseif (-not (Test-Path $BaseSnapshot)) {
    Write-Error "Base snapshot does not exist: $BaseSnapshot"
    exit 1
} else {
    Write-Host "Using existing snapshot: $BaseSnapshot" -ForegroundColor DarkGray
}

# ── Load scenarios ─────────────────────────────────────────────────────
$rawJson = Get-Content $ScenariosFile -Raw -Encoding UTF8
$scenarios = @($rawJson | ConvertFrom-Json)
if (-not $scenarios -or $scenarios.Count -eq 0) {
    Write-Error "No scenarios found in $ScenariosFile"
    exit 1
}

if ($OnlyScenario) {
    $scenarios = @($scenarios | Where-Object { $_.name -eq $OnlyScenario })
    if ($scenarios.Count -eq 0) {
        Write-Error "Scenario '$OnlyScenario' not found"
        exit 1
    }
}

$summary = @()

foreach ($scenario in $scenarios) {
    Write-Host ""
    Write-Host "=== $($scenario.name) ===" -ForegroundColor Cyan
    if ($scenario.description) {
        Write-Host $scenario.description -ForegroundColor DarkGray
    }

    # Per-scenario DB copy — isolation between scenarios; memory state
    # persists across turns WITHIN this scenario.
    $scenarioDb = Join-Path $OutputDir "$($scenario.name).db"
    Copy-Item $BaseSnapshot $scenarioDb -Force

    $transcript = [System.Collections.ArrayList]@()
    $aborted = $false
    $turnsCompleted = 0
    $totalCapturedReplies = 0
    $totalSafeAcks = 0

    $assertionResults = [System.Collections.ArrayList]@()

    foreach ($turnItem in $scenario.turns) {
        # Turn can be a plain string OR an object with {user, assert}
        if ($turnItem -is [string]) {
            $userTurn = $turnItem
            $turnAssertions = $null
        } else {
            $userTurn = $turnItem.user
            $turnAssertions = $turnItem.assert
        }

        Write-Host ""
        Write-Host "user: $userTurn" -ForegroundColor White

        # Invoke Eval CLI via ProcessStartInfo.ArgumentList so each argv element
        # is treated atomically — Start-Process -ArgumentList @() has known
        # PowerShell quoting issues that split arg values on whitespace.
        # Output is JSON on stdout; logs go to stderr.
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = (Resolve-Path $EvalExe).Path
        $psi.UseShellExecute = $false
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
        $psi.StandardErrorEncoding = [System.Text.Encoding]::UTF8
        [void]$psi.ArgumentList.Add("--db-path")
        [void]$psi.ArgumentList.Add($scenarioDb)
        [void]$psi.ArgumentList.Add("--message")
        [void]$psi.ArgumentList.Add($userTurn)
        [void]$psi.ArgumentList.Add("--ollama-url")
        [void]$psi.ArgumentList.Add($OllamaUrl)

        try {
            $proc = [System.Diagnostics.Process]::Start($psi)
            $rawOut = $proc.StandardOutput.ReadToEnd()
            $rawErr = $proc.StandardError.ReadToEnd()
            $proc.WaitForExit()

            if ($proc.ExitCode -ne 0) {
                Write-Host "  (eval exit code $($proc.ExitCode))" -ForegroundColor Red
            }

            $evalJson = $null
            try {
                $evalJson = $rawOut | ConvertFrom-Json
            } catch {
                Write-Host "  (failed to parse eval JSON output)" -ForegroundColor Red
                $aborted = $true
                break
            }

            $reply = $null
            if ($evalJson.CapturedReplies -and $evalJson.CapturedReplies.Count -gt 0) {
                $reply = $evalJson.CapturedReplies[0].Message
                $totalCapturedReplies += $evalJson.CapturedReplies.Count
                Write-Host "ani: $reply" -ForegroundColor Cyan
            } else {
                Write-Host "ani: (no reply dispatched — possible SafeAck)" -ForegroundColor Yellow
                $totalSafeAcks++
            }

            # Pull any J.5a remediation lines from stderr (info-level logs)
            $remediations = @()
            if ($rawErr) {
                $remediationMatches = [regex]::Matches($rawErr, "J\.5[ah] gate Remediate[^\n]+", "IgnoreCase")
                foreach ($m in $remediationMatches) {
                    $remediations += $m.Value.Trim()
                }
            }

            # ── Phase D assertion evaluation ────────────────────────────
            $turnAssertionResults = [System.Collections.ArrayList]@()
            if ($turnAssertions) {
                $inserts = @($evalJson.MemoryDelta.InsertedSinceBefore)
                $insertContents = $inserts | ForEach-Object { $_.ContentPreview }
                $replyLower = if ($reply) { $reply.ToLower() } else { "" }

                if ($null -ne $turnAssertions.min_memory_inserts) {
                    $expected = [int]$turnAssertions.min_memory_inserts
                    $actual = $inserts.Count
                    $pass = $actual -ge $expected
                    [void]$turnAssertionResults.Add([pscustomobject]@{
                        Name = "min_memory_inserts >= $expected"
                        Pass = $pass
                        Actual = $actual
                    })
                }

                if ($turnAssertions.memory_content_any) {
                    $candidates = @($turnAssertions.memory_content_any)
                    $matched = $false
                    foreach ($candidate in $candidates) {
                        $needle = $candidate.ToString().ToLower()
                        foreach ($content in $insertContents) {
                            if ($content -and $content.ToLower().Contains($needle)) {
                                $matched = $true
                                break
                            }
                        }
                        if ($matched) { break }
                    }
                    [void]$turnAssertionResults.Add([pscustomobject]@{
                        Name = "memory_content_any of [$(($candidates -join ', '))]"
                        Pass = $matched
                        Actual = "inserts=$($inserts.Count); content-preview=[$(($insertContents | Select-Object -First 1) -join '')]"
                    })
                }

                if ($turnAssertions.reply_contains_any) {
                    $candidates = @($turnAssertions.reply_contains_any)
                    $matched = $false
                    foreach ($candidate in $candidates) {
                        $needle = $candidate.ToString().ToLower()
                        if ($replyLower.Contains($needle)) { $matched = $true; break }
                    }
                    [void]$turnAssertionResults.Add([pscustomobject]@{
                        Name = "reply_contains_any of [$(($candidates -join ', '))]"
                        Pass = $matched
                        Actual = if ($reply) { $reply.Substring(0, [Math]::Min(80, $reply.Length)) } else { "(no reply)" }
                    })
                }

                if ($turnAssertions.reply_excludes) {
                    $forbidden = @($turnAssertions.reply_excludes)
                    $violated = $null
                    foreach ($f in $forbidden) {
                        $needle = $f.ToString().ToLower()
                        if ($replyLower.Contains($needle)) { $violated = $f; break }
                    }
                    [void]$turnAssertionResults.Add([pscustomobject]@{
                        Name = "reply_excludes [$(($forbidden -join ', '))]"
                        Pass = ($null -eq $violated)
                        Actual = if ($violated) { "found: $violated" } else { "clean" }
                    })
                }

                if ($null -ne $turnAssertions.is_safeack) {
                    $expected = [bool]$turnAssertions.is_safeack
                    $actual = ($null -eq $reply -or $reply -eq "")
                    [void]$turnAssertionResults.Add([pscustomobject]@{
                        Name = "is_safeack = $expected"
                        Pass = ($actual -eq $expected)
                        Actual = $actual
                    })
                }

                # Print per-turn assertion results
                foreach ($r in $turnAssertionResults) {
                    $tag = if ($r.Pass) { "PASS" } else { "FAIL" }
                    $color = if ($r.Pass) { "Green" } else { "Red" }
                    Write-Host "  [$tag] $($r.Name) — $($r.Actual)" -ForegroundColor $color
                }
            }

            $insertedCount = if ($evalJson.MemoryDelta -and $evalJson.MemoryDelta.InsertedSinceBefore) {
                @($evalJson.MemoryDelta.InsertedSinceBefore).Count
            } else { 0 }

            [void]$transcript.Add([pscustomobject]@{
                role = "user"
                content = $userTurn
            })
            [void]$transcript.Add([pscustomobject]@{
                role = "assistant"
                content = $reply
                cycleCompleted = [bool]$evalJson.CycleCompleted
                evalError = $evalJson.Error
                remediations = $remediations
                syntheticSid = $evalJson.SyntheticSid
                memoryInsertCount = $insertedCount
                memoryStateBefore = $evalJson.MemoryStateBefore
                memoryStateAfter = $evalJson.MemoryStateAfter
                memoryDelta = $evalJson.MemoryDelta
                assertions = @($turnAssertionResults)
                gateRuns = $evalJson.GateRuns
            })

            foreach ($r in $turnAssertionResults) {
                [void]$assertionResults.Add($r)
            }

            $turnsCompleted++
        }
        catch {
            Write-Host "  (eval invocation failed: $_)" -ForegroundColor Red
            $aborted = $true
            break
        }
    }

    # ── Save transcripts ────────────────────────────────────────────────
    $transcriptJson = Join-Path $OutputDir "$($scenario.name).transcript.json"
    ConvertTo-Json -InputObject @($transcript) -Depth 10 | Set-Content -Path $transcriptJson -Encoding UTF8

    $transcriptMd = Join-Path $OutputDir "$($scenario.name).transcript.md"
    $md = "# $($scenario.name) (prod-pipeline)`n`n"
    if ($scenario.description) { $md += "_$($scenario.description)_`n`n" }
    if ($aborted) { $md += "> **Aborted after $turnsCompleted turn(s).**`n`n" }
    $md += "---`n`n"
    for ($i = 0; $i -lt $transcript.Count; $i += 2) {
        $u = $transcript[$i]
        $a = if ($i + 1 -lt $transcript.Count) { $transcript[$i + 1] } else { $null }
        $md += "**Mark:** $($u.content)`n`n"
        if ($a) {
            if ($a.content) {
                $md += "**Ani:** $($a.content)`n`n"
            } else {
                $md += "_(no reply dispatched — possible SafeAck)_`n`n"
            }
            if ($a.remediations -and $a.remediations.Count -gt 0) {
                $md += "**Remediations this turn:**`n"
                foreach ($r in $a.remediations) {
                    $md += "- ``$r```n"
                }
                $md += "`n"
            }
            if ($a.evalError) {
                $md += "**Eval error:** ``$($a.evalError)```n`n"
            }
        }
    }
    Set-Content -Path $transcriptMd -Value $md -Encoding UTF8

    $assertionsTotal = $assertionResults.Count
    $assertionsPassed = ($assertionResults | Where-Object { $_.Pass }).Count

    $summary += [pscustomobject]@{
        Name = $scenario.name
        TurnsTotal = $scenario.turns.Count
        TurnsRan = $turnsCompleted
        Aborted = $aborted
        CapturedReplies = $totalCapturedReplies
        SafeAcks = $totalSafeAcks
        AssertionsTotal = $assertionsTotal
        AssertionsPassed = $assertionsPassed
    }
}

# ── Sweep summary ─────────────────────────────────────────────────────
Write-Host ""
Write-Host "=== prod-sweep summary ===" -ForegroundColor Yellow
$summary | Format-Table -AutoSize

$summaryFile = Join-Path $OutputDir "_summary.json"
ConvertTo-Json -InputObject @{
    BaseSnapshot = $BaseSnapshot
    ScenariosFile = $ScenariosFile
    OllamaUrl = $OllamaUrl
    StartedAt = (Get-Date).ToString("o")
    Scenarios = $summary
} -Depth 5 | Set-Content -Path $summaryFile -Encoding UTF8

Write-Host ""
Write-Host "Transcripts: $OutputDir" -ForegroundColor DarkGray
Write-Host "Per-scenario DBs preserved at $OutputDir/*.db for inspection (gitignored)." -ForegroundColor DarkGray
