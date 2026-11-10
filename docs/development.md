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

The pinned `GitHub.Copilot.SDK` package downloads and verifies its matching
Windows Copilot runtime at build time. Explain uses this native runtime.
Release packaging includes the runtime's redistribution license and the SDK,
DiffPlex and Microsoft.Extensions license texts under `licenses`.
The retained upstream texts come from [DiffPlex 1.9.0](https://github.com/mmanela/diffplex/blob/3cb6415c331a0af2f2686154cb77169ef29b63ce/License.txt),
[Copilot SDK 1.0.29](https://github.com/github/copilot-sdk/blob/v1.0.29/LICENSE),
[Copilot CLI 1.0.107](https://github.com/github/copilot-cli/blob/v1.0.107/LICENSE.md)
and [.NET Extensions](https://github.com/dotnet/extensions/blob/main/LICENSE).
Review these texts when updating the dependencies or their bundled runtime.

Explain's native fake-HTTP/fake-model checks and frontend checks run in the
ordinary offline suites. The browser mock simulates Explain interactions;
it does not contact a model or establish live ADO endpoint compatibility.

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
synthetic fixtures. The screenshots in [images](images) were captured from that
mock UI with synthetic data.

See [DESIGN.md](DESIGN.md) for the architecture, approval provider contract,
ADO calls, classification and merge preview scheduling.
