# ADO PR View

Native Windows (WPF + WebView2) dashboard for your Azure DevOps pull requests
and those of authors you choose to watch. It shows which PRs need a human,
which are blocked, which are waiting on automation, and which are ready.

**Installing? Give [INSTALL.md](INSTALL.md) to a CLI coding agent.** It has a
copyable kickoff prompt. The agent discovers your platform, runtime, existing
Azure sign-in, repositories and settings, resolves your ADO identity and
repositories with read-only requests, merges configuration safely, and launches
one configured dashboard. It asks you only what it cannot determine.

See [docs/DESIGN.md](docs/DESIGN.md) for architecture, lanes and the ADO calls used.

## Download

[Releases](../../releases/latest) provide a per-user installer
(`AdoPrView-v<version>-win-x64-setup.exe`, no administrator rights), a portable
ZIP of the same self-contained app, and SHA-256 checksums. It needs no .NET
install, only Windows 10/11 x64 with the Microsoft Edge WebView2 Evergreen
Runtime. The files are not code-signed, so SmartScreen may warn. Close any running
dashboard before installing, upgrading or uninstalling; setup refuses to run
while one is open and never touches `%LOCALAPPDATA%\AdoPrView`.

## Run

- Start Menu → **ADO PR View**, or the `AdoPrView.exe` in your install or publish folder.
- Auth: Windows `az login` (the app uses `az account get-access-token`), or set
  `AZURE_DEVOPS_EXT_PAT` / `AZURE_DEVOPS_PAT`.
- Settings (repos, watched authors, hidden PRs, polling, auto-complete options,
  Copilot/WSL mappings) live in the gear drawer and persist to
  `%LOCALAPPDATA%\AdoPrView\settings.json`. A new install starts with no
  repositories. An unreadable or invalid settings file is reported at startup and
  left unchanged; it is never replaced with defaults. Fields the app does not
  recognize are preserved when it saves.
- **My PRs** are those created by your authenticated ADO identity in each
  organization. Watched authors match display name, UPN or alias.
- Closing the window keeps it in the tray while notifications are on; exit from the tray menu.
- **Needs Human** means only reviewer approval (+1), Proof of Presence,
  auto-complete or publishing a draft remains, in any combination. Automated
  work still running stays **Waiting**; failures and required fixes stay **Blocked**.
- An optional **review bot** (`reviewBot` in settings: display name, ADO
  identity ID and the organization it reviews) gets its own gate column for
  that organization's PRs. Its open findings block; resolved
  findings show **Addressed**; **Partial** coverage is a warning, not pending
  work. The bot is assumed to review the initial PR only, so later iterations do
  not wait for another run. Without a configured bot, the column shows no
  requirement and no warnings or waits are invented.
- A custom four-note chime announces new **Needs Human** arrivals, even while
  the app is focused. Initial load and unchanged rows stay silent; simultaneous
  arrivals share one chime. The existing Notifications checkbox controls it.
- The current refresh interval is shown in the status bar and is configurable
  in Settings (default: 2 minutes; merge re-check: 10 minutes).
- Refreshes keep rows stable while the pointer is over the table or a row
  control has keyboard focus. Incoming changes are applied after you leave the
  rows, so PoP, Open and other actions cannot jump to a different PR under the
  pointer. The status line indicates pending table updates.
- **Rows re-sort only on Refresh (R)**, including Refresh from the tray.
  Background refreshes and row actions retain surviving rows' order, even when
  their Updated time or lane changes. Current lane labels/colors and header
  counts remain live; new PRs append and removed/hidden PRs disappear. Refresh
  restores Needs Human / Blocked / Waiting / Ready priority, then newest Updated
  within each lane. Search and lane/repo filters do not re-sort.
  If a displayed PR changes lane while a lane filter is active, it stays
  accessible with its new lane label until Refresh or you change a filter;
  the header counts always reflect current status.

## Automatic merge re-checks

The app sends ADO's actual **Restart merge** request (`PATCH mergeOptions`),
not just a cached `mergeStatus` read. Stale previews (including missing preview
target commits) are checked on the configured merge interval, at the next normal
refresh; the current defaults are 10 minutes and 2 minutes respectively.

