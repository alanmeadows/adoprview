# Development

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

## Offline checks

No live approval, PR or auth requests are made:

```powershell
dotnet build tests\AdoPrView.Tests -c Release -warnaserror
dotnet run --project tests\AdoPrView.Tests -c Release --no-build
node --test tests\approval-ui.test.cjs
node --test tests\row-actions.browser.test.cjs
powershell -NoProfile -ExecutionPolicy Bypass -File tests\install-guide.tests.ps1
```

CI and the release package treat compiler warnings as errors (`-warnaserror`
on the build and `dotnet publish`), so keep both projects warning-clean.

The browser regression uses Node 22+ and the installed Microsoft Edge (or set
`ROW_TEST_BROWSER` to another Chromium executable). Real mouse/keyboard input
is used, with all URL launches and PR actions intercepted; no live PR is changed.
It also renders hostile synthetic PR description/comment markdown through the
vendored marked and DOMPurify and checks no script runs or native-bridge call is made.
Native fake-HTTP/fake-clock checks cover periodic/forced restarts, target-change
sweeps, retries, in-flight deduplication and fresh result handling. The install
guide check parses every PowerShell block in INSTALL.md and runs its settings
merge against a disposable folder.

Draft-monitoring regressions use fake HTTP responses to cover opt-in persistence,
skipped detail reads and writes, publication/back-to-draft transitions, and stale
notification delivery. Frontend checks cover the Ignored drafts filter, deferred
snapshots and retained Fix controls without starting a live Fix.

The opt-in WSL preflight regression uses a disposable `/tmp` checkout and fake
CLI executables. It checks non-login discovery, quoted paths, inherited child
PATH and dependency diagnostics without starting a Fix or contacting ADO:

```powershell
$env:ADOPRVIEW_TEST_FILTER = 'WSL preflight'
$env:ADOPRVIEW_TEST_WSL_PREFLIGHT = '1'
dotnet run --project tests\AdoPrView.Tests
Remove-Item Env:\ADOPRVIEW_TEST_FILTER, Env:\ADOPRVIEW_TEST_WSL_PREFLIGHT
```

Direct reviewer approval has a focused offline native suite:

```powershell
$env:ADOPRVIEW_TEST_FILTER = 'Reviewer vote'
dotnet run --project tests\AdoPrView.Tests -c Release
Remove-Item Env:\ADOPRVIEW_TEST_FILTER
```

Its fake HTTP transport checks the authenticated reviewer-only payload, captured
source/target revisions, latest iteration, monitoring eligibility, duplicate
requests, nonretrying writes and explicit ambiguity on post-write races or
read-back failure. The frontend and browser suites exercise one-click row and
expanded actions, busy/error/confirmed states, provider coexistence and stable
neighboring action targets. All approval writes in these checks are intercepted.

`src\AdoPrView\wwwroot\index.html?mock=1` runs the UI in a browser against
synthetic fixtures. The screenshots in [images](images) were captured from that
mock UI with synthetic data.

See [DESIGN.md](DESIGN.md) for the architecture, approval provider contract,
ADO calls, classification and merge preview scheduling.
