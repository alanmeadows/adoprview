# ADO PR View — design

A Windows-native dashboard that shows **your** Azure DevOps pull requests (plus
PRs from a whitelist of other authors) across any number of repositories, with
enough detail inline that you rarely need to click into ADO.

## Architecture

```
┌──────────────── AdoPrView.exe (.NET 10, WPF) ─────────────────┐
│  MainWindow (dark title bar, taskbar overlay badge, tray icon) │
│  └─ WebView2  ── https://app.local/ (wwwroot, offline assets)  │
│        ▲  postMessage JSON-RPC  │  push events                 │
│        │                        ▼                              │
│  Bridge ── PrService ── AdoClient ── HttpClient ── ADO REST 7.1│
│              │  │          └─ TokenProvider (az CLI / PAT)     │
│              │  └─ Poller (refresh + merge re-check timers)    │
│              └─ SettingsStore (%LOCALAPPDATA%\AdoPrView)       │
└────────────────────────────────────────────────────────────────┘
```

* **Host:** WPF window hosting WebView2. Native pieces: dark immersive title
  bar, tray icon with balloon notifications when a PR newly needs human action or
  becomes blocked. A bespoke four-note chime plays once per update when one or
  more PRs enter Needs Human, even in the foreground. Initial load, unchanged
  rows, hidden/error rows, disabled notifications and headless captures are
  silent. Full refresh and single-PR refresh share transition detection.
* **UI:** plain HTML/CSS/JS (no build step) served from a WebView2 virtual host.
  `marked` + `DOMPurify` are vendored for safe Markdown descriptions.
  `?mock=1` runs the UI in any browser against fixture data for fast iteration.
* **Auth:** `AZURE_DEVOPS_EXT_PAT` → `AZURE_DEVOPS_PAT` → `az account
  get-access-token --resource 499b84ac-…` (cached until expiry). Tokens never
  reach the UI.
* **Storage:** `settings.json` (repos, whitelist, hidden PRs, intervals, auto-
  complete options, optional review bot, Fix prompt options, WSL mappings) in
  `%LOCALAPPDATA%\AdoPrView`. A missing file starts empty; an unreadable or
  invalid file stops startup with a message and is never overwritten. Every
  settings object keeps unrecognized JSON members and writes them back; saves
  are atomic (temporary file, then replace).
