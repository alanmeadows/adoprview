# Using ADO PR View

This is the detailed guide to the dashboard. For installation see
[INSTALL.md](../INSTALL.md); for architecture, classification rules and the ADO
calls used see [DESIGN.md](DESIGN.md); for building and tests see
[development.md](development.md).

- [Run and settings](#run-and-settings)
- [Lanes](#lanes)
- [Row order, filters and refreshes](#row-order-filters-and-refreshes)
- [Row actions](#row-actions)
- [Keys and hiding PRs](#keys-and-hiding-prs)
- [Expanded details and diagnostics](#expanded-details-and-diagnostics)
- [Automatic merge re-checks](#automatic-merge-re-checks)
- [Inline Proof of Presence](#inline-proof-of-presence-compatibility-prototype)
- [Auto-complete](#auto-complete)
- [Explain this PR](#explain-this-pr)
- [PR questions and lens reviews](#pr-questions-and-lens-reviews)
- [TikTok This PR](#tiktok-this-pr)
- [Inline Copilot Fix](#inline-copilot-fix)
- [Restart recovery and worktree cleanup](#restart-recovery-and-worktree-cleanup)

## Run and settings

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
  organization. Watched authors match display name, UPN or alias. The **All /
  Mine / Watched** switch filters the table by author; ownership is not a lane.
- Closing the window keeps it in the tray while notifications are on; exit from the tray menu.
- A custom four-note chime announces new **Needs Human** arrivals, even while
  the app is focused. Initial load and unchanged rows stay silent; simultaneous
  arrivals share one chime. Settings has independent **Tray notifications
  (closing the window keeps it in the tray)** and **Play chime for new Needs Human
  PRs** checkboxes. Turn the chime off to mute it while keeping tray notifications
  and background monitoring. Chimes can also stay on with tray notifications off;
  close-to-tray still depends only on Tray notifications.
  Existing settings retain their previous sound behavior until you change it.
  Muted arrivals are consumed without replaying when the chime is turned back on.
- The current refresh interval is shown in the status bar and is configurable
  in Settings (default: 2 minutes; merge re-check: 10 minutes).

### Themes

Choose **Settings > Appearance > Theme**: **Light Mode** (the default, inspired
by GitHub's light web appearance) or **Dark Mode**. Selection applies immediately
and saves independently of other
unfinished settings edits; no restart or separate Save is needed. A failed
save restores the previous appearance and shows an error.

The `theme` setting uses `light` or `dark`. Existing settings without this key
use Light Mode. The former `github` value is read as `light` and written in its
canonical form on the next settings save, without rewriting the file at load.
Explicit supported choices and unrecognized settings are preserved.
An unsupported theme from another version displays Light Mode with a notice until
you choose a supported theme. Themes cover the dashboard, Markdown/code/logs,
settings and app-owned PoP framing, loading canvas and title bar (where Windows
supports it). External ADO/sign-in pages, Windows security prompts, system
dialogs and tray menus retain their own styling. No website CSS or OS-wide
appearance setting is changed.

### Startup, exit and error logs

Lifecycle diagnostics are written to
`%LOCALAPPDATA%\AdoPrView\Diagnostics\<session>\events.jsonl`.
Each session records UTC timestamps, a unique session ID, process ID, version/build,
window visibility/state, close-to-tray, explicit tray Exit, shutdown teardown,
Windows session ending and application/process exit. Handled WPF dispatcher errors,
unobserved task exceptions, terminating unhandled exceptions and dashboard WebView
process failures have distinct records. Existing error dialogs and exception
handling behavior are unchanged.

Records are flushed to disk immediately. Each session has at most two 512 KiB log
files (`events.jsonl` and `events.previous.jsonl`); the ten most recent inactive
sessions are retained in addition to live instances. Concurrent instances have
separate locked sessions and are not mistaken for terminated processes.
Inspection and retention use a separate maintenance lock; a concurrent startup
records `prior-session-inspection-deferred` if another instance is maintaining
the history, and a later startup resumes maintenance.
An incomplete prior initialization is recorded as `prior-session-unavailable`,
without inferring an exit or preventing inspection of other sessions.
The next start reports a prior session without a clean completion record as
`prior-session-without-clean-end`. This is evidence of an **unclean or unknown
exit, not proof of a crash**: forced termination, power loss, native failures and
some OS shutdowns may leave no final exception. Exits before this logging was
installed cannot be reconstructed from these files.

Exceptions include types, error codes and method-only stack frames, not messages,
source paths, exception data, credentials, PR/code content or settings. Logging
failure displays a warning rather than preventing the app from running or claiming
the evidence was saved.

## Lanes

Every PR gets one lane label, and the header counters show how many PRs are in
each lane. Click a counter to filter by it.

- **Needs Human** means reviewer approval (+1), Proof of Presence,
  auto-complete or publishing a draft is available, with no observed blockers or
  other pending automated gates. A pending or unverified merge preview does not
  hide these actions and remains visible in the Merge gate. In practice
  that is two kinds of work, which can appear together on one PR:
  - **A reviewer's approval is still needed.** **Approve** records your own
    reviewer vote. Other required reviewers may still need to approve, including
    on watched PRs. An optional provider uses the **+1** button instead.
  - **Your finishing actions:** **PoP** (Proof of Presence), **Auto**
    (set auto-complete) and/or **Publish** (a draft), alone or alongside +1
    when a review is also still needed. These are amber on your own PRs; on
    watched PRs the same buttons appear without amber.
- **Blocked** — failures and required fixes: merge conflicts, a failed build or
  check, unresolved comments, open review-bot findings, a rejecting vote, and so
  on. These are what the inline Copilot Fix is for.
- **Waiting** — other automated work (builds, checks, review bot) is pending, or
  merge verification remains and no human action is available.
- **Ready** — no outstanding actions or observed merge uncertainty remains;
  auto-complete can complete when ADO's policies allow.

The Needs Human lane highlights available human work, not a guarantee that the
merge preview is complete, current or clean, or that every policy is green. Open the
expanded row or ADO when in doubt. [DESIGN.md](DESIGN.md#classification-lanes-sorted-top--bottom)
has the full classification rules.

An optional **review bot** (`reviewBot` in settings: display name, ADO
identity ID and the organization it reviews) gets its own gate column for
that organization's PRs. Its open findings block; resolved
findings show **Addressed**; **Partial** coverage is a warning, not pending
work. The bot is assumed to review the initial PR only, so later iterations do
not wait for another run. Without a configured bot, the column shows no
requirement and no warnings or waits are invented.

## Row order, filters and refreshes

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

## Row actions

In Settings, **Ignore draft PRs** excludes drafts from all attention lanes,
title/tray/taskbar counts, notifications and automatic merge re-checks. It is
off by default. The **Ignored drafts** filter lists them without attention
actions; open a draft in Azure DevOps to publish it, or turn the setting off.
Publishing restores normal classification on the next refresh. Existing Fix
runs and explanations, and their output, are retained and remain cancellable;
**Explain** is unavailable for ignored drafts until the setting is turned off.

Row actions use fixed **Review / Publish / PoP / Auto / Fix / Open** grid slots across
every row. Compact, busy or missing actions keep their slot; enlarged buttons
cannot displace neighboring actions. Hide has no visible slot.

**Approve** records the signed-in user's ordinary ADO reviewer vote (`10`).
Clicking it keeps the row collapsed or expanded as it was.
One click submits the vote without a confirmation dialog or account selection.
The app checks the captured PR, signed-in identity, latest iteration and
source/target revisions before submitting once, then reads back the vote and
PR state. **Your ADO review** in expanded details shows your recorded vote and
keeps **Open in ADO** available. ADO checks permissions; an author's vote may not
count toward required reviewers. Your approval does not imply other required or
latest-iteration reviews, policies, merge permission or PoP are satisfied.

An enabled optional approval provider retains its separate **+1** row action;
ordinary **Approve** remains in expanded details. Without a provider, the row's
Review slot shows **Approve** directly. Already-approved votes display
**Approved**, not another write action. If reviewer identity or revision cannot
be verified, refresh before approving. Hidden, inactive, ignored-draft and
no-longer-monitored PRs cannot be approved here. Monitored drafts support ordinary reviewer votes;
approving does not publish the draft.

ADO's reviewer-vote API has no atomic revision condition. The app checks both
sides of the write and reports **Approval not confirmed** if revisions change or
read-back fails. A vote may have been recorded in that case: refresh and inspect
the PR before retrying. It never automatically retries or revokes the vote.

**Publish** marks a draft ready for review. **Open** opens the PR in Azure
DevOps. **PoP**, **Auto** and **Fix** are described below.

## Keys and hiding PRs

`/` search · `r` refresh · `Esc` close drawer / collapse rows · `Ctrl+Z` undo Hide

Right-click a PR row and choose **Hide this PR**; there is no Hide action button.
Hide asks for confirmation showing the PR title, number and repository. Use its
Undo toast or Ctrl+Z to restore a confirmed hide. Ctrl+Z walks back hides from the
current app session only. It never reverses other PR actions; text fields keep
their normal undo behavior.

## Expanded details and diagnostics

Click a row to expand it. The details show the PR description and unresolved
threads, a **Status** card listing what remains (with **Fix PR** and **Copy
prompt**), a **Merge** card (merge status, merge re-check, branches,
auto-complete, review bot, author, created), the policies and the reviewers with
their votes. Build policies have **Re-queue** and a link to the build.

For a failed build policy, a **Failed gate** card lists the failed tasks and
their reported issues. The first failed task's log tail loads automatically;
other tasks have **Load log tail**. Loaded logs offer **Timestamps** and **Copy**.
**Re-queue** asks ADO to re-run that build policy evaluation, and **Open logs**
opens the build in your browser.

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
once ADO reports a non-conflict status. The deliberate **Re-check merge** command
in the expanded Merge card remains available. Conflicts never claim an automatic
re-check is queued.

Already queued/notSet evaluations are never cancelled. Deferred work is picked
up after those finish or a failed PR read recovers. Restart failures appear in
the row's Merge details and status line and retry on a subsequent normal refresh
(at least one refresh interval later). PATCH is only the trigger; the app reads
the PR once afterward and uses its `mergeStatus`, with later normal PR GETs
updating the observation. There is no client-side merge operation or timeout
grace. A successful current-target result can be **Clean** immediately.
ADO's `queued` is **Queued**; `notSet` or an unknown status is **Unknown**, not
proof a task is running. Neither status is restarted automatically or manually.
Stale previews remain **Stale**, target/read failures remain **Unverified**, and
failed triggers show **Retry needed**. Accepted triggers retain their timestamp
and merge-cadence retry spacing even if the follow-up PR read fails; that read
error does not become a failed write or prompt an eager duplicate trigger.

Pending or unverified merge information does not hide otherwise available human
actions: **Needs Human** can appear in the first snapshot without any clock
advance. Other pending builds/checks/review-bot gates still take precedence, as
do observed conflicts, failure and policy rejection (**Blocked**). With no
human work, an unverified merge stays **Waiting**, not **Ready**.
Ref-read failures are visible and retried on the next refresh.
Existing merge options are preserved. No merge, branch push, policy change or
auto-complete change is part of these automatic checks.

## Inline Proof of Presence (compatibility prototype)

**PoP** opens the captured PR in a reusable **native Edge WebView2 pane inside
the dashboard**, not a new browser tab. The table remains available above it.
An inset frame separates the browser from the dashboard; the toolbar follows
the app's dark palette with visible hover, keyboard-focus and disabled states.
Drag the grip to resize, or focus it and use Up/Down. The pane shows the PR title/ID and current HTTPS
origin, with Back, Reload, PR page, Check status and Close controls. Website
sign-in popups stay inside this pane.
When a website closes its sign-in popup or PoP page, only that popup or pane
closes; the dashboard stays visible in its existing window state. The dashboard's
own close button and tray Exit keep their normal behavior.

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

## Auto-complete

**Auto** asks Azure DevOps to complete the PR when its policies allow; it does
not approve the PR or bypass checks. Amber **Auto** means auto-complete is not
set and is one of your remaining actions; the expanded Merge card offers the same
**Set auto-complete**. The merge options (for example squash) come from Settings.

After ADO read-back confirms auto-complete, **Auto** becomes a muted grey check
in its existing action slot, including while the pointer remains over the row.
It is still a cancel toggle, not disabled; the expanded row also retains
**Cancel auto-complete**. A confirmed cancellation restores Set auto-complete.
Pending requests guard both controls, and failures/read-back mismatches show
explicit feedback rather than a success state. Refresh, collapse and another
PR selection do not reset the authoritative state or move neighboring rows.

## Explain this PR

Expand a PR and choose **Explain this PR** in **What does this PR do?** Copilot
reads the actual changes and surrounding code, then streams a plain-spoken,
technically deep explanation: what genuinely improves, concrete before/after
behavior, architectural/data-flow changes, externally meaningful API or schema
changes, compatibility risks, and relevant tests and gaps. Small text diagrams
are used only when inspected code establishes the connections. The answer scales
to the change rather than filling a fixed template. A dependency update should identify whether its module
belongs to the product, a developer utility, test infrastructure or a pipeline.
The model inspects relevant README files, consumers and build context rather than
inferring benefits from version numbers. **Evidence and coverage** keeps inspected
paths, revision identifiers and omissions separately, collapsed by default.
Drafts are supported. This is separate from
**Fix PR**: it does not start WSL, edit files, approve, publish or update ADO.

Explain uses the native Windows Copilot SDK and its supported default model.
Sign in with the native Windows Copilot CLI beforehand; the app reuses supported
Copilot authentication, not your ADO token. Missing sign-in, model/access failures
and ADO errors appear inline. One explanation runs at a time; **Cancel** stops it.
Partial or failed responses are labeled and must not be treated as completed.
Results stay in memory until the app exits; **Explain again** starts a fresh session.
After a failure or cancellation, choose **Retry Explain** to start over.

The displayed source, common-base and target commits identify the code being explained;
the target is the PR's actual captured target, not an assumed `main`.
If the PR changes during the request, the result is marked failed. Copilot sees
only app-provided read-only tools for that repository and those commits, not a
shell, arbitrary URLs, local files, installed skills or MCP tools. Repository
text is sent to Copilot for inference. AI explanations can be wrong; verify
important details against the code.

Coverage notes report unread changes, binaries, LFS pointers and size limits.
Explain inventories at most 1,000 changes, permits 60 file/directory reads, and bounds
text files to 80,000 characters, diffs to 24,000 characters and context windows to
120 lines/16,000 characters. Directory listings include at most 200 names.
If the model initially skips required diff or repository-content inspection, it
gets one bounded follow-up to gather that evidence, otherwise an explicit error.
Output is bounded to 64,000 characters, not an 80-word summary.
It times out after ten minutes. Cross-repository
forks and repository paths containing traversal, backslashes, control characters,
`?` or `#` are rejected explicitly.

## PR questions and lens reviews

The expanded Explain card also offers **Ask Copilot about this PR...** and
**Review with a lens...**. Both open a conversation popup. Ask follow-ups in
ordinary English, or choose a concrete review goal such as "Do these ARM changes
work with ACI?" A lens review is read-only and scoped to meaningful bugs or
compatibility failures supported by inspected code. It does not request stylistic
polish, unrelated best practices, speculative problems or a general security audit.
An empty result explicitly says no findings were established in the inspected
scope; it does not certify the PR as bug-free.

Questions and lens reviews require WSL, Git, Python 3 and the existing per-repo
WSL checkout mapping in Settings (otherwise `~/clones/<repo>`). The configured
checkout must have a remote matching the PR's exact organization/project/repository
and existing Git authentication. No extra tokens or keys are requested.
The same validated user environment is loaded for each source operation, without
Fix's WSL Copilot/NVM initialization; questions do not require a WSL Copilot CLI.
The app fetches the source and target refs into unique private refs and creates
a detached **no-checkout Git worktree under the Git common metadata directory**.
It never switches or edits your primary working files. The popup identifies this
worktree honestly: source tools read its pinned Git objects, not a populated
filesystem checkout. This avoids executing checkout filters, hooks, submodules,
skills, build steps or other PR-supplied commands. Symlinks are not followed.
Local Git replacement objects are disabled so inspected content comes from the
captured commit, not an unrelated local substitute. The Python reader uses
isolated mode without site initialization, so working-directory modules,
`PYTHONPATH`, user packages and site hooks cannot run before its pinned reads.
Missing directories (including directories absent before a change) are reported
as explicit omissions, not verified name listings or errors that block follow-ups.
Copilot gets only bounded host-controlled source readers, not a shell or arbitrary
filesystem access. Inference still uses the native Windows Copilot SDK/default
model and native Copilot sign-in, separately from WSL Git authentication; it does
not reuse Fix's broad-permission WSL model session.

Follow-ups retain completed turns and the same PR/source identity. Source and
target refs are rechecked before and after generation and before posting.
Active PRs pin and verify actual branch heads, not a possibly stale merge preview.
Changes block further answers or posting until **New conversation** captures fresh
revisions. Setup, cancellation and source failures are explicit, with no silent
remote-only fallback. **PR investigation activity...** reopens an active job even
after its row is filtered or removed, so its exact cancellation control stays
reachable. Closing the popup does not cancel the job. The popup survives dashboard
refreshes and keeps drafts/conversations separate for each PR.

Lens findings show the affected path, line and exact inspected source quote.
Finding bodies and the supplied lens are shown as literal text; comment publication
escapes Markdown/HTML so image URLs, hidden link targets or markup cannot be posted
without being visible in the confirmed preview.
**Nothing is selected or posted automatically.** Select the findings you want,
choose **Post selected findings...**, and confirm the exact count and PR.
The host rechecks PR identity and revisions, posts only that selection through
ADO's thread API, and shows individual confirmed links and posted/failed/uncertain
counts. Comments include the lens and captured commit IDs. A stable marker
reconciles retries of a preview; POST requests are never automatically retried.
An uncertain result stays uncertain until checked on ADO, not a fabricated success.
ADO offers no atomic revision precondition on thread creation, so a change racing
the final revision check can still make a posted comment refer to older pinned code.

Conversations/results stay in memory until exit. A conversation allows 12 completed
turns, up to 100,000 serialized prior conversation characters (including findings
and posting outcomes), 4,000 characters per question,
120 local source reads and eight preview findings per lens turn. Local text reads
are limited to regular tracked files of 80,000 bytes and 120-line/16,000-character
windows; binary/LFS content is omitted. Shared PR diff limits above still apply.
Oversized text windows are reported as omitted without invalidating later source
reads; select another starting line or file for context.
Inspected source and unread areas are shown separately. No tests are run by source
inspection. Binary, LFS or oversized diff omissions do not by themselves block an
answer if diff inspection was attempted and relevant worktree text was read;
the unread diff areas remain disclosed, not treated as inspected evidence.
An empty captured change inventory can still support questions after worktree text
is read; it cannot supply changed-path evidence for postable findings.
**New conversation** or normal app exit removes only the owned empty
worktree; cleanup refuses if files or index entries were added or HEAD was changed.
An interrupted app or refused cleanup can leave its worktree registered for inspection.

## TikTok This PR

Expand an active, monitored PR and choose **TikTok This PR** beside Explain.
Opus 5.5 inspects pinned diffs and full-file repository context, then creates a
short landscape explainer with natural Azure Speech narration, captions,
animated before/after or journey visuals, and real cats cast into the explanation.
Opus directs bounded compositions, timed reveals, camera motion and recurring cat
roles; it does not supply executable rendering code. Photo provenance remains in
the bundled license/catalog files, not as on-screen origin callouts. This is bounded
source inspection, not a local checkout, and never starts Fix or changes ADO.
Copilot must provide the exact `claude-opus-5.5` model; there is no silent model
or robotic voice fallback.

On first use, choose a subscription and **Find compatible resources**. The app
first checks its bundled Copilot runtime, real-cat catalog/hashes and Windows
H.264/AAC encoders. Successful local checks are reused across generations; no
FFmpeg installer or neural voice-model download is required. A failed component
is named with repair/reinstall and retry guidance; generation cannot start with
missing dependencies. Windows N may need Microsoft's Media Feature Pack.
The app
uses your installed Windows Azure CLI (`az`) and its existing `az login`,
checks Speech-capable Cognitive Services accounts and available English neural
voices, then verifies synthesis with a short generic phrase before saving your
selection. **Ava HD** is preferred when available. Choose another available voice
if desired. Missing `az`, sign-in, read permissions, Speech RBAC, network access
or quota produce explicit errors. No resources are provisioned, keys fetched
or role assignments changed. Discovery is limited to 100 candidate accounts
per selected subscription; an exact account resource ID can also be supplied.
Resource-group names support Azure's Unicode letter and decimal-digit categories;
Speech account names remain ASCII alphanumeric/hyphen names.
Exact-resource input and its open panel survive progress updates and verification
errors so a retry uses the same resource and voice. Speech response-body reads
have a two-minute maximum deadline; a stalled response fails rather than keeping
the only video slot occupied indefinitely.
Public Azure custom subdomain endpoints are currently supported, not sovereign
cloud endpoints.

Each user authenticates independently. Team members typically need
**Cognitive Services Speech User** on the parent account and enough ARM read
access to discover it; access to a Foundry project alone does not establish
Speech access. Standard neural voices need no model deployment. Change the
connection under **Settings > TikTok narration**. Only non-secret resource and
voice identifiers are persisted; Azure CLI owns sign-in, and tokens are kept
in native memory, never sent to Copilot or the WebView.

PR source is sent to Copilot for storyboard generation. Narration text is sent
to the explicitly selected Azure Speech resource and billed to its subscription.
Rendering and the bundled real-cat photos stay on this computer. The curated
catalog includes CC0 and free Unsplash-licensed photographs, with source/license
notices in the application package and no on-screen credit callouts. Each photo
has a visual description and suggested storytelling roles for Opus; it does not
search the web or download new photos during generation.
The video is not uploaded or posted to TikTok. Captions are approximate phrase
timings within measured narration, not word-aligned transcripts. AI-generated
claims still need checking against the code.

Generation can take several minutes. The animated **Generating...** panel shows
actual stages, with no invented percentage or ETA; the dashboard stays usable.
One video runs at a time, duplicate starts are prevented, and **Cancel** remains
available when its PR is filtered out or stops being monitored. Failures provide
**Retry video**; partial output never becomes playable.

Completed videos play inline with controls and seeking. **Copy MP4 path** copies
the actual local file under `%LOCALAPPDATA%\AdoPrView\Videos\completed`.
Private storyboards, inspected-path evidence, narration and scene images are
retained under `Videos\jobs`; treat these as potentially sensitive code-derived
artifacts. Results in the dashboard last until app exit; completed files remain
on disk. Exiting cancels active work, and jobs are not resumed automatically.
Files are validated as actual 1280x720 H.264/AAC media and published atomically.
The player can read only host-registered completed MP4s, not arbitrary local files.

## Inline Copilot Fix

**Fix PR** (the sparkle button in each row, also in the expanded Status card)
starts Copilot in WSL and displays stdout/stderr, status and the actual exit code
**inline in that PR's expanded details**. It does not copy/paste, focus desktop
Copilot or open a separate terminal. **Copy prompt** remains available as an
alternative. Fix is optional: viewing PRs needs neither WSL nor Copilot.

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
`~/.config/bash/environment` supplies PATH/BASH_ENV. The non-login runner keeps
existing PATH resolution first, then tries nvm's default and executable
`~/.local/bin/copilot` if Copilot is still missing. This PATH adjustment is
inherited by runner children only; no shell configuration is edited.
`bgtask` also supports the existing `~/go/bin/bgtask` fallback. Preflight errors
identify the failed dependency or inspection stage and exit code without
displaying environment values, remote URLs or raw tool errors.

Inline-run compatibility checks advertised CLI option names, not exact help
sentences. If a required option is absent, the error lists it. Check
`copilot --help` in the configured WSL distribution; updating the Windows CLI
does not change that installation.

Each run gets a unique, isolated worktree/branch at the freshly verified PR source
commit, not your current/default branch. Main-checkout files, index and branch
are not switched, reset or stashed. Different PRs can run concurrently from one
source repository; duplicate active runs of the same PR are blocked. A fork whose
source is not the mapped repository is rejected rather than fixed in the wrong
checkout. Fetch/worktree metadata are necessarily shared.

Preparation failures identify the failed stage and exit code, distinguishing
recognized authentication, network, path, conflict, dependency and unsupported
Git/checkout errors. A changed source commit requires a fresh PR refresh, not a
retry against an older commit. No existing worktree, branch or lock is removed to
make setup succeed. A worktree is reported created only after Git creates it.
When the app-owned run directory exists, its `prepare.stderr` retains the
original command error with user-only permissions; its path appears in the run
panel. **This private file may contain remote URLs or authentication details:
review and redact it before sharing.** Raw Git errors never go into the dashboard
or lifecycle log. Failed prelaunch records are not resumed.

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
collapsed or another PR is open. If output following disconnects, the app keeps
monitoring the owned task rather than treating the lost output as process exit.
Completion, failure or cancellation clears the indicator only after the actual
task ends. For tracked runs, an exited supervisor alone is not enough: the app
also verifies workspace ownership and a free execution lock before recording
stopped proof or releasing the same-PR reservation. Unconfirmed liveness stays
busy with a needs-attention message and exact-run cancellation controls.
Reduced-motion mode keeps the same busy state
and accessible labels without animation. If the PR leaves the active list entirely (completed,
abandoned or no longer returned), its runs appear, collapsed, below the table in
the unfiltered view only. **Cancel run** targets only that app-owned task.
Changing views never cancels a run.

If the dashboard browser is busy, right-click the native tray icon and select
**Stop Copilot run → !number repository**. This bypasses the browser and does
not wait for unrelated startup recovery. Cancellation signals the owned
transport before durable IO; short status/stop/ownership probes have bounded
response limits independent of transport cancellation callbacks. An explicit
stop, including one arriving before CLI identity capture, escalates TERM to KILL only for the new
runner's own CLI group, after verifying its captured parent/group/session and
Linux start time; a missing or reused leader is refused. No process-name searches or global WSL
termination are used. Already-running runners retain their original scripts.
If descendants or ownership cannot be proven stopped (including a held
execution lock), the app keeps the reservation and reports uncertainty rather
than claiming success. Worktrees and supervised logs remain available.
During blocked or failed shutdown, exact-run controls stay available until
other service teardown succeeds. A failed exit restores browser output delivery.
Native Stop entries include the full run ID to distinguish retained runs for
the same PR. Unreadable history without a PR identity is not an actionable target.
New launches capture the Linux HOME, XDG config directory and any
`BGTASK_STATE_DIR` override. Launch, status, Stop and log access use that same
registry even if shell configuration changes. Exact-task control and ownership
probes do not re-source environment files for these captured runs, so a blocked
setup cannot redirect or hold their Stop request. Recovery refuses automatic
resume if the captured registry changed; explicit Stop still checks the original
task identity. Older records without registry context retain ownership checks
and report missing/ambiguous tasks as unconfirmed rather than guessing a registry.
Private prompt removal also bypasses shell initialization and has a separate
two-second response limit. If removal fails or times out after a verified Stop,
the task remains truthfully stopped with its work retained and an explicit
private-prompt cleanup warning; cleanup cannot keep Stop waiting for setup.
Ownership/history commands likewise bypass initialization for older records,
using the same environment as their identity-checked status command rather
than sourcing a potentially different registry after Stop.
Stop also interrupts pending recovery for a needs-attention record whose task
is already confirmed stopped. It rechecks owned identity and the free execution
lock without sending another Stop to an exited task, and preserves its real
exit code rather than replacing it with a synthetic cancellation code.
Recovery log replay observes Stop cancellation. Once Stop is verified, replay
has an independent two-second limit; unavailable history reports a warning
without changing verified stopped state or removing retained logs/work.
An interrupted replay retains received output and its offsets instead of
restarting the same log from the beginning. A new dashboard can replay the
retained log again; Stop does not duplicate history in the current run.
Native Stop includes returned cleanup/history diagnostics in its own warning
dialog, even when the dashboard is unavailable. A warning does not undo verified
inactive state or imply that an unconfirmed task stopped.
An already-captured Stop action also interrupts pending terminal history after
the task exits, even if its displayed status has already changed to completed
or failed. Terminal status does not mean background reconciliation has finished.
Completed-run cleanup also observes Stop and dashboard detachment. Interrupted
cleanup retains the verified completed outcome and reports a cleanup warning;
an abandoned late reply cannot mark the worktree removed. Normal cleanup keeps
its existing ownership, publication and execution-lock checks.

Output uses a grow-on-demand tail buffer with the same retained-output limit,
so empty or short saved histories do not each reserve the maximum buffer.
Native delivery keeps at most one
unacknowledged browser message plus the latest pending snapshot per run. A busy
browser cannot accumulate an unlimited output/dispatcher queue. Updates
retain the latest terminal/status update while acknowledgements are missing;
pending runs are served fairly even while another run keeps streaming.
Older lifecycle/output replay cannot replace a newer pending or in-flight
snapshot during delivery restoration.
Repeated errors during one unresolved delivery failure are reported once.
A successful resynchronization post resets that suppression, so a later
delivery failure is reported again; abandoned late posts do not reset it.
Dashboard ready/init resynchronizes after navigation or reload. Monotonic
lifecycle revisions reject old same-output status/worktree snapshots. The initial
dashboard response also reads authoritative run states and the latest PR snapshot
after asynchronous lists finish. Whole PR snapshots have a separate publication
revision, so delayed init/refresh messages cannot replace newer displayed or
queued state. This does not change row-order or interaction-pause behavior.
These changes do not cap CPU/RAM or automatically kill tasks; whole-system resource exhaustion
can still affect the app and Windows.


**Open in Azure DevOps** and external links use the same validated HTTPS
launcher. If Windows cannot open its default browser, the action reports an
error instead of closing the dashboard; check Windows' default HTTPS app or
copy the PR URL manually. Native event or dashboard-delivery failures are
recorded as `native-action-failed` diagnostics with safe exception structure,
and report a native error when the dashboard cannot display one.

## Restart recovery and worktree cleanup

Tracked launches are recorded atomically under
`%LOCALAPPDATA%\AdoPrView\CopilotRuns`, separately from settings and credentials.
Records contain captured PR/workspace/task/session identity, task-registry paths, status and log
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
