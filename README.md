# ADO PR View

In the Azure DevOps portal, especially with a lot of pull requests, it is hard
to tell at a glance which PRs have finished all their machine validation and now
only need a person to review, approve or finish them. ADO PR View is a small
Windows app that shows your PRs, and those of people you choose to watch, in one
table sorted by what they are waiting on: **Needs Human**, **Blocked**,
or **Waiting**. A **Completed** view lists watched PRs merged in the last 7 days.

![The default Light Mode dashboard: lane counters in the header, and one row per PR with its lane, gates and actions](docs/images/overview.png)

Screenshots use the default Light Mode with synthetic PR data.

## Get it

- **Download:** the [latest release](../../releases/latest) has a per-user
  installer (`AdoPrView-v<version>-win-x64-setup.exe`, no administrator rights),
  a portable ZIP of the same app, and SHA-256 checksums. It needs Windows 10/11
  x64 and the Microsoft Edge WebView2 Evergreen Runtime; .NET is included. The
  files are not code-signed, so SmartScreen may warn. Close any running
  dashboard before installing, upgrading or uninstalling; setup refuses to run
  while one is open and never touches your settings and data in
  `%LOCALAPPDATA%\AdoPrView`.
- **Set it up with an agent:** give [INSTALL.md](INSTALL.md) to a CLI coding
  agent. It has a copyable kickoff prompt. The agent finds your existing Azure
  sign-in, repositories and settings, checks your ADO identity with read-only
  requests, merges configuration safely and launches the dashboard.

## What it shows you

### Needs Human: a reviewer still has to approve

![Two PRs needing reviewer approval, each with an enlarged +1 button; one also needs PoP](docs/images/needs-human-review.png)

The PR needs reviewer approval, with no observed blockers or other pending
automated gates. A pending or unverified merge preview remains visible without
hiding available human work. The **+1** button is enlarged on these rows. Any approver can
give it, so watched PRs show it too; it is amber only on your own PRs. The +1
can appear alongside finishing steps, like PoP on the second row.

### Needs Human: your finishing steps

![Three PRs with finishing actions: PoP and Auto on your PR, PoP on a watched PR, and a draft needing +1, Publish and Auto](docs/images/needs-human-finish.png)

The finishing actions are **PoP** (Proof of Presence with a security key),
**Auto** (set auto-complete) and **Publish** (a draft). They can appear alone,
in any combination, or alongside +1 when a review is also still needed, as on
the draft. They are amber on your PRs; a watched PR shows the same steps without
amber.

Needs Human highlights available human work; it is not a guarantee that the
merge preview is current or complete, or that every policy is known or green.

### Inline Copilot Fix

![A blocked PR expanded with an inline Copilot run panel showing synthetic demo output and a Cancel run button](docs/images/copilot-fix-inline.png)

The sparkle button on a row starts Copilot CLI in WSL to fix that PR in its own
isolated worktree. Its output streams into the PR's expanded row, where you can
follow or cancel it. The output in this picture is synthetic demo data. Fix is
optional and needs WSL with an authenticated Copilot CLI; it runs with broad
permissions and the worktree is not a sandbox. Read
[Inline Copilot Fix](docs/usage.md#inline-copilot-fix) before using it.

### Explain, ask questions and review with a lens

Expanded PRs offer a plain-spoken, code-grounded deep explanation of what
genuinely improves, architecture, API changes and meaningful risks.
[PR questions and lens reviews](docs/usage.md#pr-questions-and-lens-reviews)
open a follow-up conversation backed by a pinned isolated WSL Git worktree.
Lens findings are private previews: only findings you select and confirm are
posted to ADO, with exact outcomes and confirmed comment links.

### TikTok This PR

Expanded PRs also offer [TikTok This PR](docs/usage.md#tiktok-this-pr): an
Opus 5.5 code-grounded short explainer with Azure neural narration, captions,
animated visuals and real cats integrated into the explanation, rendered locally
and played inline. First use
selects an accessible Azure Speech resource using your Azure CLI sign-in.
Speech processes narration in Azure and charges that resource; no API key or
extra model deployment is needed.

### Merge re-checks and auto-complete

<img src="docs/images/merge-auto.png" width="480" alt="The expanded Status and Merge cards with Re-check merge and Set auto-complete buttons">

- **Merge re-checks:** ADO's merge preview goes stale when the target branch
  moves. The app periodically asks ADO to recompute it, and **Re-check merge**
  does it on demand. It never merges or pushes.
- **Auto** asks Azure DevOps to complete the PR when its policies allow; it does
  not approve the PR or bypass checks. Once set, it turns into a grey check
  that you can click to cancel.
- **Failed builds:** expand a blocked row to see the failed tasks, their errors
  and the end of the log, with **Re-queue** and **Open logs**.

## What you still do after an agent sets it up

The agent does the configuration. You may still need to:

- complete any sign-in, MFA, SSO or consent prompt it cannot do for you;
- answer a question about which repositories, watched authors or WSL checkout
  to use, only if it cannot work that out;
- sign in to Copilot CLI in WSL, only if you want inline Copilot Fix;
- do the actual reviewing, approving, Proof of Presence and other PR decisions.
  The app shows you what is left; it does not do these for you.

Viewing PRs needs neither WSL nor Copilot.

## Documentation

- [INSTALL.md](INSTALL.md): agent-first installation and update guide.
- [docs/usage.md](docs/usage.md): lanes, row actions, keys, diagnostics,
  merge re-checks, Proof of Presence, auto-complete, Copilot Fix and recovery.
- [docs/DESIGN.md](docs/DESIGN.md): architecture, classification rules and the
  ADO calls used.
- [docs/development.md](docs/development.md): building, offline tests and
  releases.

## License

[MIT](LICENSE)