Each refresh shares one ref read per repository/target branch. When an observed
target head changes, one sweep rechecks **eligible monitored active, non-hidden PRs**,
including formerly clean previews and PRs in other repositories. This detects
external merges without depending on watched authors or a disappearing Needs
Human row. A target push also counts; hides, filters, lane changes and incomplete
PR lists do not. Multiple changed refs in one refresh share a sweep, and
unchanged refs do not repeatedly force it.

Observed **Conflicts** are excluded from every automatic restart, including
stale-target checks, forced sweeps and retries. Normal read-only refreshes still
consume ADO's source-push-driven merge result; automatic eligibility resumes
once ADO reports a non-conflict status. The deliberate **Re-check Merge** command
remains available. Conflicts never claim an automatic re-check is queued.

Already queued/notSet evaluations are never cancelled. Deferred work is picked
up after those finish or a failed PR read recovers. Restart failures appear in
the row's Merge details and status line and retry on a subsequent normal refresh
(at least one refresh interval later); accepted requests await ADO's fresh result.
If ADO keeps returning an old stale preview, another restart waits for the merge
interval. Ref-read failures are visible and retried on the next refresh.
Existing merge options are preserved. No merge, branch push, policy change or
auto-complete change is part of these automatic checks.

## Keys

`/` search · `r` refresh · `Esc` close drawer / collapse rows · `Ctrl+Z` undo Hide

Right-click a PR row and choose **Hide this PR**; there is no Hide action button.
Hide asks for confirmation showing the PR title, number and repository. Use its
Undo toast or Ctrl+Z to restore a confirmed hide. Ctrl+Z walks back hides from the
current app session only. It never reverses other PR actions; text fields keep
their normal undo behavior.

## Build

```powershell
cd src\AdoPrView
dotnet build                     # dev
dotnet publish -c Release -r win-x64 --self-contained false -o <publish-folder>
```

`.github/scripts/New-ReleasePackage.ps1` builds the self-contained release ZIP,
installer (needs Inno Setup 6) and checksums. CI runs the native, frontend and
install-guide tests on every push and pull request; pushing a `v<version>` tag
that matches the project version and is on `main` publishes a release.

Dev flags: `--mock` (fixture data), `--screenshot out.png [--size WxH] [--eval js] [--eval-wait ms]`.

## Row actions

Row actions use fixed **+1 / Publish / PoP / Auto / Fix / Open** grid slots across
every row. Compact, busy or missing actions keep their slot; enlarged buttons
cannot displace neighboring actions. Hide has no visible slot.

The **+1** slot is larger and amber when the PR needs human reviewer approval,
including an approval invalidated by later commits. It opens the PR in ADO so
you can arrange review. Reviews, PoP and Auto-complete do not occupy table
columns; their action buttons and expanded status/reviewer details retain the
underlying requirements.

## Inline Proof of Presence (compatibility prototype)

**PoP** opens the captured PR in a reusable **native Edge WebView2 pane inside
the dashboard**, not a new browser tab. The table remains available above it.
An inset frame separates the browser from the dashboard; the toolbar follows
the app's dark palette with visible hover, keyboard-focus and disabled states.
Drag the grip to resize, or focus it and use Up/Down. The pane shows the PR title/ID and current HTTPS
origin, with Back, Reload, PR page, Check status and Close controls. Website
sign-in popups stay inside this pane.

Complete the normal ADO website's PoP banner yourself, including sign-in,
security-key or Windows Hello prompts. **Check status**, returning focus from
the website to the dashboard, and closing the pane read that exact PR's actual
ADO policy state. Navigation/close alone never counts as approval, and these
checks do not reorder the table.

The pane requests Windows primary-account SSO and keeps a separate app-owned
browser session at `%LOCALAPPDATA%\AdoPrView\PoPWebView2`. It cannot access the
dashboard RPC bridge, host objects or `app.local` assets. No normal Edge
profile/cookies are copied, no credentials are scraped, and TLS, WebAuthn and
enterprise policies remain intact. This is **not the managed Edge profile**:
tenant Conditional Access may still reject embedded authentication. If that
happens, use the explicitly external **Open in Edge** fallback, then return and
Check status. Tenant/key combinations other than those already tried need
human verification.

