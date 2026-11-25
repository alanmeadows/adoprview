# Troubleshooting ADO PR View

This guide is for people using ADO PR View and for a Copilot or other coding
agent helping them. It covers what each feature needs on Windows and WSL, safe
checks, where the app writes logs, and how to file a useful bug report.

Rules for anyone (person or agent) following this guide:

- Run checks in the **same place the app runs**. The app is a Windows program.
  Unless a step says WSL, use Windows PowerShell, not a WSL terminal.
- Do not print, copy or paste tokens. Never use `gh auth token`,
  `gh auth status --show-token` or `az account get-access-token` output in a
  report.
- Do not delete sign-in, settings, profile or run folders to "reset" things.
  Each fix below names the smallest change.
- Do not test a fix by starting **Fix PR**, approving, PoP or posting comments.

## Explain says Copilot is not signed in

This is the most common Copilot problem. **Explain this PR**, **Ask Copilot
about this PR...**, **Review with a lens...** and **TikTok This PR** all sign in
the same way, so the same fix applies to all four.

### How Explain signs in

Explain does not use the `copilot` command you installed. It starts a Copilot
runtime that ships inside the app
(`runtimes\win-x64\native\copilot-runtime.exe`, or `win-arm64`). That runtime
uses its own private Copilot folder under `%LOCALAPPDATA%\AdoPrView`, and the
app removes token variables (`GH_TOKEN`, `GITHUB_TOKEN`,
`COPILOT_GITHUB_TOKEN`) from its environment.

The result: **the sign-in Explain can use is the Windows GitHub CLI (`gh`)**.
The runtime runs `gh auth token` and uses that account. The account must have
GitHub Copilot access.

These sign-ins are separate and are **not** used by Explain:

| Where you signed in | Stored where | Used by |
|---|---|---|
| VS Code GitHub Copilot extension | VS Code's own account storage | VS Code only |
| `copilot login` or `/login` in Windows PowerShell or Windows Terminal | Copilot CLI's folder (`%USERPROFILE%\.copilot`) and Windows Credential Manager | The `copilot` command on Windows, not Explain |
| `copilot login` inside WSL (including a Windows Terminal tab opened on Ubuntu or another distro) | That Linux user's home in that distro | **Fix PR** only |
| `gh auth login` inside WSL | That Linux user's home in that distro | Not Explain |
| **`gh auth login` in Windows PowerShell** | GitHub CLI for Windows | **Explain, questions, lens reviews, TikTok** |

So "I signed in to Copilot in VS Code and in Windows Terminal" can still leave
Explain signed out.

### What the message tells you

From the release after v0.1.4, the message names what the app found:

| Message contains | Meaning | Fix |
|---|---|---|
| `gh.exe was not found on the PATH this app started with` | The app cannot run GitHub CLI. Either it is not installed for Windows, or it was installed after the app (or Windows Explorer) started. | Install GitHub CLI for Windows (`winget install --id GitHub.cli`, or from <https://cli.github.com>). Run `gh auth login` in a **new** PowerShell window. Exit ADO PR View from the tray menu (closing the window only hides it) and open it from the Start Menu. If the message stays the same, sign out of Windows and back in so Explorer picks up the new PATH. |
| `gh.exe is on this app's PATH but did not provide a signed-in account` | GitHub CLI runs, but has no usable sign-in. | In Windows PowerShell run `gh auth login`, then `gh auth status`. Choose **Retry Explain**; no app restart is needed. |
| `Copilot is not signed in. Sign in with the native Windows Copilot CLI` | v0.1.4 or earlier. This older text was misleading. | Same checks as the two rows above. |

If `gh auth status` is green but Explain still fails, see
[Known limitations](#known-limitations-that-can-look-like-a-sign-in-problem).

### Step-by-step check (Windows PowerShell)

Run each line in Windows PowerShell. Stop at the first step that fails.

1. **Is GitHub CLI installed for Windows?**

   ```powershell
   where.exe gh
   ```

   Good: one or more paths ending in `gh.exe`.
   Bad: `INFO: Could not find files for the given pattern(s).` Install GitHub CLI
   for Windows, then restart the app as described above.

2. **Is GitHub CLI signed in?**

   ```powershell
   gh auth status
   $LASTEXITCODE
   ```

   Good: `Logged in to github.com account ...` and exit code `0`. The token
   line in this output is masked; still do not paste it into a report.
   Bad: `You are not logged into any GitHub hosts` or a non-zero exit code.
   Run `gh auth login` and choose **GitHub.com**. A human must complete the
   browser or device-code step.

3. **Does that account have Copilot?** If several accounts are listed, the
   **active** one is used. Check that account's Copilot access at
   <https://github.com/settings/copilot>. Switch accounts with
   `gh auth switch` if needed.

4. **Did the app start before `gh` was installed or before your PATH
   changed?** The app keeps the PATH it was started with. A new terminal can
   see `gh` while the running app cannot. Exit the app from the tray and reopen
   it. The newer error message says directly whether `gh.exe` was found.

5. **Retry Explain.** If it still fails, copy the exact message (it should not
   contain secrets, but check) and file a bug as described below.

## What each feature needs

Viewing PRs never needs WSL or Copilot. Each optional feature fails on its own,
with a message in the row; it does not stop the dashboard.

| Feature | Windows needs | WSL needs | Sign-in used |
|---|---|---|---|
| Viewing PRs, merge re-checks, Auto, re-queue | .NET Desktop Runtime 10, WebView2 runtime, Azure CLI (`az`) | Nothing | `az login` in Windows, or `AZURE_DEVOPS_EXT_PAT` / `AZURE_DEVOPS_PAT` set before the app starts |
| **Explain this PR** | The full app package (bundled runtime), GitHub CLI `gh` on the app's PATH | Nothing | Windows `gh auth login` with Copilot access |
| **Ask Copilot** / **Review with a lens** | Same as Explain | `wsl.exe`, a distro with `git` and `python3`, and a checkout of the PR's repository (Settings → Copilot in WSL, default `~/clones/<repo>`) whose remote matches the PR's ADO repository | Windows `gh` for Copilot; your existing WSL Git credentials for fetching |
| **TikTok This PR** | Same as Explain, plus Windows `az`, H.264/AAC encoders (Windows N needs the Media Feature Pack) | Nothing | Windows `gh` with access to the `claude-opus-5.5` model; `az login` with Speech access (**Cognitive Services Speech User**) |
| **Fix PR** | `wsl.exe` | A distro with `git`, an authenticated `copilot` CLI, `bgtask` (on PATH or `~/go/bin/bgtask`), `flock`, `setsid`, `python3`; optional `~/.config/bash/environment` | `copilot` sign-in **inside that WSL distro**. Windows sign-ins are not used. |
| **PoP** | WebView2 runtime | Nothing | Your normal organization sign-in in the PoP pane; human only |
| Approval provider (**+1**) | A locally installed provider assembly configured in settings | Depends on the provider | Owned by the provider |
| Update checks | Internet access to `api.github.com` and `github.com` | Nothing | None |

Notes:

- ADO PR View does not install `bgtask`; Fix's preflight error names it when it
  is missing.
- The WSL distro is the one chosen in **Settings → Copilot in WSL**; blank means
  the WSL default. Check it with `wsl.exe --list --verbose`.
- Installing or signing in to the Windows `copilot` CLI does not change the WSL
  one, and the reverse.

### Safe checks for the WSL features

Run these in Windows PowerShell; replace `<distro>` with the configured distro
(or omit `-d <distro>` for the default). They print versions and paths only.

```powershell
wsl.exe --list --verbose
wsl.exe -d <distro> -- bash -lc 'command -v git python3 copilot bgtask flock setsid'
wsl.exe -d <distro> -- bash -lc 'git -C ~/clones/<repo> remote -v'
```

The Fix runner is not a login shell: it sources `~/.config/bash/environment`
(and `BASH_ENV`) when present, then tries nvm's default and
`~/.local/bin/copilot`. If a tool is found only in a login shell, add its
location to that environment file yourself; the app never edits it.
`git remote -v` prints remote URLs, which can include organization and
repository names; do not paste them publicly.

Check WSL Copilot sign-in by running `copilot` interactively in that distro.
Do not test it by starting Fix PR.

## Where the app writes things

All under `%LOCALAPPDATA%\AdoPrView` unless noted.

| Path | What it is | Safe to share? |
|---|---|---|
| `Diagnostics\<session>\events.jsonl` (and `events.previous.jsonl`) | One JSON line per lifecycle event: start, window state, exit, handled and unhandled errors, WebView failures, update check failures. Exceptions are recorded as type, error code and method names only. Max 512 KiB per file; the 10 most recent sessions are kept. | Mostly. Read it first: some `reason` fields hold short messages (for example an update-check error). |
| `Explain\runtime\logs\process-*.log` | The bundled Copilot runtime's own log for Explain. Errors only. | Review first; may contain local paths. |
| `Investigations\runtime\logs`, `Videos\runtime\logs` | Same, for questions/lens reviews and TikTok. | Review first. |
| `CopilotRuns` | Fix PR run records. Each run folder can hold `prepare.stderr` and the run's output log. | **No**, not without careful redaction: may contain remote URLs, authentication details and code. |
| `Videos\completed`, `Videos\jobs` | Finished MP4s and private storyboards/narration. | No: derived from private code. |
| `settings.json` | Your repositories, watched authors, WSL mappings. | No: names your organization and repositories. |
| `WebView2`, `PoPWebView2` | Browser profiles, including sign-in cookies. | **Never.** |
| `Updates` | Downloaded update installers. | Not useful for bug reports. |

What is **not** logged:

- **Explain, question, lens and TikTok failure messages are shown only in the
  app.** They are not written to `events.jsonl`. Copy the message text from the
  card before closing the app.
- The app does not record PATH, environment variables, prompts or PR content.

## Check the installed version and files

The release installer puts the app in `%LOCALAPPDATA%\Programs\ADO PR View`.
Portable and source builds live wherever you put them; set `$InstallDir`
accordingly.

```powershell
$InstallDir = "$env:LOCALAPPDATA\Programs\ADO PR View"
(Get-Item "$InstallDir\AdoPrView.exe").VersionInfo.ProductVersion
Get-ChildItem "$InstallDir\runtimes\win-x64\native" | Select-Object Name, Length
```

The version looks like `0.1.4+<commit>`. The `native` folder must contain
non-empty `copilot-runtime.exe`, `runtime.node` and `copilot_runtime.dll` (use
`win-arm64` on Arm). If any is missing, Explain says so; reinstall the full
release rather than copying single files.

Other version checks: `az version`, `gh --version`, and for Fix,
`wsl.exe -d <distro> -- bash -lc 'copilot --version'`.

## Comparing the app's environment with your terminal

The app gets its environment from whatever started it: normally Windows
Explorer through the Start Menu, not your terminal. Common differences:

- **PATH.** A tool installed after Explorer started may be missing from the app
  until it is fully restarted, or until you sign out and in. For `gh`, the newer
  Explain message reports this directly.
- **Variables you set in a terminal** (`$env:...` or in a PowerShell profile)
  are not seen by an app started from the Start Menu.
- **Variables the Copilot runtime never receives,** even if set for your whole
  Windows account: `GH_TOKEN`, `GITHUB_TOKEN`, `COPILOT_GITHUB_TOKEN`,
  `COPILOT_HOME`, `GH_CONFIG_DIR`, `GH_HOST`, `HTTP_PROXY`, `HTTPS_PROXY`,
  `NO_PROXY`, `NODE_OPTIONS`. The runtime receives only basic Windows variables
  such as `PATH`, `TEMP`, `USERPROFILE`, `APPDATA` and `LOCALAPPDATA`.
- **WSL.** A Windows Terminal tab can be a WSL shell. Its sign-ins and PATH are
  Linux-only and invisible to the Windows app.

To see what account variables exist, without printing values:

```powershell
'Path','GH_CONFIG_DIR','GH_HOST','HTTPS_PROXY','HTTP_PROXY' | ForEach-Object {
  [pscustomobject]@{ Name = $_
    User = [bool][Environment]::GetEnvironmentVariable($_, 'User')
    Machine = [bool][Environment]::GetEnvironmentVariable($_, 'Machine') } }
```

## Known limitations that can look like a sign-in problem

These follow from the current design. They are not confirmed causes for any
particular report.

- **`GH_CONFIG_DIR` is not passed on.** If you keep GitHub CLI's settings in a
  custom folder, `gh auth status` works in your terminal but the runtime's `gh`
  looks in the default folder and finds no sign-in.
- **Only GitHub.com sign-in has been exercised.** GitHub Enterprise Server
  accounts and `GH_HOST` are not passed on and are untested.
- **Proxy variables are not passed on.** Networks that require
  `HTTPS_PROXY`-style settings may produce network errors from Explain. Whether
  the runtime uses the Windows system proxy instead has not been verified.
- **The account needs Copilot access and the model.** Explain uses the
  runtime's default model. TikTok requires exactly `claude-opus-5.5`; there is
  no fallback. A missing model or policy shows as
  `Copilot returned no explanation. Check your Copilot access/model availability`
  or `Opus returned no storyboard`, or as the service's own error text.
- **Only x64 and Arm64 Windows** are supported for the bundled runtime.

## Other Explain messages

| Message | Meaning |
|---|---|
| `Explain's bundled Copilot runtime file is missing or empty at ...` | Incomplete install. Reinstall the full release package. |
| `Explanation timed out. Retry with a smaller PR.` | The ten-minute limit was reached. |
| `Copilot did not confirm the restricted read-only tool inventory. No PR content was sent.` | The runtime offered different tools than expected. Note the app version and report it. |
| `Copilot returned without inspecting any PR diffs` / `... repository context beyond the diff` | The model skipped required reading. Retry. |
| `Another PR explanation is running.` | Only one explanation runs at a time. Cancel or wait. |
| `Explanation failed:` followed by other text | Usually an error from the Copilot service or network. Quote it in a bug report after checking it holds no private names. |

## Least-disruptive fixes

1. Copy the exact message from the card.
2. Retry once, after fixing anything the message names.
3. Fully exit from the tray menu and reopen from the Start Menu, so the app picks
   up the current PATH.
4. Sign in with the tool the feature actually uses (table above). A human must
   complete browser, device-code, MFA or consent steps.
5. Reinstall the same release over itself if bundled files are missing. Setup
   refuses to run while the app is open and leaves `%LOCALAPPDATA%\AdoPrView`
   alone.
6. Sign out of Windows and back in if PATH still looks stale.

Do not delete `%LOCALAPPDATA%\AdoPrView`, `%USERPROFILE%\.copilot`, GitHub CLI's
settings, Credential Manager entries or WSL homes as a troubleshooting step.

## Filing a bug

Use **New issue → Bug report** in this repository. This repository is
**public**: anything you paste is visible to everyone.

Include:

- **Version:** the `ProductVersion` from above, and whether you used the
  installer, portable ZIP or a source build.
- **Feature and exact message:** for example the full `Explanation failed: ...`
  text.
- **Environment:** Windows version and architecture, whether WSL is installed
  and which features you use it for.
- **Sign-in checks, summarized,** not pasted:
  - `where.exe gh` found GitHub CLI: yes/no (not the path);
  - `gh auth status` exit code, host (for example github.com) and whether more
    than one account is listed;
  - where you ran `copilot login`: Windows PowerShell, Windows Terminal with
    PowerShell, a WSL tab, or VS Code only;
  - whether the app was started before installing or signing in to `gh`;
  - whether `GH_CONFIG_DIR`, `GH_HOST` or proxy variables are set (from the
    check above, true/false only).
- **Lifecycle log, only if relevant** (crashes, unexpected exits): the lines
  around the problem from `events.jsonl`, after reading them.

Before posting, replace with placeholders:

- user names and home paths (`C:\Users\<name>` → `%USERPROFILE%`);
- organization, project, repository, branch and PR titles or numbers;
- account names, e-mail addresses and tenant names;
- any URL that includes these.

Never attach: tokens or `gh auth token` output, `settings.json`, WebView2
folders, `CopilotRuns` files, video job files, source code, prompts, or a full
environment-variable dump.

If you used a coding agent, give it this file and ask it to run the checks
above and draft the bug report with the redactions. Review the draft yourself
before posting.

## Possible future improvements (not available yet)

These are ideas, not current features:

- Let Explain also use a standalone `copilot login` (Windows) account.
- Record Explain/questions/TikTok failure categories in the lifecycle log, so
  they survive after the app closes.
- Pass proxy settings to the bundled runtime.
