# Agent-assisted installation

Give this runbook to a CLI coding agent in the app's source folder, or alongside
a complete, trusted Windows publish directory. The goal is a configured, running
app, not a list of settings for the user to click through.

## Copyable kickoff prompt

```text
Install/configure ADO PR View for me using INSTALL.md in this source tree.
Determine Windows versus Linux/WSL first. Discover and reuse my compatible
installation, dependencies, Windows Azure credentials, settings, and repositories
from the context I selected; do not scan my home or add every accessible repo.
Resolve my actual ADO identity and repository IDs with read-only requests.
Preserve my watched authors, preferences, custom Fix prompt/model requirements,
hidden PRs, credentials, browser profiles, and Copilot run/worktree state.
Back up and atomically merge configuration before launch; never overwrite an
unreadable file or write while a dashboard can autosave. Reconcile all relevant
dashboard instances and owned jobs before proposing any exit/update; do not
cancel work or restart an active session to install. Ask one focused question
only when discovery cannot resolve a choice or human consent is truly required.
Configure optional WSL Copilot only if wanted; viewing PRs does not require WSL.
Leave settings fields you do not recognize unchanged; do not add or remove them.
Do not use approvals, Fix, publishing, merge-preview restarts, PR writes or
worktree cleanup as installation tests. Preserve global shell/model/MCP config.
Install/update one shortcut and launch one real app, not mocks, when safe and
authorized. Verify its actual executable, effective settings, and live PR data
or a meaningful no-PR result; errors are blockers, not an empty-dashboard pass.
Report the launch/configuration paths, selected scope, and any remaining blocker.
```

If the user supplied additional constraints, they take precedence over defaults
in the examples. Do not repeatedly ask decisions already recorded in settings or
the current request. MFA, SSO, consent, security keys and Windows Hello may need
the human; explain the specific interaction, wait, then continue. Never promise
unconditional unattended authentication.

## Current support and distribution

- **Windows desktop only:** .NET 10 WPF, WebView2 Evergreen Runtime. The verified
  publish target below is `win-x64`; do not promise native ARM/Linux support.
- Reuse a compatible installed build or a user-supplied **complete** trusted
  publish tree. Otherwise build the selected source on Windows. This folder
  need not be a Git checkout; do not assume `git pull` is applicable.
- Tagged versions are published on the repository's GitHub **Releases** page as
  a self-contained `win-x64` per-user installer (`AdoPrView-v<version>-win-x64-setup.exe`),
  a portable ZIP of the same files, and a SHA-256 checksums file (section 6).
  Building this source on Windows remains supported. The files are not
  code-signed, so SmartScreen may warn. Do not invent a release URL or app
  command-line flags; the app's only flags are developer `--mock`,
  `--screenshot`, `--size`, `--eval` and `--eval-wait`, none of which are for
  installation.
- Do not read, copy, import or decrypt any credential or token file as part of
  setup.

## 1. Find the actual Windows host

Record the host OS, Windows user/session, source/publish path and intended
installation path before choosing commands. On Windows, use Windows PowerShell
5.1 or PowerShell 7 and Windows paths. These snippets do not require PowerShell 7.

```powershell
[Environment]::OSVersion.Platform
[pscustomobject]@{
    UserProfile = $env:USERPROFILE
    LocalAppData = $env:LOCALAPPDATA
    Programs = [Environment]::GetFolderPath('Programs')
}
```

An agent already in Linux must first distinguish native Linux from WSL. In WSL,
use supported Windows interop; a Linux username/home is not the Windows user:

```bash
uname -s
printf 'WSL distribution: %s\n' "${WSL_DISTRO_NAME-}"
command -v powershell.exe
powershell.exe -NoProfile -Command '[Environment]::OSVersion.Platform; $env:USERPROFILE; $env:LOCALAPPDATA; [Environment]::GetFolderPath("Programs")'
# Convert only an explicitly selected source path if it is needed on Windows:
wslpath -w "$PWD"
```

Require a real Windows desktop session and `Win32NT` from interop. Native Linux
without a usable Windows host/interop is an explicit platform blocker: do not
run WPF with Linux `dotnet`, install WSL to view PRs, assume a developer's user
path, or pretend a remote service session has shown a visible desktop window.
Run the remaining PowerShell on that Windows host as its intended user. WSL
Linux `az` authentication is not proof that Windows `cmd.exe /c az` is signed in.