## Auto-complete action

After ADO read-back confirms auto-complete, **Auto** becomes a muted grey check
in its existing action slot, including while the pointer remains over the row.
It is still a cancel toggle, not disabled; the expanded row also retains
**Cancel auto-complete**. A confirmed cancellation restores Set auto-complete.
Pending requests guard both controls, and failures/read-back mismatches show
explicit feedback rather than a success state. Refresh, collapse and another
PR selection do not reset the authoritative state or move neighboring rows.

## Fix PR with Copilot

**Fix PR** (the sparkle button) starts Copilot in WSL and displays stdout/stderr,
status and the actual exit code **inline in that PR's expanded details**. It does
not copy/paste, focus desktop Copilot or open a separate terminal. **Copy prompt**
remains available as an alternative.

The generated Fix/Copy prompt is configurable in settings:

- `triageInstructions` replaces the default "Ask" text verbatim.
- `fixSkill` names an optional local Copilot skill for the default Ask. It is not
  installed by the app.
- `fixReviewerModels` lists exact model names for independent, read-only
  **NO GOLD PLATING** reviews after implementation and before committing and
  pushing. An unavailable named reviewer blocks publication; there is no silent
  model substitution, global preference change or merge authority. Empty means
  no review gate.

The prompt also requires the repository's own validation: the applicable
`AGENTS.md` files and the guidance they reference, or otherwise that repository's
actual required checks. Missing, unavailable or failing required checks block
commit, push and completion. After validation, reviews and the description gate,
the run commits the requested fixes and pushes them with an ordinary non-force
`git push --no-follow-tags --recurse-submodules=check <remote> HEAD:refs/heads/<source branch>`
to the PR's existing source branch only, then verifies the remote ref and PR.
Tags are not pushed, and commits referencing unpublished submodule commits block
publication instead of pushing another repository. Before a new
run launches, the dashboard checks that the remote's fetch URL and every
effective push URL (`git remote get-url --push --all`, including `pushurl` and
`pushInsteadOf` rewrites) are the PR's repository; otherwise the run is not
started. The run's push command repeats that check immediately before pushing. No changes means no empty
commit or push. A rejected or unverifiable push is reported as blocked or
uncertain, never forced or retried.

Every run must retrieve and check the live full PR description before reporting
completion, even with no code changes. Changed work or an inaccurate description
requires maintenance that preserves useful context and reports only actual
validation. Each write must fit ADO's 4000-character limit and immediately be
read back and compared with the intended body (only line endings/trailing
newlines normalized). Success responses alone are not proof: one corrective
retry is allowed; persistent mismatch/unreadability blocks completion/publication.

In **Settings → Copilot in WSL**, select the distribution (blank uses the WSL
default) and each repository's source directory. Blank directories try
`~/clones/<repo>`. The app verifies the directory and its exact Azure DevOps
organization/project/repository remote before launching. WSL needs Git,
authenticated Copilot CLI and the managed task runner `bgtask`. The shared
`~/.config/bash/environment` supplies PATH/BASH_ENV.

Each run gets a unique, isolated worktree/branch at the freshly verified PR source
commit, not your current/default branch. Main-checkout files, index and branch
are not switched, reset or stashed. Different PRs can run concurrently from one
source repository; duplicate active runs of the same PR are blocked. A fork whose
source is not the mapped repository is rejected rather than fixed in the wrong
checkout. Fetch/worktree metadata are necessarily shared.

The inline output is noninteractive, safely rendered and bounded. Each deliberate
Fix PR run uses **`--yolo`**, the documented equivalent of `--allow-all-tools
--allow-all-paths --allow-all-urls`. These permissions apply to that process only;
global Copilot settings are unchanged. Inherited allow-all and assisted-approval
environment choices cannot replace the requested launch behavior. New runs are
launched without a `git push` deny so they can publish to the PR source branch;
the source-branch-only, no-force limit is a prompt instruction, not enforced by
the app. `gh pr merge` and `az repos pr update` command denies remain, as do the
prompt's restrictions on merge, approval, auto-complete and policy bypass. Runs
started by earlier versions keep their original `git push` deny, including when
resumed; they are not resumed or published retrospectively.
The worktree is an isolated starting directory, **not a filesystem sandbox**.
Interactive sign-in is not available in this output panel: authenticate in WSL
beforehand, or use Copy prompt with your own interactive workflow. Missing
CLI/auth/configuration or denied tools are errors, not clipboard-success
fallbacks. Exit 0 means the CLI exited normally, not that the PR has been verified
fixed.

