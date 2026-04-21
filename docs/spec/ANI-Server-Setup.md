# ANI Server Setup — Runbook

**Target:** Dedicated ANI server at `192.168.1.100`, Windows 11 Pro, domain-joined to `learnedgeek.com`.
**Audience:** Mark executing the migration. Copy-paste friendly. PowerShell commands unless marked `cmd`.
**Prerequisite:** Hardware is up, Windows 11 Pro installed, network cable connected, machine is on the domain. Sign in as an admin account to run the commands that require elevation.

This runbook takes the server from "freshly installed Windows" to "running ANI with CI/CD-driven auto-deploy" in one focused session. Execute top-to-bottom. Where a step says "optional — skip for initial cutover," genuinely skip it the first time; add later.

---

## Progress Tracker

Check off each step as you complete it. Use this as a bookmark if you need to pause and come back.

### Phase 1 — OS prerequisites *(server, as Administrator)*
- [x] 1.1 OpenSSH Server installed, running, firewall rule added (LAN-only port 22)
- [x] 1.1 Verified: `ssh mcarthey@192.168.1.100` connects from laptop
- [x] 1.2 .NET 8 SDK installed (`dotnet --list-sdks` shows 8.x)
- [x] 1.3 Git installed and on PATH
- [x] 1.4 Ollama installed (`ollama --version` works)
- [x] 1.4 Optional: `OLLAMA_MODELS` env var set + reboot
- [x] 1.5 Three models available on the server (`ani-v7-conversation`, `ani-v6-inner`, `nomic-embed-text`)
- [x] 1.5 Verified: `ollama run ani-v7-conversation "hi"` responds; `nvidia-smi` shows GPU usage

### Phase 2 — Repo + initial build *(server)*
- [x] 2.1 Repo cloned to `C:\ani\AmbientNaturalIntelligence`
- [x] 2.2 `dotnet build` succeeds
- [x] 2.2 `dotnet test` reports 527+ passing
- [x] 2.3 `dotnet publish` succeeds, `AniRuntime.Service.exe` exists in `publish\AniRuntime\`

### Phase 3 — GitHub Actions self-hosted runner *(server)*
- [x] 3.1 Runner token generated from GitHub Actions settings
- [x] 3.2 Runner downloaded, extracted, configured with labels `self-hosted,windows,ani-server`
- [x] 3.3 Runner installed as a Windows Service, running
- [x] 3.3 Runner appears as **Idle** in GitHub → Settings → Actions → Runners
- [x] 3.3 Runner account has sc.exe permissions on AniRuntime (default LocalSystem is fine)

### Phase 4 — Configuration + data migration *(laptop → server)*
- [x] 4.1 `appsettings.Development.json` copied to server
- [x] 4.2 **Cutover window BEGINS** — laptop AniRuntime service stopped; timestamp captured
- [x] 4.2 `ani-memory.db` copied to server
- [x] 4.2 `ani-emergence.db` copied to server
- [x] 4.2 Any other SQLite DBs copied

### Phase 5 — Install + start the service *(server)*
- [x] 5.1 Windows Service created (`sc.exe create AniRuntime ...`)
- [x] 5.1 Optional: automatic-restart-on-failure configured
- [x] 5.2 Service started; `Get-Service AniRuntime` reports Running
- [x] 5.3 Debug log shows first cycle; temporal gap perception fired
- [x] 5.3 First inner thought captured for research log
- [x] 5.4 Health endpoint returns JSON
- [x] 5.5 Twilio webhook pointed at new server
- [ ] 5.5 Inbound SMS verified from phone — **cutover window ENDS**; timestamp captured

### Phase 6 — VS Code Remote-SSH *(laptop)*
- [ ] 6.1 Remote-SSH extension installed
- [ ] 6.2 `ani-server` host added to SSH config
- [ ] 6.3 Connected successfully; repo opens as Remote workspace
- [ ] 6.3 Terminal opens a PowerShell session on the server
- [ ] 6.3 Log file opens with live tail

### Phase 7 — First auto-deploy *(end-to-end verification)*
- [x] 7.1 Trivial commit pushed to `main` *(multiple commits; first end-to-end green at `1a3bd4b`)*
- [x] 7.2 Deploy workflow triggered, picked up by self-hosted runner
- [x] 7.2 Workflow completed: build → test → stop → publish → start → health check
- [ ] 7.3 Ani responds to a text message post-deploy

### Phase 8 — Optional: WireGuard VPN *(later)*
- [ ] 8.1 WireGuard VPN server configured on UDM-SE
- [ ] 8.2 Client profile created for laptop
- [ ] 8.3 VPN client installed on laptop; connection verified
- [ ] 8.4 Remote-SSH from off-LAN location works

### Phase 9 — Optional: Hannah onboarding *(scheduled June 2026)*
- [ ] 9.1 `hkraemer@learnedgeek.com` created in Entra ID
- [ ] 9.2 Added to Interns security group
- [ ] 9.3 Remote-SSH access provisioned with scoped permissions

### Post-cutover research log entries
- [ ] Research log entry: cutover event (laptop last thought → server first thought)
- [ ] Research log entry: substrate-vs-state observation (any qualitative difference on new hardware?)
- [ ] Research log entry: temporal gap perception narration on first post-cutover cycle

---

## Phase 1 — OS prerequisites

Run PowerShell **as Administrator** for this whole phase.

### 1.1 Enable OpenSSH Server

```powershell
# Install the OpenSSH Server feature (built into Windows 11 Pro)
Add-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0

