#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Iterative chat with ani-v7-conversation via Ollama on ani-server.

.DESCRIPTION
  Maintains conversation history in a JSON file across invocations.
  POSTs each message + history to Ollama's /api/chat. Modelfile carries
  the Ani persona system prompt — no need to inject one.

.PARAMETER Message
  The next message to send.

.PARAMETER Reset
  Clear the history file before sending (or before exiting if no Message).

.PARAMETER HistoryFile
  Where to persist conversation history. Default: $env:LOCALAPPDATA\anichat\history.json.

.PARAMETER Endpoint
  Ollama /api/chat URL. Default: http://ani-server:11434/api/chat.

.PARAMETER OllamaModel
  Model name to invoke. Default: ani-v7-conversation.

.PARAMETER SystemPrompt
  Optional override for the Modelfile-baked system prompt. Leave empty to use Modelfile default.

.PARAMETER MaxTokens / Temperature / TopP / RepeatPenalty
  Generation parameters. Defaults match the Modelfile.

.EXAMPLE
  ./tools/anichat.ps1 "how was your day?"
  ./tools/anichat.ps1 "tell me about that book"
  ./tools/anichat.ps1 -Reset
  ./tools/anichat.ps1 -HistoryFile .\scenarios\hurt-recovery.json "i'm having a rough night"
#>

[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Message,

    [string]$HistoryFile,

    [string]$Endpoint = "http://ani-server:11434/api/chat",

    [string]$OllamaModel = "ani-v7-conversation",

    [string]$SystemPrompt = "",

    [switch]$Reset,

    [int]$MaxTokens = 200,
    [double]$Temperature = 0.75,
    [double]$TopP = 0.9,
    [double]$RepeatPenalty = 1.15,

    [switch]$Quiet
)

if (-not $HistoryFile) {
    $HistoryFile = Join-Path $env:LOCALAPPDATA "anichat\history.json"
}

$historyDir = Split-Path $HistoryFile -Parent
if (-not (Test-Path $historyDir)) {
    New-Item -ItemType Directory -Path $historyDir -Force | Out-Null
}

if ($Reset) {
    if (Test-Path $HistoryFile) { Remove-Item $HistoryFile }
    if (-not $Quiet) { Write-Host "history reset: $HistoryFile" -ForegroundColor Yellow }
    if (-not $Message) { return }
}

if (-not $Message) {
    Write-Error "Usage: ./anichat.ps1 'your message'   (or -Reset to clear)"
    exit 1
}

# Load history (array of {role, content})
$history = @()
if (Test-Path $HistoryFile) {
    $raw = Get-Content $HistoryFile -Raw -Encoding UTF8
    if ($raw -and $raw.Trim()) {
        $parsed = $raw | ConvertFrom-Json
        if ($null -ne $parsed) {
            $history = @($parsed)
        }
    }
}

# Build messages array — Modelfile carries SYSTEM, only inject if caller overrode
$messages = @()
if ($SystemPrompt) {
    $messages += @{ role = "system"; content = $SystemPrompt }
}
foreach ($entry in $history) {
    $messages += @{ role = $entry.role; content = $entry.content }
}
$messages += @{ role = "user"; content = $Message }

$body = @{
    model    = $OllamaModel
    messages = $messages
    stream   = $false
    options  = @{
        temperature    = $Temperature
        top_p          = $TopP
        repeat_penalty = $RepeatPenalty
        num_predict    = $MaxTokens
    }
}

$bodyJson = $body | ConvertTo-Json -Depth 10 -Compress

try {
    $response = Invoke-RestMethod -Method Post -Uri $Endpoint -Body $bodyJson -ContentType "application/json" -ErrorAction Stop
}
catch {
    Write-Error "API call failed: $_"
    exit 1
}

$output = $response.message.content

if (-not $Quiet) {
    Write-Host ""
    Write-Host "ani: " -ForegroundColor Cyan -NoNewline
    Write-Host $output
}
else {
    Write-Output $output
}

# Append to history
$newHistory = @($history)
$newHistory += [pscustomobject]@{ role = "user"; content = $Message }
$newHistory += [pscustomobject]@{ role = "assistant"; content = $output }

ConvertTo-Json -InputObject $newHistory -Depth 10 | Set-Content -Path $HistoryFile -Encoding UTF8

if (-not $Quiet) {
    $turnCount = [Math]::Floor($newHistory.Count / 2)
    Write-Host ""
    Write-Host "[$turnCount turns]  history: $HistoryFile" -ForegroundColor DarkGray
}