A run's panel lives in **its own PR's expanded details**. Collapsing the row hides
the panel; reopening shows it again. Lane, search, repository and scope filters
hide the panel together with its row, and never move it to another view. The Fix
icon keeps spinning for that PR's active run, including while its details are
collapsed or another PR is open; completion, failure or cancellation clears it
even when collapsed. If the PR leaves the active list entirely (completed,
abandoned or no longer returned), its runs appear, collapsed, below the table in
the unfiltered view only. **Cancel run** targets only that app-owned task.
Changing views never cancels a run.

### Restart recovery and worktree cleanup

Tracked launches are recorded atomically under
`%LOCALAPPDATA%\AdoPrView\CopilotRuns`, separately from settings and credentials.
Records contain captured PR/workspace/task/session identity, status and log
pointers, not the prompt, output body or credentials. WSL ownership receipts and
supervised logs remain in their existing run/task locations. Cross-instance
reservations and run leases prevent duplicate launches, resumes and cleanup.

Closing the dashboard detaches its output follower instead of cancelling
the supervised WSL job. Startup checks live bgtask identity/status and reattaches
surviving output. A definitely interrupted job can resume **once**, in its same
verified worktree, using the installed CLI's documented `--resume <exact UUID>`.
New launches preassign that UUID with `--session-id`. Resume sends only a short
continuation preserving the saved task's original instructions; it does not
replay the original prompt or inject a newly generated prompt. Ordinary failures,
successful completions and explicit cancellations are **never auto-resumed**.
Missing exact session state, unavailable WSL/tools or ambiguous ownership/liveness
leave visible needs-attention history and preserved work instead of a fresh run.
The CLI must already be authenticated; this panel cannot perform sign-in.
Any failed continuation (including missing authentication) stays needs-attention
with its output/worktree and cannot trigger another automatic attempt.

Success is persisted before cleanup. Immediately after success, and again on
startup if needed, cleanup rechecks the exact nonce-linked receipt, Git namespace,
inactive owned task and execution lock under a repository metadata lock. Only a
clean worktree with no tracked/untracked/ignored changes is removable. If HEAD
moved, it must be reachable in a fresh fetch of the intended PR source branch;
unpublished or unverifiable commits are retained. Cleanup removes only our exact
worktree, disposable branch and run refs, never the source/main branch, unrelated
worktrees or broad Git metadata. There is no force removal or broad prune.
Dirty/unpublished/uncertain and failed/cancelled work remains with its path/reason.
A successful run whose cleanup fails stays **completed**, not resumable; its
history and supervised log references survive worktree removal.

Launches made by builds without the durable session/nonce registry are not
guessed, auto-resumed or cleaned. Such older builds keep in-memory history and
cancel their owned jobs on real Exit.

## Offline checks

No live approval, PR or auth requests are made:

```powershell
dotnet run --project tests\AdoPrView.Tests
node --test tests\approval-ui.test.cjs
node --test tests\row-actions.browser.test.cjs
powershell -NoProfile -ExecutionPolicy Bypass -File tests\install-guide.tests.ps1
```

The browser regression uses Node 22+ and the installed Microsoft Edge (or set
`ROW_TEST_BROWSER` to another Chromium executable). Real mouse/keyboard input
is used, with all URL launches and PR actions intercepted; no live PR is changed.
Native fake-HTTP/fake-clock checks cover periodic/forced restarts, target-change
sweeps, retries, in-flight deduplication and fresh result handling. The install
guide check parses every PowerShell block in INSTALL.md and runs its settings
merge against a disposable folder.

`src\AdoPrView\wwwroot\index.html?mock=1` runs the UI in a browser against
synthetic fixtures.
