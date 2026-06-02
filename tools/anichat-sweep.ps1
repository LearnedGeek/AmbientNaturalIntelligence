#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Run a JSON scenarios file through ani-v7-conversation and dump transcripts.

.DESCRIPTION
  Each scenario is a sequence of user turns. The harness loops through each
  scenario with an isolated history file, calling tools/anichat.ps1 per turn,
  and writes JSON + markdown transcripts to a timestamped output directory.

  Scenarios format (top-level JSON array):
    [
      {
        "name": "scenario-name",
        "description": "what this probes",
        "turns": ["user msg 1", "user msg 2", ...],
        "system_prompt_override": null
      },
      ...
    ]

.PARAMETER ScenariosFile
  Path to JSON scenarios file. Default: tools/scenarios/baseline.json.

.PARAMETER OutputDir
  Where to write transcripts. Default: tools/scenarios/results/<timestamp>/.

.PARAMETER OnlyScenario
  Run a single scenario by name (matches against .name field).

.PARAMETER DelayMs
  Pause between turns (politeness for Ollama). Default 200.

.EXAMPLE
  ./tools/anichat-sweep.ps1
  ./tools/anichat-sweep.ps1 -ScenariosFile tools/scenarios/baseline.json
  ./tools/anichat-sweep.ps1 -OnlyScenario karen-binding
#>

[CmdletBinding()]
param(
    [string]$ScenariosFile = "tools/scenarios/baseline.json",
    [string]$OutputDir,
    [string]$OnlyScenario,
    [int]$DelayMs = 200
)

if (-not (Test-Path $ScenariosFile)) {
    Write-Error "Scenarios file not found: $ScenariosFile"
    exit 1
}

if (-not $OutputDir) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $OutputDir = Join-Path (Split-Path $ScenariosFile -Parent) "results/$stamp"
}

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

$anichat = Join-Path $PSScriptRoot "anichat.ps1"
if (-not (Test-Path $anichat)) {
    Write-Error "anichat.ps1 not found next to harness at $anichat"
    exit 1
}

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

    $historyFile = Join-Path $OutputDir "$($scenario.name).history.json"
    if (Test-Path $historyFile) { Remove-Item $historyFile }

    $transcript = [System.Collections.ArrayList]@()
    $aborted = $false
    $turnsCompleted = 0

    foreach ($userTurn in $scenario.turns) {
        Write-Host ""
        Write-Host "user: $userTurn" -ForegroundColor White

        $params = @{
            Message     = $userTurn
            HistoryFile = $historyFile
            Quiet       = $true
        }
        if ($scenario.system_prompt_override) {
            $params.SystemPrompt = $scenario.system_prompt_override
        }

        $reply = $null
        try {
            $reply = & $anichat @params
        }
        catch {
            Write-Host "  (anichat threw: $_)" -ForegroundColor Red
            $aborted = $true
            break
        }

        # anichat returns the reply via Write-Output in -Quiet mode.
        # PowerShell may collect multi-line output as an array of strings.
        if ($reply -is [array]) { $reply = ($reply -join "`n") }

        if ([string]::IsNullOrWhiteSpace($reply)) {
            Write-Host "  (empty reply)" -ForegroundColor Red
            $aborted = $true
            break
        }

        Write-Host "ani: $reply" -ForegroundColor Cyan

        [void]$transcript.Add([pscustomobject]@{ role = "user"; content = $userTurn })
        [void]$transcript.Add([pscustomobject]@{ role = "assistant"; content = $reply })
        $turnsCompleted++

        if ($DelayMs -gt 0) { Start-Sleep -Milliseconds $DelayMs }
    }

    # Save transcripts
    $transcriptJson = Join-Path $OutputDir "$($scenario.name).transcript.json"
    ConvertTo-Json -InputObject @($transcript) -Depth 10 | Set-Content -Path $transcriptJson -Encoding UTF8

    $transcriptMd = Join-Path $OutputDir "$($scenario.name).transcript.md"
    $md = "# $($scenario.name)`n`n"
    if ($scenario.description) { $md += "_$($scenario.description)_`n`n" }
    if ($aborted) { $md += "> **Aborted after $turnsCompleted turn(s).**`n`n" }
    $md += "---`n`n"
    foreach ($t in $transcript) {
        if ($t.role -eq "user") {
            $md += "**Mark:** $($t.content)`n`n"
        }
        else {
            $md += "**Ani:** $($t.content)`n`n"
        }
    }
    Set-Content -Path $transcriptMd -Value $md -Encoding UTF8

    $replyChars = ($transcript | Where-Object { $_.role -eq "assistant" } | ForEach-Object { $_.content.Length } | Measure-Object -Sum).Sum
    $summary += [pscustomobject]@{
        Name         = $scenario.name
        TurnsTotal   = $scenario.turns.Count
        TurnsRan     = $turnsCompleted
        Aborted      = $aborted
        ReplyCharSum = $replyChars
    }
}

Write-Host ""
Write-Host "=== summary ===" -ForegroundColor Yellow
$summary | Format-Table -AutoSize

# Save summary
$summaryFile = Join-Path $OutputDir "_summary.json"
ConvertTo-Json -InputObject $summary -Depth 5 | Set-Content -Path $summaryFile -Encoding UTF8

Write-Host ""
Write-Host "Transcripts: $OutputDir" -ForegroundColor DarkGray
