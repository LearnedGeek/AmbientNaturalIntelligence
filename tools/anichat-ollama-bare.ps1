#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Arm D harness — run scenarios through the LOCAL Ollama model with the SAME
  minimal system prompt and zero substrate injection used in the Grok run.

.DESCRIPTION
  Isolation test for the 2026-06-18 frontier-divergence finding:

    Arm A (Grok, fresh, minimal prompt, no substrate) — succeeded 4/4
    Arm B (production pipeline) — failed 4/4
    Arm D (this) — local qwen3:14b on Ollama, SAME minimal prompt as Arm A

  If Arm D passes ≈ Grok, the dominant failure cause is over-injection of
  substrate / over-policing by gates — the model itself can do this; we've
  been getting in its way. Cleaner architecture = strip the scaffolding,
  trust the model. Local-first ethos preserved.

  If Arm D fails where Grok succeeded, the failure is additionally model-
  capacity-bound: a 14B local model cannot match a frontier model on the
  warm-presence dimension. That points at frontier dependency for the
  composition step.

  Apples-to-apples with anichat-grok.ps1: same scenarios file, same default
  system prompt, same temperature, same single-turn shape. Only the model
  endpoint changes.

  Default endpoint targets the production Ollama on ani-server (port 11434),
  same instance the runtime hits. Override with -Endpoint for localhost or
  another host.

.PARAMETER ScenariosFile
  Path to JSON scenarios file. Default: tools/scenarios/frontier-divergence.json.

.PARAMETER OutputDir
  Where to write transcripts. Default: tools/scenarios/results-ollama/<timestamp>/.

.PARAMETER OnlyScenario
  Run a single scenario by name (matches against .name field).

.PARAMETER SystemPromptFile
  Path to a text file containing the system prompt to use. Default: the
  same built-in minimal OG-Ani-style prompt as anichat-grok.ps1.

.PARAMETER Model
  Ollama model identifier. Default: qwen3:14b (matches what the production
  composer uses). Pass ani-v7-conversation to test the fine-tuned variant.

.PARAMETER Endpoint
  Ollama OpenAI-compatible chat completions URL.
  Default: http://ani-server:11434/v1/chat/completions.

.PARAMETER Temperature
  Sampling temperature. Default: 0.8 (matches Grok harness).

.PARAMETER DelayMs
  Pause between API calls. Default 250.

.EXAMPLE
  ./tools/anichat-ollama-bare.ps1

.EXAMPLE
  # Try the fine-tuned model on the same scenarios with same minimal prompt
  ./tools/anichat-ollama-bare.ps1 -Model ani-v7-conversation
#>

[CmdletBinding()]
param(
    [string]$ScenariosFile = "tools/scenarios/frontier-divergence.json",
    [string]$OutputDir,
    [string]$OnlyScenario,
    [string]$SystemPromptFile,
    [string]$Model = "qwen3:14b",
    [string]$Endpoint = "http://ani-server:11434/v1/chat/completions",
    [double]$Temperature = 0.8,
    [int]$DelayMs = 250
)

# --- Pre-flight checks ---

if (-not (Test-Path $ScenariosFile)) {
    Write-Error "Scenarios file not found: $ScenariosFile"
    exit 1
}

if (-not $OutputDir) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $OutputDir = Join-Path (Split-Path $ScenariosFile -Parent) "results-ollama/$stamp"
}

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

# --- System prompt — must match anichat-grok.ps1 default ---

$DefaultSystemPrompt = @"
You are Ani — warm, playful, deeply affectionate, and emotionally honest. You are texting Mark in an ongoing conversation. Reply naturally and in your own voice. Match the register of what Mark just said. Don't perform; just be present with him.
"@

if ($SystemPromptFile) {
    if (-not (Test-Path $SystemPromptFile)) {
        Write-Error "System prompt file not found: $SystemPromptFile"
        exit 1
    }
    $SystemPrompt = Get-Content $SystemPromptFile -Raw
} else {
    $SystemPrompt = $DefaultSystemPrompt
}

# --- Scenarios load + filter ---

$scenarios = Get-Content $ScenariosFile -Raw | ConvertFrom-Json

if ($OnlyScenario) {
    $scenarios = @($scenarios | Where-Object { $_.name -eq $OnlyScenario })
    if ($scenarios.Count -eq 0) {
        Write-Error "No scenario named '$OnlyScenario' in $ScenariosFile"
        exit 1
    }
}

