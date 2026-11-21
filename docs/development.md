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
Explain explicitly selects the bundled `runtimes\win-<process architecture>\native`
stdio wrapper, not the OS architecture: the x64 release also runs under Windows
ARM64 emulation. SDK 1.0.29's default resolver uses the OS architecture instead.
Missing or empty runtime files fail explicitly; there is no global CLI fallback.
Release packaging includes the runtime's redistribution license and the SDK,
DiffPlex and Microsoft.Extensions license texts under `licenses`.
The retained upstream texts come from [DiffPlex 1.9.0](https://github.com/mmanela/diffplex/blob/3cb6415c331a0af2f2686154cb77169ef29b63ce/License.txt),
[Copilot SDK 1.0.29](https://github.com/github/copilot-sdk/blob/v1.0.29/LICENSE),
[Copilot CLI 1.0.107](https://github.com/github/copilot-cli/blob/v1.0.107/LICENSE.md)
and [.NET Extensions](https://github.com/dotnet/extensions/blob/main/LICENSE).
The Windows SDK .NET projection's Microsoft license and C#/WinRT MIT notice are
retained under `licenses\Microsoft.Windows.SDK.NET.Ref`. The official
[Windows SDK redist list](https://learn.microsoft.com/en-us/legal/windows-sdk/redist#microsoftwindowssdknetref)
explicitly permits its unmodified projection/runtime files for WinRT API calls;
they are not relicensed under this application's MIT license.
Review these texts when updating the dependencies or their bundled runtime.

Explain's native fake-HTTP/fake-model checks and frontend checks run in the
ordinary offline suites. The browser mock simulates Explain interactions;
it does not contact a model or establish live ADO endpoint compatibility.
The native suite also checks the actual runtime binaries' PE architecture without
starting Copilot or authenticating. To check a published folder, set
`ADOPRVIEW_TEST_PUBLISH_DIRECTORY` to its absolute path and run the native suite
with `ADOPRVIEW_TEST_FILTER` set to `Explain runtime`.

PR investigation tests use fake models and synthetic ADO responses, including
follow-up identity, cancellation, stale revisions, lens preview validation,
explicit selection/confirmation, idempotency and partial/uncertain posting.
Run `dotnet run --project tests\AdoPrView.Tests` with
`ADOPRVIEW_TEST_FILTER=Investigation|Explain` for these targeted checks.
Set `ADOPRVIEW_TEST_WSL_INVESTIGATION=1` to also exercise the production worktree
scripts against a disposable local Git upstream in WSL; it does not authenticate
or contact ADO/Copilot. The fixture checks pinned before/after reads, symlink/LFS/
size omissions, no executed checkout filters and an unchanged primary checkout.
`node --test tests\investigation.browser.test.cjs` exercises the real popup in a
temporary headless Edge profile with synthetic host replies, never live comments.

## Offline checks

PR video uses Windows media APIs through the Windows SDK target framework,
not bundled FFmpeg or downloaded voice models. Azure Speech uses bounded REST
requests authenticated by the user's Windows Azure CLI, independently of
Copilot authentication. The model never receives Azure credentials or a shell.
Cat provenance/license notices and verified thumbnail hashes ship with the app.
The precurated `VideoAssets\cats.json` contains each photo's selection ID,
visual description/storytelling hints, author, source, approved license and
SHA-256. Opus receives only ID, description and framing hints; generation has
no photo-search/download tool. The catalog is bounded to 2-8 assets. To replace
or extend it within that bound, verify the original license, add the JPEG and
manifest/notice entry, and set its full-photo and face crops in
`VideoRenderer.Looks`. Update the release package's required-asset checks.
Do not treat Google Images or a stock search's "free" label as reuse permission.
The current set uses five CC0 photographs and three free Unsplash photographs,
not Unsplash+ or Pixabay assets; their own licenses, not MIT, cover the photos.
Opus directs a bounded animated storyboard rather than generated renderer code:
6-10 scenes, 120-210 spoken words and 30-150 seconds of measured narration.
The host draws at 15 fps (at most 2,250 frames), encodes H.264/AAC at 30 fps,
and removes intermediate frame JPEGs afterward. Motion-heavy videos can be
substantially larger than static slides; the observed 69-second pilot was 53 MB.
Only narration, scene keyframes, storyboard/evidence and the completed MP4 are
retained normally; cleanup failures are surfaced rather than silently ignored.

No live approval, PR or auth requests are made:

```powershell
dotnet build tests\AdoPrView.Tests -c Release -warnaserror
dotnet run --project tests\AdoPrView.Tests -c Release --no-build
node --test tests\approval-ui.test.cjs
node --test tests\row-actions.browser.test.cjs
powershell -NoProfile -ExecutionPolicy Bypass -File tests\install-guide.tests.ps1
```

`ADOPRVIEW_TEST_FILTER=Video|Explain` selects source/video/native offline
regressions, including fake Azure CLI/HTTP resource discovery, denied synthesis,
SSML escaping, media validation failure, cancellation and range requests.
The browser suite includes first-use setup and responsive video controls.
The browser mock never invokes Azure, Copilot or a renderer and never pretends
its synthetic generation produced a real MP4.

Explicit integration harnesses are separate from offline suites:

```powershell
dotnet run --project tests\AdoPrView.Tests -c Release -- --video-discovery-test <account-resource-id> <evidence-directory> <voice-name>
dotnet run --project tests\AdoPrView.Tests -c Release -- --video-speech-test <account-resource-id> <output-directory> <voice-name>
dotnet run --project tests\AdoPrView.Tests -c Release -- --video-pilot <this-public-repo-checkout> <artifact-directory> <account-resource-id> <voice-name>
dotnet run --project tests\AdoPrView.Tests -c Release -- --video-playback <actual-mp4-path> <evidence-directory>
```

Speech tests incur Azure usage for a generic sample sentence. The pilot also
uses real Copilot Opus 5.5 and Speech calls; its ADO GET transport is synthetic
around authentic pinned public PR #19 Git objects, not a production private PR.
No ADO writes are permitted. It records actual-vs-synthetic boundaries, timing,
source evidence, storyboard and media artifacts outside Git. Playback uses a
disposable native WebView2 profile and verifies decoded audio/video, seek,
HTTP ranges, unregistered-URL rejection and player preservation. It does not
establish a human listening judgment; audition the actual generated WAV/MP4.

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

Worktree preparation and native browser failure checks have separate opt-ins:

```powershell
$env:ADOPRVIEW_TEST_FILTER = 'Copilot preparation|Native action'
$env:ADOPRVIEW_TEST_WSL_PREPARE = '1'
$env:ADOPRVIEW_TEST_NATIVE_ACTION = '1'
dotnet run --project tests\AdoPrView.Tests -c Release
Remove-Item Env:\ADOPRVIEW_TEST_FILTER, Env:\ADOPRVIEW_TEST_WSL_PREPARE, Env:\ADOPRVIEW_TEST_NATIVE_ACTION
```

Preparation uses only disposable `/tmp` Git repositories, a synthetic Git
failure shim and the production setup script: no live remote, model, supervisor
job or installed app state. It covers quoted/linked checkouts, preserved dirty
source files, exact commits and safe stage/reason/exit errors. The native action
probe uses a disposable WPF WebView2 profile, intercepts popup launch with a
synthetic Windows failure and verifies the dashboard stays visible/responding.
Browser row checks cover lost/uncertain process activity and failed action
feedback with both normal and reduced motion.

Focused Fix output/control checks use only fake transports, small synthetic
output and disposable settings/history, without a model or live ADO requests:

```powershell
$env:ADOPRVIEW_TEST_FILTER = 'Copilot'
dotnet run --project tests\AdoPrView.Tests -c Release
node --test --test-name-pattern='native Fix delivery|pre-navigation Fix|Copilot' tests\approval-ui.test.cjs
Remove-Item Env:\ADOPRVIEW_TEST_FILTER
```

They cover bounded per-chunk allocations, a follower that ignores
cancellation, stop/detach with retained output, uncertain owned liveness and
browser acknowledgement/coalescing/disposal, fair delivery between runs,
independent response deadlines with blocking/throwing cancellation callbacks,
and intentional recovered-history replay after cancellation, including a
potentially live needs-attention run blocked in preflight. They also cover
startup/shutdown reservation races, controls surviving failed teardown,
synchronous posting hangs and same-output lifecycle revisions. The existing
`ADOPRVIEW_TEST_NATIVE_ACTION=1` opt-in also enables `Native Copilot`: a real
disposable WPF/WebView2 window with withheld output acknowledgements, a fake
hung follower, an actual unrelated recovery blocked at its fake preflight, and
the production shutdown sequence held before final control disposal.
The native button stops the selected run while those sentinels remain blocked,
asserts a three-second control deadline and prints the measured milliseconds.
It also verifies retained output/work and durable exact-task stop proof.
This bounded synthetic check is
not an installed-app or live WSL/model end-to-end test. Do not run browser or WSL
opt-ins during unresolved shared-machine overload.

The smaller kernel-control opt-in executes the exact production runner in an
explicitly selected, already-responsive WSL distribution:

```powershell
$env:ADOPRVIEW_TEST_FILTER = 'Copilot kernel'
$env:ADOPRVIEW_TEST_WSL_CONTROL = '1'
$env:ADOPRVIEW_TEST_WSL_DISTRO = 'Ubuntu'
dotnet run --project tests\AdoPrView.Tests -c Release
Remove-Item Env:\ADOPRVIEW_TEST_FILTER, Env:\ADOPRVIEW_TEST_WSL_CONTROL, Env:\ADOPRVIEW_TEST_WSL_DISTRO
```

It requires Bash, Python 3 with Linux pidfd support, `flock`, `setsid` and
`timeout`, plus permission to become a child subreaper. A disposable sleeping
CLI and its descendant ignore TERM so the runner must escalate only its owned
group to KILL. The fixture checks the real exit receipt, free execution lock,
preserved synthetic work/logs, a held orphan-descendant lock and refusal of a
foreign ownership receipt. It additionally exercises the verbatim production
signal guards with stale-start/absent captured identities and a real disposable
leader; it does not force genuine host PID reuse. A fixture-only `BASH_ENV`
DEBUG hook schedules TERM immediately before the production identity-capture
assignment, proving pending Stop is replayed. A deliberately missing readiness
file verifies cleanup of unregistered descendants. The report includes measured
TERM-to-KILL time. It has a 20-second Linux deadline, a 25-second host
cancellation deadline and identity-checked, pidfd-backed cleanup of only its
own descendant tree, independent of successful readiness. Cleanup has a
two-second deadline; unconfirmed cleanup preserves the fixture directory and
fails rather than claiming success.
It never invokes Copilot, bgtask, ADO, installed app state or existing user jobs;
it does not establish live supervisor integration or installed-app E2E.

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

## Theme design and checks

`wwwroot/themes.css` defines semantic canvas, surface, foreground, border,
control, focus, status and output roles for `light` and `dark`.
Components in `app.css` consume these roles rather than fixed colors.
`AppTheme.cs` mirrors only the small native chrome subset; a native test checks
it against the CSS. Set `color-scheme` per palette for native browser controls.

Light Mode is a Primer-inspired theme, not a clone of github.com's navigation
or a statement about its current default theme. The sources are official
[Primer color primitives](https://primer.style/product/primitives/color/),
[color usage](https://primer.style/product/getting-started/foundations/color-usage/),
[typography](https://primer.style/product/getting-started/foundations/typography/)
and [buttons](https://primer.style/product/components/button/). The bundled subset
adapts [Primer primitives 11.10.0 light tokens](https://cdn.jsdelivr.net/npm/@primer/primitives@11.10.0/dist/css/functional/themes/light.css):
white/subtle gray canvases, cool gray borders, blue links/focus, green primary
actions, 6px radii, 1px borders and system fonts. The upstream MIT notice is
retained at `licenses/Primer/LICENSE.txt` and included in build/publish output.
No Primer runtime package, CDN request, remote font, GitHub logo or endorsement
is involved.

Native `settings.json` is the only preference authority. `setTheme` validates
and atomically saves only that field, rolling it back on failure, then updates
native chrome. General settings saves deliberately cannot reset a separately
saved theme. `AppTheme.DashboardUri` supplies a derived `?theme=` first-paint
hint; the synchronous `theme.js` applies it before the stylesheet and updates
the current URL after confirmed saves so reloads also start with the right
palette. There is no localStorage preference. Missing keys default to Light Mode.
The former `github` value normalizes to `light` in memory and on subsequent saves,
without rewriting the file at load. Unknown future values render the effective
default with a notice without being silently rewritten.

Busy/refresh indicators preserve their state and accessible labels when
`prefers-reduced-motion` disables animation. Browser checks explicitly emulate
both `no-preference` and `reduce`, independent of the test host's OS preference.

The regular frontend/browser checks cover live switching, failure rollback,
reload, first-paint defaults, representative computed colors/contrast and
hover/focus/disabled states. To save synthetic screenshots, set
`ADOPRVIEW_THEME_SCREENSHOTS` to an output directory before running the browser
suite. The optional native check uses a disposable settings file and WebView2
profile, the real bridge and selector, and no ADO requests or installed app data:

```powershell
$env:ADOPRVIEW_TEST_FILTER = 'theme'
$env:ADOPRVIEW_TEST_NATIVE_THEME = '1'
dotnet run --project tests\AdoPrView.Tests -c Release
Remove-Item Env:\ADOPRVIEW_TEST_FILTER, Env:\ADOPRVIEW_TEST_NATIVE_THEME
```

## Native PoP checks

CI and release checks enable the native PoP regression with the installed
WebView2 Evergreen Runtime; the WebView2 SDK is not downgraded.
The same check can be enabled locally. It intercepts all website requests with synthetic pages
and uses a disposable browser profile. It exercises sign-in popup/page
`window.close()` in normal and maximized windows, captured-policy rechecks,
stale-close selection isolation, host disposal and ordinary deliberate window close,
without real sign-in or PR writes. The fixture emulates close-to-tray so an
unexpected host close cannot pass merely because the process remains alive.
These synthetic checks do not establish that the full tenant sign-in flow in
[#15](https://github.com/alanmeadows/adoprview/issues/15) is resolved; human validation remains outstanding.

```powershell
$env:ADOPRVIEW_TEST_FILTER = 'Native PoP WebView2'
$env:ADOPRVIEW_TEST_POP_WEBVIEW = '1'
dotnet run --project tests\AdoPrView.Tests -c Release
Remove-Item Env:\ADOPRVIEW_TEST_FILTER, Env:\ADOPRVIEW_TEST_POP_WEBVIEW
```

See [DESIGN.md](DESIGN.md) for the architecture, approval provider contract,
ADO calls, classification and merge preview scheduling.