## 2. Reuse scoped context and make updates safe

Start with the selected source/publish directory, an existing app shortcut and
`%LOCALAPPDATA%\AdoPrView\settings.json`. Inspect those known locations, not
recursive home/drive scans. Reuse an existing installation path/shortcut target;
for a source-folder installation the normal target is `dist\AdoPrView.exe`.
Read settings into memory without dumping the file to chat.

For repository scope, prefer existing configured repos and the user's selected
repo/remotes. Capture `git remote -v` / `git remote get-url <name>` output
privately: remote URLs can contain credentials. Normalize recognized ADO HTTPS
(`dev.azure.com`, `<org>.visualstudio.com`) or `ssh.dev.azure.com:v3/...` remotes
to organization/project/repository names. Never infer identity from a folder
name or whitelist all repos in an org. If relevant, read existing `az devops
configure --list` defaults only when that extension is already present; the app
does not need the Azure DevOps CLI extension. Resolve ambiguous tenant, account
or repository choices with one focused question. Do not invent watched authors.

**Before changing files or exiting an app:**

1. Enumerate `AdoPrView.exe` with `Get-CimInstance Win32_Process`, recording each
   exact path, PID and owner. Include other copies sharing this Windows user's
   app-data directory, including tray instances; never kill by process name.
2. Read the updated build's `CopilotRuns` records privately and reconcile their
   exact task IDs, run receipts, distro and worktree CWD against live `bgtask
   status <exact-name> --json` in the **recorded** WSL environment. Names alone
   and stale records do not establish ownership or inactivity.
3. Absence of new records is not proof that older apps have no jobs. For legacy
   launches, inspect only the known `~/.local/state/ado-prview/runs` receipts and
   matching `ado-prview-<run-id>` tasks/runner processes in the known distro(s).
   Do not start a stopped distro merely to test recovery. Unavailable WSL,
   unreadable receipts or uncertain live status is a blocker, not "inactive."
4. Older builds cancel their owned jobs on real Exit; updated builds detach
   followers, but startup can resume interrupted runs and clean safe completed
   worktrees. Inspect those pending effects before launch. Do not cancel,
   auto-resume, alter receipts or clean worktrees to make installation possible.
   If a live/uncertain run, pending recovery/cleanup, or lack of exit authority
   prevents a safe update, leave the current app/state alone and report it.
5. With permission and no such blocker, gracefully exit only the identified app
   instances, using tray **Exit** or an exact-window close. Ordinary X can hide
   to tray. Confirm exit and re-read settings after its final placement save.
   Do not force termination or copy over locked release files.

Even a configuration-only edit must wait until all same-user dashboard copies
are stopped: they share settings and may autosave. If an already-running,
compatible app needs **no changes**, verify/reuse it instead of launching a
second copy. Do not delete `CopilotRuns`, WSL run/task state, `WebView2`,
`PoPWebView2`, or credential files during setup.

## 3. Check/install only missing dependencies

Probe known command paths and the existing runtime, without upgrading working
tools just because a newer version exists:

```powershell
Get-Command dotnet.exe, az, git.exe -ErrorAction SilentlyContinue |
    Select-Object Name, Source
# Also check this known per-user SDK location if dotnet is not on PATH:
$LocalDotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
# Set $Dotnet to the verified Windows executable, not a Linux shim.
& $Dotnet --list-sdks
& $Dotnet --list-runtimes
```

Release downloads are self-contained: they need no .NET runtime or SDK, only
64-bit Windows 10 or later with the Microsoft Edge **WebView2 Evergreen
Runtime**, which every build requires and current Windows normally includes.
Source builds need a .NET 10 SDK. A complete framework-dependent publish only
needs the matching **x64 .NET 10 Desktop Runtime** (`Microsoft.WindowsDesktop.App`
and `Microsoft.NETCore.App`), not Visual Studio, Node, Git, an SDK or WSL.
Check the apphost's normal runtime discovery too: a runtime reachable only by a
temporary `DOTNET_ROOT` override is not proof the normal Start Menu EXE will run.