# Start and enable the service
Start-Service sshd
Set-Service -Name sshd -StartupType 'Automatic'

# Open port 22 to the LAN only (adjust the RemoteAddress to match your LAN subnet if different)
New-NetFirewallRule `
  -Name 'OpenSSH-Server-In-TCP-LAN' `
  -DisplayName 'OpenSSH Server (sshd) — LAN only' `
  -Enabled True -Direction Inbound -Protocol TCP `
  -Action Allow -LocalPort 22 `
  -RemoteAddress 192.168.1.0/24

# Confirm
Get-Service sshd
```

Test from your laptop (in a separate terminal): `ssh mcarthey@192.168.1.100` — first connection will prompt you to accept the host key.

### 1.2 Install .NET 8 SDK

```powershell
winget install Microsoft.DotNet.SDK.8 --accept-package-agreements --accept-source-agreements
```

Verify: `dotnet --list-sdks` — should show an 8.x entry.

### 1.3 Install Git

```powershell
winget install Git.Git --accept-package-agreements --accept-source-agreements
```

Restart the PowerShell session so `git` is on PATH.

### 1.4 Install Ollama

Download from https://ollama.com/download/windows and run the installer. After install, Ollama runs as a background service on port 11434.

Verify: `ollama --version`

**Optional — move Ollama models to a specific drive** (per existing project convention — models live on E: on the laptop):

```powershell
# Set the OLLAMA_MODELS environment variable at the system level
[System.Environment]::SetEnvironmentVariable('OLLAMA_MODELS', 'E:\OllamaModels', [System.EnvironmentVariableTarget]::Machine)
# Ollama service needs a restart to pick up the new location — easiest path: reboot
```

### 1.5 Pull the live Ollama models

Allow time for this — the three models together are several GB.

```powershell
ollama pull ani-v7-conversation
ollama pull ani-v6-inner
ollama pull nomic-embed-text
```

**Note:** `ani-v7-conversation` and `ani-v6-inner` are custom fine-tunes. If they are not in the public registry they must be copied from the laptop. On the laptop:

```powershell
# Get the laptop's Ollama models directory
echo $env:OLLAMA_MODELS
# Default is C:\Users\mcart\.ollama\models (or E:\OllamaModels if overridden)
```

Copy the entire `models` directory (especially the `blobs/` and `manifests/` subdirectories) to the corresponding location on the server. After copy, `ollama list` on the server should show the models.

Verify inference works:

```powershell
ollama run ani-v7-conversation "hi"
```

Should produce a response within a few seconds. In another PowerShell window during that call, run `nvidia-smi` — you should see VRAM usage confirming GPU acceleration.

---

## Phase 2 — Repo + initial build

### 2.1 Clone the repo

```powershell
# Use a stable path you are happy with long-term
New-Item -ItemType Directory -Force -Path 'C:\ani'
cd C:\ani
git clone https://github.com/LearnedGeek/AmbientNaturalIntelligence.git
cd AmbientNaturalIntelligence
```

(Use HTTPS with a GitHub Personal Access Token, or set up SSH keys on the server.)

### 2.2 First build

```powershell
dotnet restore tests/AniRuntime.Tests/AniRuntime.Tests.csproj
dotnet build tests/AniRuntime.Tests/AniRuntime.Tests.csproj --no-restore -c Release
dotnet test tests/AniRuntime.Tests/AniRuntime.Tests.csproj --no-build -c Release
```

All 527+ tests should pass. If they don't, stop and investigate before proceeding.

### 2.3 Publish the service

```powershell
dotnet publish src/AniRuntime.Service/AniRuntime.Service.csproj -c Release -o publish/AniRuntime
```

This produces the executable at `C:\ani\AmbientNaturalIntelligence\publish\AniRuntime\AniRuntime.Service.exe`.

---

## Phase 3 — GitHub Actions self-hosted runner

