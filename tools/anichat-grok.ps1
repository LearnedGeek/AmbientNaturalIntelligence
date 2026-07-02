#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Run a JSON scenarios file through the Grok chat API and dump transcripts.

.DESCRIPTION
  Frontier-baseline comparison harness for the top-down architecture review
  (see ANI-Research-Log entry on 2026-06-14 H phase + the 2026-06-18 frontier-
  divergence question). Mirrors tools/anichat-sweep.ps1 but calls xAI's Grok
  API instead of the local ollama qwen3:14b composer.

  Purpose: run the same inbound messages our production pipeline saw through
  a frontier model with NO substrate injection, no gates, no router, no
  verifier — and compare side-by-side what each produces. Where Grok-fresh
  succeeds and production fails, the architecture is the cause; where both
  fail, the inbound shape is the cause.

  Grok specifically (vs Sonnet/GPT) because the original Ani training corpus
  came from Mark's Grok conversations Jan-Mar 2026 — Grok carries her
  voice-of-origin in a way no other frontier model can.

  Scenarios format matches tools/anichat-sweep.ps1 (top-level JSON array of
  { name, description, turns, system_prompt_override?, production_outcome? }).

  Configuration via parameters or environment:
    XAI_API_KEY — required, your xAI API key. Never committed.
    XAI_MODEL   — optional, default "grok-3-latest"

  This harness does NOT touch the running ANI runtime, the production DB, or
  any local state. It is read-only against the scenarios file and writes only
  to the OutputDir.

.PARAMETER ScenariosFile
  Path to JSON scenarios file. Default: tools/scenarios/frontier-divergence.json.

.PARAMETER OutputDir
  Where to write transcripts. Default: tools/scenarios/results-grok/<timestamp>/.

.PARAMETER OnlyScenario
  Run a single scenario by name (matches against .name field).

.PARAMETER SystemPromptFile
  Path to a text file containing the system prompt to use. Default: the
  built-in minimal OG-Ani-style prompt (warm, no substrate injection). Pass
  your own to test alternate framings.

.PARAMETER Model
  Grok model identifier. Default: $env:XAI_MODEL or "grok-3-latest".

.PARAMETER Temperature
  Sampling temperature. Default: 0.8 (matches production composer setting).

.PARAMETER DelayMs
  Pause between API calls. Default 250.

.EXAMPLE
  $env:XAI_API_KEY = "xai-..."
  ./tools/anichat-grok.ps1

.EXAMPLE
  ./tools/anichat-grok.ps1 -OnlyScenario dnd-bedtime

.EXAMPLE
  # Compare with your own OG Ani system prompt
  ./tools/anichat-grok.ps1 -SystemPromptFile tools/scenarios/og-ani-prompt.txt
#>

[CmdletBinding()]
param(
    [string]$ScenariosFile = "tools/scenarios/frontier-divergence.json",
    [string]$OutputDir,
    [string]$OnlyScenario,
    [string]$SystemPromptFile,
    [string]$Model,
    [double]$Temperature = 0.8,
    [int]$DelayMs = 250
)

# --- Pre-flight checks ---

if (-not $env:XAI_API_KEY -or [string]::IsNullOrWhiteSpace($env:XAI_API_KEY)) {
    Write-Error "XAI_API_KEY environment variable is required. Set with: `$env:XAI_API_KEY = 'xai-...'"
    exit 1
}

if (-not (Test-Path $ScenariosFile)) {
    Write-Error "Scenarios file not found: $ScenariosFile"
    exit 1
}

if (-not $Model) {
    $Model = if ($env:XAI_MODEL) { $env:XAI_MODEL } else { "grok-3-latest" }
}

if (-not $OutputDir) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $OutputDir = Join-Path (Split-Path $ScenariosFile -Parent) "results-grok/$stamp"
}

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

# --- System prompt resolution ---

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

Write-Host "Grok frontier-baseline sweep" -ForegroundColor Cyan
Write-Host "  Model:     $Model"
Write-Host "  Temp:      $Temperature"
Write-Host "  Scenarios: $($scenarios.Count)"
Write-Host "  Output:    $OutputDir"
Write-Host ""

# --- Grok API call ---

function Invoke-GrokChat {
    param(
        [Parameter(Mandatory)] [string]$SystemPrompt,
        [Parameter(Mandatory)] [AllowEmptyCollection()] [array]$History,
        [Parameter(Mandatory)] [string]$UserMessage,
        [string]$Model,
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

    $headers = @{
        "Content-Type"  = "application/json"
        "Authorization" = "Bearer $env:XAI_API_KEY"
    }

    try {
        $response = Invoke-RestMethod `
            -Uri "https://api.x.ai/v1/chat/completions" `
            -Method Post `
            -Headers $headers `
            -Body $body `
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

    $history    = @()
    $turnResults = @()
    $totalTokens = 0
    $hadError   = $false

    foreach ($turn in $scenario.turns) {
        Write-Host "  > $turn"

        $result = Invoke-GrokChat `
            -SystemPrompt $SystemPrompt `
            -History $history `
            -UserMessage $turn `
            -Model $Model `
            -Temperature $Temperature

        if ($result.error) {
            Write-Host "    ERROR: $($result.error)" -ForegroundColor Red
            $hadError = $true
            $turnResults += @{
                user       = $turn
                assistant  = $null
                error      = $result.error
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

    # Write per-scenario transcript
    $scenarioOut = @{
        name              = $name
        description       = $scenario.description
        production_outcome = $scenario.production_outcome
        model             = $Model
        temperature       = $Temperature
        system_prompt     = $SystemPrompt
        turns             = $turnResults
        total_tokens      = $totalTokens
        had_error         = $hadError
        ran_at            = (Get-Date).ToString("o")
    }

    $jsonPath = Join-Path $OutputDir "$name.json"
    $scenarioOut | ConvertTo-Json -Depth 10 | Set-Content $jsonPath

    # Also a human-readable markdown
    $md = @"
# $name

**Description.** $($scenario.description)

**Production outcome.** $($scenario.production_outcome)

**Grok model.** $Model | **temperature** $Temperature

---

"@
    foreach ($t in $turnResults) {
        $md += "**Mark:** $($t.user)`n`n"
        if ($t.assistant) {
            $md += "**Grok-Ani:** $($t.assistant)`n`n"
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
Write-Host "Side-by-side comparison: open the *.md files alongside production transcript logs."