Use the [official .NET Windows installer](https://learn.microsoft.com/dotnet/core/install/windows)
if a runtime/SDK is missing. An agent can resolve the actual .NET 10 x64 installer
from Microsoft's download page, verify its Microsoft Authenticode signature,
then run the documented `/install /quiet /norestart` arguments with authorized
permissions; check its exit code (3010 means reboot required, not ready now).
For a non-admin **build SDK**, Microsoft's
[`dotnet-install.ps1`](https://learn.microsoft.com/dotnet/core/tools/dotnet-install-script)
supports `-Channel 10.0 -Quality GA -Architecture x64 -InstallDir <chosen-path>
-NoPath`. This does not register a system runtime or solve apphost discovery
for a shortcut automatically. Do not change global shell configuration or
require elevation when compatible existing/per-user dependencies suffice.

Detect WebView2 via the `pv` value (valid version above `0.0.0.0`) in these
known keys, not merely the presence of the Edge browser:

```powershell
$WebViewKeys = @(
    'HKCU:\Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
    'HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
)
$WebViewKeys | Where-Object { Test-Path -LiteralPath $_ } |
    ForEach-Object { Get-ItemPropertyValue -LiteralPath $_ -Name pv }
```

If missing, obtain Microsoft's Evergreen bootstrapper from its
[official distribution page](https://learn.microsoft.com/microsoft-edge/webview2/concepts/distribution#online-only-deployment),
verify its signature and use `MicrosoftEdgeWebview2Setup.exe /silent /install`.
The bootstrapper supports non-elevated/per-user installation; enterprise policy
can still require consent. Recheck the runtime, not just installer exit status.

Azure CLI is optional when a valid existing PAT is supplied. If CLI auth is
needed and Windows `az` is missing, the
[documented Windows package](https://learn.microsoft.com/cli/azure/install-azure-cli-windows)
is `winget install --exact --id Microsoft.AzureCLI`. Do not install the DevOps
extension just for this app. Respect installer/UAC/enterprise permission
boundaries; refresh the agent's **process** PATH after installation and verify
`cmd.exe /d /c az version`, since that is how the app resolves Azure CLI.

## 4. Verify auth and resolve repositories with GETs

The app's native token precedence is `AZURE_DEVOPS_EXT_PAT`, then
`AZURE_DEVOPS_PAT` when the first variable is absent, otherwise Windows
`az account get-access-token --resource 499b84ac-1321-427f-aa17-267ca6975798 -o json`.
Never print token output, put it in command arguments/settings, enable a
transcript around it, or send it to the UI. Reuse a valid session/PAT; do not
create a PAT or replace an existing credential automatically. If sign-in is
actually needed, use Windows `az login` (with the selected tenant if required)
and complete the human interaction. Do not change the default account blindly.

The following in-memory helper mirrors the app's auth and restricts credentialed
requests to the ADO HTTPS origin, with redirects disabled. Run it in the same
Windows user/environment that will launch the EXE; a PAT set only in this process
will not magically be available to future Start Menu launches. Prefer reusable
Windows Azure CLI sign-in or an explicitly approved existing secure environment
provisioning mechanism, not a token embedded in a shortcut/script.

```powershell
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$pat = [Environment]::GetEnvironmentVariable('AZURE_DEVOPS_EXT_PAT')
if ($null -eq $pat) { $pat = [Environment]::GetEnvironmentVariable('AZURE_DEVOPS_PAT') }
if (-not [string]::IsNullOrWhiteSpace($pat)) {
    $auth = 'Basic ' + [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes(':' + $pat))
} else {
    $tokenJson = & az account get-access-token --resource 499b84ac-1321-427f-aa17-267ca6975798 -o json
    if ($LASTEXITCODE -ne 0) { throw 'Windows Azure CLI authentication required.' }
    try { $token = ($tokenJson | ConvertFrom-Json -ErrorAction Stop).accessToken }
    catch { throw 'Azure CLI returned unreadable token data; contents withheld.' }
    if ([string]::IsNullOrWhiteSpace($token)) { throw 'No ADO access token returned.' }
    $auth = 'Bearer ' + $token
}
$handler = New-Object Net.Http.HttpClientHandler
$handler.AllowAutoRedirect = $false
$http = New-Object Net.Http.HttpClient($handler)
$http.Timeout = [TimeSpan]::FromSeconds(60)
function Get-AdoJson([string]$Url) {
    $uri = [Uri]$Url
    if ($uri.Scheme -ne 'https' -or $uri.Host -ne 'dev.azure.com' -or
        -not $uri.IsDefaultPort -or $uri.UserInfo) {
        throw 'Refusing to send ADO credentials outside the supported origin.'
    }
    $request = New-Object Net.Http.HttpRequestMessage([Net.Http.HttpMethod]::Get, $uri)
    $request.Headers.Add('Authorization', $auth)
    $request.Headers.Add('Accept', 'application/json')
    $response = $null
    try {
        $response = $http.SendAsync($request).GetAwaiter().GetResult()
        if ([int]$response.StatusCode -ne 200) {
            throw ('ADO GET failed: HTTP ' + [int]$response.StatusCode + '; not an empty result.')
        }
        $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        try { return ($body | ConvertFrom-Json -ErrorAction Stop) }
        catch { throw 'ADO returned invalid JSON; do not treat it as no PRs.' }
    } finally {
        if ($null -ne $response) { $response.Dispose() }
        $request.Dispose()
    }
}
```

Set `$CandidateRepos` to only the context-selected/discovered entries (each has
`org`, `project`, `repo` strings), not sample/internal defaults. For existing
settings, verify the intended configured scope as well as requested additions.
Resolve canonical names and IDs before writing:

```powershell
$RepoChecks = @{}
$VerifiedRepos = @(
    foreach ($candidate in $CandidateRepos) {
        $org = [Uri]::EscapeDataString($candidate.org)
        $project = [Uri]::EscapeDataString($candidate.project)
        $name = [Uri]::EscapeDataString($candidate.repo)
        $me = Get-AdoJson "https://dev.azure.com/$org/_apis/connectionData"
        $identity = [Guid]::Empty
        if (-not [Guid]::TryParse($me.authenticatedUser.id, [ref]$identity) -or
            $identity -eq [Guid]::Empty) { throw 'ADO authenticated identity is missing.' }
        $repo = Get-AdoJson "https://dev.azure.com/$org/$project/_apis/git/repositories/$name`?api-version=7.1"
        if (-not $repo.id -or -not $repo.project.id -or -not $repo.name -or -not $repo.project.name) {
            throw 'Repository identity could not be resolved.'
        }
        if (($repo.name -ne $candidate.repo -and $repo.id -ne $candidate.repo) -or
            ($repo.project.name -ne $candidate.project -and $repo.project.id -ne $candidate.project)) {
            throw 'Resolved repository/project does not match the selected scope.'
        }
        # Also prove PR-list access; never infer it from repository access alone.
        $id = [Uri]::EscapeDataString($repo.id)
        $active = Get-AdoJson "https://dev.azure.com/$org/$project/_apis/git/repositories/$id/pullrequests?searchCriteria.status=active&`$top=1000&api-version=7.1"
        if ($null -eq $active.value) { throw 'ADO PR list has no value array.' }
        $RepoChecks["$org/$project/$id"] = [pscustomobject]@{
            identity = $me.authenticatedUser; active = $active.value; webUrl = $repo.webUrl
        }
        [pscustomobject]@{
            org = $candidate.org; project = $repo.project.name; repo = $repo.name
            repoId = $repo.id; projectId = $repo.project.id
        }
    }
)
# Dispose $http/$handler and discard token/header variables when GET verification is finished.
```

Verify the returned `webUrl` against the selected canonical ADO repo URL too.
The app constructs its web URL from `org/project/repo`; it does not store a
`webUrl` setting. Identity is `authenticatedUser.id` **per org**, not a hardcoded
author/UPN or Azure subscription owner. Compare active PR `createdBy.id` to it;
the existing whitelist additionally matches display name, UPN or alias. Preserve
those authors and hidden keys. Empty `value: []` is valid; missing/invalid data,
401/203/403, inaccessible projects or policy-detail errors are not success.

## 5. Back up and atomically merge settings before launch

`Settings.cs` uses `JsonSerializerDefaults.Web` (camelCase). Supported fields:

| Field | Shape / new-install default |
|---|---|
| `repos` | Array of `{org, project, repo, repoId, projectId}`; verified selected scope |
| `whitelist` | String array; `[]` means only the actual authenticated user's PRs |
| `hidden` | String array of `repoId:prId`; preserve |
| `refreshSeconds` / `mergeCheckMinutes` | Integers; `120` / `10` |
| `notifications` | Boolean; `true` |
| `autoComplete` | `{mergeStrategy:"squash", deleteSourceBranch:true, transitionWorkItems:true}`; options, not an install-time action |
| `triageInstructions` | Optional custom Fix prompt; preserve verbatim |
| `wslDistro` | Optional distro name; null/blank uses WSL default |
| `wslRepoDirectories` | Object keyed by lowercase `org/project/repo`, values Linux source paths |
| `window` | Optional `{left, top, width, height, maximized}`; preserve |
| `reviewBot` | Optional `{name, authorId, organization}` of a review bot whose findings gate PRs in that ADO organization only; omit when the team has none |
| `fixSkill` | Optional local Copilot skill named by the default Fix prompt; preserve |
| `fixReviewerModels` | Optional string array of exact reviewer model names for the Fix prompt; preserve |

**Settings behavior of this build:** a missing file starts with no repositories;
an unreadable or invalid file is reported and the app exits without changing
it; fields it does not recognize are preserved on save. Older builds of this app
instead replace a missing/unreadable file with a built-in default repository and
drop unrecognized fields (including the three newer fields above) when they save.
So copy the updated build into place (section 6) **before** merging settings,
then create the shortcut and launch; do not launch an older build afterwards
against the merged file. Do not copy another user's
settings, whitelist or credential. Existing malformed/unsupported JSON is a
blocker, not permission to replace it. Preserve unknown root/nested fields when
merging.

Here is a self-contained merge of the GET-verified repos. It leaves existing
preferences and unknown fields in place, creates defaults **only for a new
file**, detects a conflicting write, keeps the replaced file as a backup, and
writes UTF-8 without a BOM on the same volume. Run only after the lifecycle/exit
gate above; its exclusive installer lock does not stop app autosave.

```powershell
$ErrorActionPreference = 'Stop'
function Assert-DashboardStopped {
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    foreach ($process in @(Get-CimInstance Win32_Process -Filter "Name='AdoPrView.exe'")) {
        $owner = Invoke-CimMethod -InputObject $process -MethodName GetOwnerSid
        if ($owner.ReturnValue -ne 0) { throw 'Cannot establish dashboard process owner; do not edit.' }
        if ($owner.Sid -eq $sid) { throw "Dashboard PID $($process.ProcessId) can autosave; do not edit." }
    }
}
function Set-JsonProperty($Object, [string]$Name, $Value) {
    $Object | Add-Member -MemberType NoteProperty -Name $Name -Value $Value -Force
}
Assert-DashboardStopped
if (@($VerifiedRepos).Count -eq 0) { throw 'No GET-verified selected repositories.' }
$dir = Join-Path $env:LOCALAPPDATA 'AdoPrView'
$path = Join-Path $dir 'settings.json'
[IO.Directory]::CreateDirectory($dir) | Out-Null
$lock = [IO.File]::Open((Join-Path $dir 'agent-install.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
$tmp = Join-Path $dir ('settings.agent-' + [Guid]::NewGuid().ToString('N') + '.tmp')
try {
    $existed = [IO.File]::Exists($path)
    $originalHash = $null
    if ($existed) {
        $originalHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        try { $settings = [IO.File]::ReadAllText($path) | ConvertFrom-Json -ErrorAction Stop }
        catch { throw 'Existing settings unreadable; preserved unchanged.' }
        if ($settings -isnot [pscustomobject] -or $settings.repos -isnot [Array]) {
            throw 'Unsupported settings shape; preserved unchanged.'
        }
    } else {
        $settings = [pscustomobject][ordered]@{
            repos = @(); whitelist = @(); hidden = @()
            refreshSeconds = 120; mergeCheckMinutes = 10; notifications = $true
            autoComplete = [pscustomobject]@{
                mergeStrategy = 'squash'; deleteSourceBranch = $true; transitionWorkItems = $true
            }
            triageInstructions = $null; wslDistro = $null
            wslRepoDirectories = [pscustomobject]@{}; window = $null
        }
    }
    $repos = @($settings.repos)
    foreach ($verified in $VerifiedRepos) {
        $key = "$($verified.org)/$($verified.project)/$($verified.repo)".ToLowerInvariant()
        $matches = @($repos | Where-Object {
            "$($_.org)/$($_.project)/$($_.repo)".ToLowerInvariant() -eq $key
        })
        if ($matches.Count -gt 1) { throw 'Existing duplicate repository entries need reconciliation.' }
        if ($matches.Count -eq 1) {
            foreach ($name in @('org', 'project', 'repo', 'repoId', 'projectId')) {
                Set-JsonProperty $matches[0] $name $verified.$name
            }
        } else { $repos += $verified }
    }
    Set-JsonProperty $settings 'repos' @($repos)
    $json = $settings | ConvertTo-Json -Depth 100
    $null = $json | ConvertFrom-Json -ErrorAction Stop
    $bytes = (New-Object Text.UTF8Encoding($false)).GetBytes($json)
    $stream = [IO.FileStream]::new($tmp, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) }
    finally { $stream.Dispose() }
    Assert-DashboardStopped
    if ($existed) {
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $originalHash) {
            throw 'Settings changed concurrently; abort and re-read.'
        }
        $backup = Join-Path $dir ('settings.before-agent-' + [Guid]::NewGuid().ToString('N') + '.json')
        [IO.File]::Replace($tmp, $path, $backup)
    } else {
        if ([IO.File]::Exists($path)) { throw 'Settings appeared concurrently; abort and re-read.' }
        [IO.File]::Move($tmp, $path)
    }
    if ([IO.File]::ReadAllText($path) -cne $json) { throw 'Configuration read-back mismatch; do not launch.' }
} finally {
    if ([IO.File]::Exists($tmp)) { [IO.File]::Delete($tmp) }
    $lock.Dispose()
}
```

Validate the rest of an existing schema before invoking the merge (array/string/
boolean/numeric types, non-null `autoComplete`, repo entries and WSL map). Do not
silently repair invalid user preferences. The snippet deliberately does not
remove old repos/authors, reset Hidden, change intervals, or rewrite prompts.
Make any requested additions of watched authors, WSL mappings, `reviewBot`,
`fixSkill` or `fixReviewerModels` **in memory before serialization** under the
same backup/atomic-write gate, not a second unlocked edit. Re-read and compare
the intended fields; do not log the full file.

### Optional review bot

Configure `reviewBot` only when the user or the team's documented setup names a
bot whose PR comment threads must be resolved. Do not guess one from PR history.
Resolve its identity ID with a GET of the threads of a PR it has commented on,
using the helper from section 4, and require exactly one matching author:

```powershell
# $Org, $Project, $RepoId and $PrId identify a PR the named bot has commented on;
# the bot's gate applies only to PRs in $Org.
$threads = Get-AdoJson "https://dev.azure.com/$([Uri]::EscapeDataString($Org))/$([Uri]::EscapeDataString($Project))/_apis/git/repositories/$([Uri]::EscapeDataString($RepoId))/pullRequests/$PrId/threads?api-version=7.1"
$botIds = @($threads.value | ForEach-Object { $_.comments } |
    Where-Object { $_.author.displayName -match [regex]::Escape($BotName) } |
    ForEach-Object { $_.author.id } | Sort-Object -Unique)
if ($botIds.Count -ne 1) { throw 'Review bot identity is missing or ambiguous; ask the user.' }
$ReviewBot = [pscustomobject]@{ name = $BotName; authorId = $botIds[0]; organization = $Org }
```

Without `reviewBot` the dashboard shows no bot requirement, warning or wait.
PRs in other organizations get no bot gate. A `reviewBot` without
`organization` is reported as an error and applied nowhere; add the organization
the bot actually reviews rather than guessing.


### Optional WSL Copilot

Leave it alone unless wanted/already configured; never install/enable it just to
view PRs. Use `wsl.exe --list --verbose` to identify the actual distro, then
read-only checks in that selected distro for the exact existing source checkout
and its matching ADO remote. Record the verified Linux path under
`wslRepoDirectories["org/project/repo"]` and distro under `wslDistro`; do not
substitute a Windows path or map a same-named unrelated repo.

Check Git, Copilot, `bgtask`, `flock`, `setsid` and `python3`, plus current
`copilot --help` / `bgtask --help`, using the same owned
`~/.config/bash/environment`/existing BASH_ENV as the runner. Do not overwrite
those files, global model/MCP configuration, `triageInstructions`, `fixSkill` or
`fixReviewerModels`. If `fixSkill` or `fixReviewerModels` are set, check that the
named skill is present and tell the user the reviewer models must be available
to their Copilot CLI; do not substitute models or edit global preferences.
If CLI authentication needs interaction, let the user complete it in the actual
WSL environment; do not test it by starting Fix, a resume, or a new model task.
Missing optional tools/auth can remain a clearly reported optional blocker.

## 6. Publish/copy, shortcut and one real launch

**Release installer.** Download the setup EXE and checksums file of the
selected release, then compare `(Get-FileHash <file> -Algorithm SHA256).Hash`
with its checksums line before running it. After the section 2 safe-exit gate,
run setup interactively or with Inno Setup's standard `/VERYSILENT
/SUPPRESSMSGBOXES /NORESTART` and check its exit code. Setup installs per user
(no administrator rights) to `%LOCALAPPDATA%\Programs\ADO PR View` and creates
or repoints the per-user Start Menu shortcut **ADO PR View**, the same shortcut
the manual route below uses. It refuses to run, changing nothing, while any
`AdoPrView.exe` is running, and it never installs, changes or removes
`%LOCALAPPDATA%\AdoPrView` (settings, token, browser profiles, Copilot run
history); uninstalling from Windows Settings leaves that data too. Then run the
section 5 settings merge with every dashboard still closed and launch from the
shortcut as below; skip the publish/copy and shortcut steps. The portable ZIP
holds the same files and can be used as a complete publish directory.

**Source build.** For a source build, set `$SourceRoot` to the verified Windows source directory
and `$Dotnet` to the chosen Windows SDK executable. Publish to a **new staging
directory**, not over a running app. Use the agent's normal supervised build
runner if available; check the actual publish exit code.

```powershell
$stage = Join-Path $env:TEMP ('ado-prview-publish-' + [Guid]::NewGuid().ToString('N'))
& $Dotnet publish (Join-Path $SourceRoot 'src\AdoPrView\AdoPrView.csproj') -c Release -r win-x64 --self-contained false -o $stage
if ($LASTEXITCODE -ne 0) { throw 'Publish failed; do not install partial output.' }
```

For a trusted complete publish directory, use it as `$stage` and skip the SDK/
build. Do not invent a GitHub download URL. After the safe-exit gate, back up
the old installed release and shortcut, copy the **whole** publish tree including
`wwwroot`, native loaders and runtime/dependency files to `$InstallDir`, and
compare relative file lists and SHA-256 hashes. Do not delete broad directories,
touch unrelated files, mix old/new release payloads or overwrite app-data state.
On a partial/locked copy, stop; restore the known backup while apps remain closed
or report the blocker. A successful build alone is not an installed update.
Once the copy is verified, and with every dashboard still closed, run the
section 5 settings merge, then continue with the shortcut and launch below.

Create/update a single per-user Start Menu shortcut. Preserve the existing
shortcut location when present; don't add duplicates or embed credentials.
The following normal EXE target assumes runtime discovery and persistent auth
have been checked for future shortcut launches too:

```powershell
$exe = Join-Path $InstallDir 'AdoPrView.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Installed EXE missing.' }
$shortcutPath = Join-Path ([Environment]::GetFolderPath('Programs')) 'ADO PR View.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $exe
$shortcut.WorkingDirectory = $InstallDir
$shortcut.Arguments = ''
$shortcut.IconLocation = "$exe,0"
$shortcut.Save()
# Reopen the shortcut and compare TargetPath/WorkingDirectory/Arguments.
# Recheck same-user instances; reuse one matching real instance, never duplicate.
Start-Process -FilePath $exe -WorkingDirectory $InstallDir
```

Do not run the last line when a matching instance is already running. New
installs use normal mode: **no `--mock`, screenshot, eval, or invented setup/
read-only flags**. Verify the actual installed path/PID, responsiveness and
visible real dashboard, then its first completed refresh, selected repos,
identity and status/error line. Use read-only UI inspection if available.
If you cannot inspect that outcome, report the unverified part rather than
claiming a ready dashboard merely because a process exists.

Compare live GET results with own/watched-author scope and hidden/filter state.
"No active PRs for the configured authors" is meaningful only after successful
identity/repo/list access and no dashboard repo/row errors. Reset only temporary
UI filters when appropriate, not the user's saved whitelist/hidden list.

**Runtime side effects are not an installation test:** normal polling already
performs the documented automatic stale merge-preview restarts; startup also
reconciles recorded Copilot sessions/cleanup. There is currently no flag that
makes a real launch strictly read-only or suppresses recovery. Explain these
existing behaviors before launch; if they are outside the user's authorization,
stop before launching instead of claiming they were disabled. Never click
Re-check Merge, approval, PoP, Auto, Publish or Fix to validate setup.

## HYPOTHETICAL EXAMPLE: teammates sharing one organization

> **Hypothetical example only.** It shows how a team whose repositories live in
> one organization can make setup repeatable. It is not a default; every value
> must still be verified with the read-only GETs above, and IDs are never copied
> from another person's settings.

Suppose a team works in organization `msazuredev`, project
`AzureForOperatorsIndustry`, repositories `azlocal-fabric`, `nc-1p-core` and
`nc-nexus-overlay`, and its PRs are reviewed by a bot whose display name
contains `Agency`. A teammate's agent would:

1. Prefer the repository the teammate selected (its ADO remote), plus any of the
   three the teammate says they work in. Do not add the others unasked.

   ```powershell
   $CandidateRepos = @(
       [pscustomobject]@{ org = 'msazuredev'; project = 'AzureForOperatorsIndustry'; repo = 'azlocal-fabric' }
   )
   ```

2. Run the section 4 GETs to resolve `repoId`, `projectId` and the teammate's
   own identity. The teammate's watched authors are their own choice; ask only
   if they want any.
3. Resolve the bot with the section 5 snippet using `$BotName = 'Agency'` and
   a PR in that repository the bot has commented on. Store the resulting
   `reviewBot` (its `organization` is `msazuredev`); never hardcode its ID in
   scripts or documents.
4. Optionally set the team's agreed reviewer models, for example
   `fixReviewerModels = @('claude-opus-5.5', 'gpt-6-astra')`, and `fixSkill` only
   when that skill is installed in the teammate's WSL Copilot.
5. For WSL Copilot, map `wslRepoDirectories['msazuredev/azureforoperatorsindustry/azlocal-fabric']`
   to the teammate's verified Linux checkout of that repository.

## Troubleshooting, resuming setup, and final report

| Symptom | Agent action |
|---|---|
| Linux-only / no Windows desktop | Report unsupported platform/host; preserve source and state. |
| Locked app / live or uncertain jobs | Leave running work intact; report exact non-secret PID/task/distro blocker. Resume from the safety gate, not cancellation. |
| Unreadable/unsupported settings | Preserve it and backup; ask before repair. Do not launch into fallback defaults. |
| Missing Desktop Runtime/WebView2 | Install only the missing compatible dependency with consent; recheck real EXE/shortcut discovery. |
| 401/203/403 or wrong identity | Reuse/repair Windows auth for the selected org/account; request only needed human sign-in/consent. Never call it zero PRs. |
| Empty dashboard | Check successful live lists, author IDs/whitelist, Hidden, filters, repo and row errors. |
| Optional Copilot unavailable | Keep PR viewing usable; report exact optional WSL/tool/auth limitation, no test Fix/resume. |
| Embedded PoP rejected by tenant | Human-only flow; explicit external Edge fallback. Never weaken tenant/TLS protections. |

On rerun, reuse the selected paths, valid auth, verified repositories and existing
shortcut; add no duplicate repos/apps. Re-read after a blocked/concurrent write
instead of blindly replaying old JSON. Keep rollback backups until the installed
app/configuration is verified. Don't remove run records or worktrees as cleanup
of an installer attempt.

Finish with: installed executable and shortcut locations; configuration/backup
paths; configured org/project/repo scope and preserved watched-author choices;
actual running PID/path and live refresh or verified no-PR outcome; optional WSL
status; and any remaining blocker or specific human interaction. Report actual
side effects, not imagined tests or capabilities.