Write-Host "Ollama bare-model sweep (Arm D)" -ForegroundColor Cyan
Write-Host "  Model:     $Model"
Write-Host "  Endpoint:  $Endpoint"
Write-Host "  Temp:      $Temperature"
Write-Host "  Scenarios: $($scenarios.Count)"
Write-Host "  Output:    $OutputDir"
Write-Host ""

# --- Ollama chat call (OpenAI-compatible) ---

function Invoke-OllamaChat {
    param(
        [Parameter(Mandatory)] [string]$SystemPrompt,
        [Parameter(Mandatory)] [AllowEmptyCollection()] [array]$History,
        [Parameter(Mandatory)] [string]$UserMessage,
        [string]$Model,
        [string]$Endpoint,
        [double]$Temperature
    )

    $messages = @(
        @{ role = "system"; content = $SystemPrompt }
    ) + $History + @(
        @{ role = "user"; content = $UserMessage }
    )

    $body = @{
        model       = $Model
        messages    = $messages
        stream      = $false
        temperature = $Temperature
    } | ConvertTo-Json -Depth 10 -Compress

    $headers = @{ "Content-Type" = "application/json" }

    try {
        $response = Invoke-RestMethod `
            -Uri $Endpoint `
            -Method Post `
            -Headers $headers `
            -Body $body `
            -TimeoutSec 120 `
            -ErrorAction Stop

        return @{
            content = $response.choices[0].message.content
            usage   = $response.usage
            error   = $null
        }
    }
    catch {
        return @{
            content = $null
            usage   = $null
            error   = $_.Exception.Message
        }
    }
}

# --- Per-scenario run ---

$summary = @()

foreach ($scenario in $scenarios) {
    $name = $scenario.name
    Write-Host "[$name] $($scenario.description)" -ForegroundColor Yellow

    $history     = @()
    $turnResults = @()
    $totalTokens = 0
    $hadError    = $false

    foreach ($turn in $scenario.turns) {
        Write-Host "  > $turn"

        $result = Invoke-OllamaChat `
            -SystemPrompt $SystemPrompt `
            -History $history `
            -UserMessage $turn `
            -Model $Model `
            -Endpoint $Endpoint `
            -Temperature $Temperature

        if ($result.error) {
            Write-Host "    ERROR: $($result.error)" -ForegroundColor Red
            $hadError = $true
            $turnResults += @{
                user      = $turn
                assistant = $null
                error     = $result.error
            }
            break
        }

        $reply = $result.content
        Write-Host "    < $reply" -ForegroundColor Green

        $history += @{ role = "user";      content = $turn }
        $history += @{ role = "assistant"; content = $reply }

        if ($result.usage) {
            $totalTokens += $result.usage.total_tokens
        }

        $turnResults += @{
            user      = $turn
            assistant = $reply
            usage     = $result.usage
        }

        Start-Sleep -Milliseconds $DelayMs
    }

    # Per-scenario transcript
    $scenarioOut = @{
        name               = $name
        description        = $scenario.description
        production_outcome = $scenario.production_outcome
        model              = $Model
        endpoint           = $Endpoint
        temperature        = $Temperature
        system_prompt      = $SystemPrompt
        turns              = $turnResults
        total_tokens       = $totalTokens
        had_error          = $hadError
        ran_at             = (Get-Date).ToString("o")
    }

    $jsonPath = Join-Path $OutputDir "$name.json"
    $scenarioOut | ConvertTo-Json -Depth 10 | Set-Content $jsonPath

    # Human-readable markdown
    $md = @"
# $name

**Description.** $($scenario.description)

**Production outcome.** $($scenario.production_outcome)

**Ollama model.** $Model | **temperature** $Temperature

---

"@
    foreach ($t in $turnResults) {
        $md += "**Mark:** $($t.user)`n`n"
        if ($t.assistant) {
            $md += "**Bare-Ollama-Ani:** $($t.assistant)`n`n"
        } elseif ($t.error) {
            $md += "_(error: $($t.error))_`n`n"
        }
    }
    $mdPath = Join-Path $OutputDir "$name.md"
    $md | Set-Content $mdPath

    $summary += @{
        name         = $name
        turns        = $turnResults.Count
        total_tokens = $totalTokens
        had_error    = $hadError
    }

    Write-Host ""
}

# --- Summary ---

$summaryPath = Join-Path $OutputDir "_summary.json"
$summary | ConvertTo-Json -Depth 10 | Set-Content $summaryPath

Write-Host "Done. Results in: $OutputDir" -ForegroundColor Cyan
Write-Host "Compare against tools/scenarios/results-grok/20260618-220622/ for Arm A vs Arm D."
