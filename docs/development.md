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

The opt-in WSL preflight regression uses a disposable `/tmp` checkout and fake
CLI executables. It checks non-login discovery, quoted paths, inherited child
PATH and dependency diagnostics without starting a Fix or contacting ADO:

```powershell
$env:ADOPRVIEW_TEST_FILTER = 'WSL preflight'
$env:ADOPRVIEW_TEST_WSL_PREFLIGHT = '1'
dotnet run --project tests\AdoPrView.Tests
Remove-Item Env:\ADOPRVIEW_TEST_FILTER, Env:\ADOPRVIEW_TEST_WSL_PREFLIGHT
```

`src\AdoPrView\wwwroot\index.html?mock=1` runs the UI in a browser against
synthetic fixtures. The screenshots in [images](images) were captured from that
mock UI with synthetic data.

See [DESIGN.md](DESIGN.md) for the architecture, approval provider contract,
ADO calls, classification and merge preview scheduling.