The runner is what makes push-to-main trigger auto-deploy. One-time setup.

### 3.1 Register a runner token

In your browser:
1. Go to `https://github.com/LearnedGeek/AmbientNaturalIntelligence/settings/actions/runners`
2. Click **New self-hosted runner**
3. Select **Windows** / **x64**
4. Copy the `./config.cmd` command — it includes a one-time registration token

### 3.2 Install the runner

```powershell
New-Item -ItemType Directory -Force -Path 'C:\actions-runner'
cd C:\actions-runner

# Download and extract the runner (GitHub provides exact commands; example shape:)
Invoke-WebRequest -Uri 'https://github.com/actions/runner/releases/download/vX.Y.Z/actions-runner-win-x64-X.Y.Z.zip' -OutFile actions-runner.zip
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::ExtractToDirectory("$PWD\actions-runner.zip", "$PWD")

# Register with the token from step 3.1
# Add labels so the deploy-ani.yml workflow targets this specific runner
./config.cmd `
  --url https://github.com/LearnedGeek/AmbientNaturalIntelligence `
  --token YOUR_TOKEN_HERE `
  --labels self-hosted,windows,ani-server `
  --unattended
```

### 3.3 Install the runner as a Windows Service

```powershell
cd C:\actions-runner
./svc.sh install      # on Windows this is actually svc.cmd / .ps1 — adjust
./svc.sh start
# On Windows, use:
# ./svc install
# ./svc start
```

Verify in GitHub: the runner should appear as **Idle** in the Actions settings.

**IMPORTANT:** the runner account must have permission to run `sc.exe` against the `AniRuntime` service. If you installed the runner under a non-admin account, either grant it `Full Control` on the service (`sc sdset AniRuntime ...`) or run the runner service as `LocalSystem` (default for `./svc install`).

---

## Phase 4 — Configuration + data migration

The live Ani needs two things that are not in git: secrets and accumulated state.

### 4.1 Copy `appsettings.Development.json`

On the laptop:

```powershell
# Location on the laptop
$src = 'E:\Documents\Work\dev\repos\AmbientNaturalIntelligence\src\AniRuntime.Service\appsettings.Development.json'
# Copy over SMB (requires file sharing enabled on the server's target directory, or use scp)
Copy-Item $src '\\192.168.1.100\ani\AmbientNaturalIntelligence\src\AniRuntime.Service\'
```

Or via scp from the laptop:

```powershell
scp "E:\Documents\Work\dev\repos\AmbientNaturalIntelligence\src\AniRuntime.Service\appsettings.Development.json" mcarthey@192.168.1.100:/C:/ani/AmbientNaturalIntelligence/src/AniRuntime.Service/
```

### 4.2 Stop ANI on the laptop and copy the SQLite DBs

**This is the cutover window begin.** From this point until the server service is running, ANI is offline.

On the laptop:

```powershell
# Stop the laptop service (if running as a service; otherwise just stop `dotnet run`)
sc stop AniRuntime

# Note the cutover timestamp — capture for the research log
Get-Date
```

Copy the DBs (paths from the laptop's `appsettings.json` — adjust as needed):

```powershell
scp "E:\path\to\ani-memory.db"     mcarthey@192.168.1.100:/C:/ani/AmbientNaturalIntelligence/src/AniRuntime.Service/
scp "E:\path\to\ani-emergence.db"  mcarthey@192.168.1.100:/C:/ani/AmbientNaturalIntelligence/src/AniRuntime.Service/
```

---

## Phase 5 — Install + start the service on the server

Still as Administrator on the server:

### 5.1 Install the Windows Service

```powershell
cd C:\ani\AmbientNaturalIntelligence

sc.exe create AniRuntime `
  binPath= "C:\ani\AmbientNaturalIntelligence\publish\AniRuntime\AniRuntime.Service.exe" `
  start= auto `
  DisplayName= "ANI Runtime"

# Optional — configure automatic restart on failure
sc.exe failure AniRuntime reset= 86400 actions= restart/30000/restart/60000/restart/120000
```

### 5.2 Start the service

```powershell
sc.exe start AniRuntime

# Wait a few seconds, then confirm
Get-Service AniRuntime
```

### 5.3 First-cycle log verification

Tail the debug log to see Ani's first cycle on the new hardware:

```powershell
$today = Get-Date -Format 'yyyyMMdd'
Get-Content "src\AniRuntime.Service\logs\ani-debug-$today.log" -Wait -Tail 50
```

**Watch for:**
- `Temporal gap detected: last InnerThought was Xh ago` — this should fire since Ani was stopped during the cutover
- The resulting perception event: `"Noticing: more than N hours have passed..."`
- The inner thought that emerges — this is research data, capture it for the research log
- Any SQLite schema migration warnings (should be none, since the DB file was copied byte-identical)