* **Approval provider (optional):** the **+1** slot opens the PR in ADO unless an
  approval provider is loaded. A provider is trusted native .NET code that the
  core loads only from an explicitly configured local assembly; it is not a
  sandbox or plugin marketplace. See [Approval provider contract](#approval-provider-contract).

## Approval provider contract

The public dashboard contains no approval-service integration. `ApprovalProvider.cs`
defines a small native contract (`ApprovalProviderContract.Version`) that a
separately built, locally installed assembly may implement:

* `Evaluate(PrView)` returns an `ApprovalOffer` (button title and optional detail)
  or null. Only the fixed **+1** slot renders it, as escaped text; providers cannot
  supply HTML, scripts or new UI surfaces.
* `RequestAsync` runs for the PR captured by the native snapshot key, with a host
  scoped to that PR (fresh re-read, hidden check, removal of one reviewer). The
  returned `ApprovalOutcome` (kind, message, raw response, refresh warning) is
  shown as text in that row; the dashboard re-reads the PR when asked.
* Optional settings expose a message, help text and allow-listed action IDs;
  native dialogs run on the UI thread with the main window as owner.

The core loads a provider only when its own settings explicitly configure one
with an absolute local path. It requires exactly one exported provider type and
a matching contract version, and an https link if any. Load failures are shown
as an explicit error; nothing is guessed from files, tokens or profiles that
happen to exist. When no provider is configured, the core does not probe for
one, the approval RPCs (`generateApproval`, `approvalSettings`,
`approvalSettingsAction`) are refused natively, and the UI shows no provider
settings. A provider is trusted in-process code, not a security boundary.
Service identity, repository scope, credentials and reviewer rules belong to the
provider, never to the core.

[INSTALL.md](../INSTALL.md) is the agent-facing setup runbook: scoped read-only
identity/repo discovery, backup/atomic merge, lifecycle safety and a verified
real launch.

## ADO calls

| Purpose | Call |
|---|---|
| Identity | `GET {org}/_apis/connectionData` |
| Active PRs per repo | `GET git/repositories/{repo}/pullrequests?searchCriteria.status=active&$top=500` (filter: creator = me or whitelist) |
| Full PR (description > 400 chars) | `GET git/repositories/{repo}/pullrequests/{id}` |
| Policy gates | `GET policy/evaluations?artifactId=vstfs:///CodeReview/CodeReviewId/{projectId}/{id}` |
| Pending review-enforcement details | `GET …/pullrequests/{id}/statuses` (latest matching context and configured author) |
| Threads | `GET …/pullrequests/{id}/threads` (system threads ignored) |
| Review bot state | `GET …/pullrequests/{id}/iterations` + comments by the configured `reviewBot.authorId` containing `Iteration #N` |
| Failed-gate log tail | `GET build/builds/{id}/timeline` → failed Task `log.id`; `GET build/builds/{id}/logs` (lineCount); `GET …/logs/{logId}?startLine=&endLine=` |
| Merge re-check | Shared exact-target `GET git/repositories/{repo}/refs?filter=heads/{target}`; scheduled stale checks or a target-change/forced sweep issue fresh PR GET + `PATCH pullrequests/{id} {mergeOptions}` (ADO "Restart merge"); skip `queued`/`notSet`, and skip `conflicts` for automatic requests |
| Auto-complete | `PATCH pullrequests/{id} {autoCompleteSetBy:{id:me}, completionOptions:{mergeStrategy:squash, deleteSourceBranch:true, transitionWorkItems:true}}` |
| Cancel auto-complete | `PATCH … {autoCompleteSetBy:{id:"00000000-…"}}` |
| Publish draft | `PATCH … {isDraft:false}` |
| Re-queue build policy | `PATCH policy/evaluations/{evaluationId}` |
| PoP | Opens a separate native inline Edge WebView2 pane for the captured PR; human sign-in/key action only, real policy read on Check status/return/close; explicit external Edge fallback |

## Classification ("lanes", sorted top → bottom)

1. **Needs Human** — nothing agent-fixable or automated/in-progress remains;
   human reviewer approval (+1), Proof of Presence, and enabling auto-complete
   belong here, in any combination. Publishing a draft also remains a human
   action. The human can be you or another reviewer; it is not an ownership lane.
2. **Blocked** — merge conflicts, failed build/status check, unresolved
   comments, open review-bot findings, missing work-item link, rejecting vote.
   These are what "Triage" hands to Copilot.
3. **Waiting** — automated builds/checks or merge evaluation are pending
   (human actions stay highlighted so you can do them early). Automated review-
   enforcement statuses saying *Missing Approvals* or *Waiting on reviewer(s)
   to approve*, or *Additional human sign-off is required* are human requirements,
   even when their policy says queued, running or rejected. Actual processing or
   unknown review-status waits stay Waiting;
   broken/failed automation stays Blocked. Optional reviewers with no vote do
   not create a requirement; rejecting / waiting-for-author votes stay Blocked.
4. **Ready** — every blocking policy approved (auto-complete will merge).

The optional review bot is identified only by the configured `reviewBot`
identity ID; its display name is a label. It applies only to PRs in its
configured `reviewBot.organization`; other organizations, a missing bot and a
bot without an organization (reported as a refresh error) all get a "not
applicable" gate, and no warning or wait is derived from bot comments. With one:

* **Partial review** is a terminal result: the run completed, but only some
  reviewer agents ran. Keep the amber **Partial** coverage warning and its
  detail; do not treat it as pending work, retry it, or call it fully clean.
* The bot reviews the initial PR, not every subsequent ADO update. Retain its
  recorded outcome across later iterations; resolved initial findings remain
  **Addressed**, rather than waiting for reports that will never arrive. Open
  findings from any iteration still block, even if a newer review is clean.
* Missing or unrecognized outcomes show a warning; absence of a report is not
  evidence of an active run and does not add a Waiting condition. The detail
  identifies the reviewed iteration separately from the PR's current iteration.

The **+1** action is larger and amber when human approval (including one
invalidated by later commits) is needed. Without a provider it opens the PR in
ADO. With a provider, its offer replaces the button title for that PR; any
service-specific checks, such as which repositories are supported or how a stale
vote is handled, are the provider's responsibility and are reported back as its
outcome.

At startup and on explicit Refresh (button, R shortcut or tray), order is lane
priority then most recently updated first. The backend still produces that sort;
only an explicit refresh increments `Snapshot.OrderRevision`. The frontend
retains surviving keys in their existing order on all other snapshots, including
single-row updates and automatic merge sweeps. New keys append; removed/hidden
rows disappear. Lane changes update each row's colored lane label and the header
counts in place. Per-section lane headings are replaced by truthful per-row
labels, so a lane transition cannot silently move an active row or leave it under
the wrong heading. An active lane filter retains displayed PRs that change lane
until an explicit Refresh/filter change, with current lane labels, live counters
and an explanatory status message. Filters preserve relative order. Expansion and scroll survive
repaints where possible; a main Refresh deliberately re-sorts surviving rows.

## Merge preview scheduling

Full refreshes share lazy ref-read tasks, keyed by org/project/repo ID and exact
case-sensitive target ref. Missing preview target commits are stale when the
current head is known; missing/failed ref reads are explicit unverified warnings,
not clean previews. Ref reads recover on the next poll without waiting for the
merge interval. Retained known refs allow detection when a merge removes the last
watched PR on a target. Removing a configured repo removes its target baselines.

At the configured merge cadence, stale previews are restarted. Successful target
head changes coalesce into one sweep generation across all monitored active,
non-hidden eligible PRs, whether stale or formerly clean. No disappearance/lane-change
heuristic is used; external merges and ordinary target pushes are both covered.
Each PR handles a sweep generation at most once after successful submission.
Failed/incomplete PR reads and in-flight evaluations defer that work until a later
refresh. Unchanged refs do not create new sweep generations.

One automatic eligibility rule excludes observed `conflicts` from cadence,
forced sweeps and retries, and is checked again after the fresh pre-write GET.
Conflict observations clear stale local pending state and consume deferred sweep
generations without erasing real error diagnostics. Normal read-only refreshes
still observe source-push recomputation; non-conflict results restore ordinary
eligibility. Deliberate manual Re-check Merge is not subject to this exclusion.

Full refresh, single-PR refresh and manual merge restarts share the refresh gate.
Writes are sequential and use AdoClient's shared throttle. A fresh PR read
revalidates active/in-flight state, then a single non-transport-retried PATCH
resends only the existing `mergeOptions`, never completion options. Local accepted
request state prevents old terminal snapshots from immediately causing another
restart. Fresh terminal results at the current target clear pending state; old
stale results retry after the merge cadence. Failed writes retain their error and
retry no earlier than the next normal refresh interval, after another fresh PR
read. Raw queued/notSet jobs are never cancelled, even if they take longer.

The status line reports actual submitted/checking/error counts. Merge gate
tooltips and expanded details show target-read failures, last request time,
pending outcomes and explicit retry failures. Stale alone never claims a job was
queued. None of this changes approval, policy or auto-complete behavior, and
automatic sweeps never advance the UI's order revision.

Background snapshots are held while the pointer is over the rows, a pointer
gesture is in progress, or a row control has keyboard-visible focus. The displayed
snapshot remains the action source until interaction ends; only the newest pending
snapshot is then applied. Metadata-only refresh events do not rebuild the rows;
periodic redraws and asynchronous log expansion are also deferred. Mouse-click
focus does not pin updates after the pointer leaves. Explicit actions can still expand details and show
busy/outcome feedback against the displayed PR's stable key. The bridge resolves
that key, never a row index; a removed PR cannot fall through to another row.

Hide exists only in the row's right-click context menu; it captures PR identity
when the menu opens, not when a later menu click looks up a possibly moved row.
Native context menus remain available in terminal output, input and links.
Hide requires confirmation with the selected PR's full title, number and repo.
Only completed hides enter the session's undo history; the toast's Undo action
restores that exact PR, and Ctrl+Z restores the most recent hide. Failed undos
surface an error and remain retryable. Text fields retain their normal Ctrl+Z
behavior, and no other PR operation is undone. Settings, rather than a potentially
older snapshot's hidden flag, determine row visibility after a restore.

## UI

* Header: lane counters (click to filter), search, repo filter, scope
  (all / mine / whitelist), last refresh, refresh, settings.
* Table row: lane stripe, title + `!id` + repo + branch, author, gate chips
  (Merge · Build · Checks · Comments · Review bot),
  updated age, one-click actions (+1, Publish, PoP, Auto-complete, Fix PR, Open).
  A needed human/latest-iteration +1 is larger amber; otherwise it is compact.
  It opens ADO unless an approval provider offers an action for that PR.
  Hide is right-click only. One shared
  action grid reserves fixed +1 / Publish / PoP / Auto / Fix / Open slots even
  when a button is compact, busy, unavailable or absent. Buttons do not wrap,
  clip or shift neighboring actions.
* Expanded row: rendered description · policy list · failed build log tails
  (copy, open in ADO, re-queue) · unresolved threads · reviewers and votes.
  When a provider offers approval, the row also shows its action, optional
  provider link and the last outcome/error as text (kept in UI memory across
  refreshes).
* Settings drawer: repositories (paste any ADO repo URL, or discover repos
  with your PRs), whitelist authors (alias, UPN or display name), hidden PRs
  (restore), refresh/merge-check intervals, auto-complete options, Copilot/WSL
  mappings, and provider settings only when a provider is loaded.
* Fix PR: builds the existing context-rich prompt (PR URL, branch, blockers,
  log tail, threads and safety constraints), validates fresh active
  ADO PR identity plus its exact source ref/commit, then starts Copilot in an
  isolated WSL worktree. Source checkout work files/index/branch are untouched.
  `triageInstructions` replaces the default Ask; `fixSkill` optionally names a
  local skill for it. When `fixReviewerModels` lists models, the shared
  generated Fix/Copy prompt requires one independent read-only review per named
  model after implementation and before any already-permitted publication.
  Each reviewer receives `NO GOLD PLATING` and
  a correctness/regression/requested-requirements-only scope. The main agent
  must assess feedback, implement justified required fixes and run targeted
  checks; unavailable required reviewers explicitly block publication without
  substitution. Existing authority boundaries remain unchanged.
  Final completion/publication also requires a live full-description GET and
  accuracy check after reviewer-required changes, including no-code-change
  runs. Changed work or inaccurate descriptions require maintenance. Every
  write is counted against 4000 characters and immediately GET/read-back
  compared with the intended text, normalizing only line endings/trailing
  newlines. One corrective retry is permitted; unreadable/mismatched results
  block completion with explicit lengths/status. Later edits repeat the check.
  Unique run worktrees permit concurrent PRs, with durable cross-instance
  same-PR reservations and per-run driver leases.
  The native runner launches hidden child processes and streams bounded plain
  output/status/exit snapshots into that PR's inline card, without replacing row
  DOM while streaming. Output is retained across renders. A run's card renders
  only inside its own PR's expanded details, so filters and collapse hide it with
  its row. Runs whose PR is absent from the whole active snapshot (completed,
  abandoned, no longer returned) appear collapsed in a panel below the table, in
  the unfiltered view only. Changing views never cancels a run. Cancellation
  targets only owned tasks.
  `%LOCALAPPDATA%\AdoPrView\CopilotRuns` stores atomic private launch/state
  receipts (no prompt, output body or credentials), including exact bgtask task
  and preassigned Copilot session UUID. New CLI launches use `--session-id`;
  normal GUI disposal detaches followers without stopping supervision. Startup
  reconciles live owned tasks and reattaches output. Only a definitely inactive,
  interrupted job with an existing exact session and verified retained worktree
  can use `--resume <UUID>` once; its saved instructions are preserved rather
  than replaying a new Fix template. Missing/ambiguous state is needs-attention.
  Successful, explicitly cancelled and ordinary failed jobs never auto-resume.
  Success is durable before immediate cleanup; startup catches up cleanup even
  when the task finished while the UI was closed. Under the per-repo metadata
  lock, a fresh receipt/liveness/execution-lock check allows only exact owned,
  clean worktrees to be removed. Changed HEAD must be freshly proven reachable
  in the intended remote PR source. Dirty/untracked/ignored/unpublished work and
  failed/cancelled worktrees are preserved with reasons/paths. The owned branch
  and refs are deleted only after `git worktree remove` succeeds; if Git refuses
  (e.g. initialized submodules) everything is kept and reported retained. Cleanup errors
  never invalidate terminal completion; history/log pointers survive removal.
  Legacy launches without the new registry/session proof are not adopted or
  deleted. No broad cleanup or force worktree removal is used. Each deliberate run uses
  `--yolo` for tool/path/URL permissions without changing global configuration;
  inherited allow-all environment settings are cleared and assisted approval is
  disabled for the child process. The worktree is not a filesystem sandbox.
  New runs persist explicit source-branch push authority in their request and
  launch in a `*-push` runner mode without the `git push` deny (resume keeps the
  same mode); records lacking it stay no-push. Push-authorized launches also
  require every effective push URL of the chosen remote to be the PR's
  repository (prepare re-checks their hash). The exact verified remote, captured
  source ref and a push command that re-checks that hash and uses
  `--no-follow-tags --recurse-submodules=check` (unpublished submodule commits
  block instead of being pushed) are appended to the staged prompt; ref/no-force limits are
  prompt instructions, not enforced. Merge/PR-update command denies and prompt
  restrictions on approval, auto-complete and policy bypass remain. Noninteractive sign-in limitations
  are explicit. Copy prompt remains separate and never focuses another app.
  The row's running indicator derives from run-ID-keyed native history and the
  latest captured per-PR run, not the
  launch RPC or expansion state. Events update its fixed button in place even
  while collapsed; preparing/recovering/running/cancelling remains active until a terminal
  run state arrives.
* Inline PoP prototype: a native resizable pane below the dashboard owns a
  separate WebView2 environment/profile (`PoPWebView2`), with Windows
  primary-account SSO enabled. HTTPS sign-in popups use additional unprivileged
  WebView2 controls in the same pane/environment. There is no dashboard message
  dispatcher, host object or `app.local` mapping on any website surface; web
  messages/host objects are disabled and dashboard RPC also verifies its source.
  Origin display omits URL paths/query/fragment. Local/non-HTTPS navigation,
  external URI schemes and downloads are blocked without weakening browser or
  tenant protections. A captured immutable PR selection guards asynchronous
  initialization/status results against row changes/reuse. A read-only native
  ADO refresh consumes actual policy state on Check status/return/close, without
  marking navigation as approval or incrementing the explicit-order revision.
  Native key/PIN dialogs remain human-only. App-owned SSO is not proof of
  managed Edge/Conditional Access compatibility; external Edge is an explicit
  fallback, never an automatic popup escape.