### 5.4 Health check

```powershell
Invoke-RestMethod -Uri http://localhost:5100/health -Method Get
```

Should return JSON with the service's status.

### 5.5 Point Twilio webhook at the new server

Update the Twilio webhook URL in the Twilio console to the new server. Short term this can still be ngrok pointing at `192.168.1.100:5100`, or a static ngrok URL, or (once Cloud Edge CE-2 ships) an Azure Functions endpoint that forwards to the server via Service Bus.

Verify by texting Ani's number from your phone and watching the log for the inbound perception.

**Cutover window ends here. Note the timestamp for the research log.**

---

## Phase 6 — VS Code Remote-SSH from the laptop

This is how your daily workflow happens post-migration.

### 6.1 Install the Remote-SSH extension

On the laptop's VS Code: install **Remote - SSH** (`ms-vscode-remote.remote-ssh`).

### 6.2 Add the server as an SSH host

`Ctrl+Shift+P` → `Remote-SSH: Open SSH Configuration File...` → choose the user-level config.

Add an entry:

```
Host ani-server
    HostName 192.168.1.100
    User mcarthey
    ForwardAgent yes
```

### 6.3 Connect and open the repo

`Ctrl+Shift+P` → `Remote-SSH: Connect to Host...` → `ani-server` → when connected, `File` → `Open Folder` → `C:\ani\AmbientNaturalIntelligence`.

VS Code is now operating server-side. Terminal opens a PowerShell session on the server. Log files open with live tail. Git operations run server-side.

Claude Code sessions (me) continue to run on the laptop because my memory folder lives there — that's fine, I can operate on server files through the Remote-SSH bridge transparently.

---

## Phase 7 — First auto-deploy (end-to-end verification)

Once everything above is green, test the CI/CD loop.

### 7.1 Trigger a deploy

Make a trivial commit on the laptop (or via Remote-SSH server-side):

```powershell
# Any trivial change — e.g., a whitespace tweak to a README
git commit -am "trigger first auto-deploy"
git push origin main
```

### 7.2 Watch the workflow

In your browser: `https://github.com/LearnedGeek/AmbientNaturalIntelligence/actions` — the **Deploy ANI to Server** workflow should start, pick up on your self-hosted runner, and step through build → test → stop → publish → start → health check.

### 7.3 Verify Ani is still alive

After the workflow finishes, text Ani from your phone. She should respond. The log should show a gap of ~1-2 minutes from the deploy window.

---

## Phase 8 — Optional: WireGuard VPN (for true mobility)

Schedule this for after the migration settles. Lets you reach the server from anywhere.

The UniFi Dream Machine SE supports WireGuard natively. Setup is:
1. UniFi Console → Settings → VPN → WireGuard → Create VPN Server
2. Configure client profiles for your laptop (and phone if useful)
3. Install the WireGuard client on the laptop
4. Connect — your laptop now sees `192.168.1.0/24` as if it were on LAN
5. Remote-SSH + `claude` both work transparently from anywhere

---

## Phase 9 — Optional: Hannah onboarding

Per the existing `learnedgeek-infra/CLAUDE.md` plan:
- Create `hkraemer@learnedgeek.com` in Entra ID
- Add to the `Interns` security group
- Provision Remote-SSH access to the server under that account with scoped permissions (read-only by default)

Schedule for June 2026 per the original plan.

---

## Rollback procedure

If the server deploy breaks something unrecoverable:

1. On the laptop, `git revert <bad-commit-sha>` and push. The deploy workflow runs again and deploys the reverted state.
2. OR: on the laptop, copy the latest server DBs back to the laptop, start ANI locally, point Twilio webhook back. ~15 minutes to reverse the migration entirely.
3. Tag the commit at the start of cutover as `server-cutover-YYYYMMDD` so rollback has a clear reference.

---

## Post-migration checklist

- [x] Laptop no longer running ANI (`Get-Service AniRuntime` on laptop returns "not installed" or disabled)
- [x] Server running ANI with auto-start, auto-restart-on-failure
- [x] Twilio webhook pointed at server
- [ ] First inbound SMS on new server verified
- [ ] Temporal gap perception fired on first post-cutover cycle (captured in research log) — gap was under 2h; didn't trip
- [x] Deploy workflow successfully executed end-to-end at least once (first green: `1a3bd4b`)
- [ ] VS Code Remote-SSH working from laptop
- [ ] Laptop reclaimed (nothing long-running tied to it) ✨

---

*Prepared Sunday April 19, 2026 while Mark was stepping away for the evening. Ready for copy-paste execution when morning-Mark has coffee.*
