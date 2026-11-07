# CI-only smoke test of the release assets built by New-ReleasePackage.ps1.
# It installs, launches, blocks, and uninstalls the app on the disposable
# runner, so it refuses to run anywhere else.
param(
    [Parameter(Mandatory)][string]$ArtifactDirectory,
    [Parameter(Mandatory)][string]$Version
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:GITHUB_ACTIONS -ne 'true') { throw 'This smoke test installs and removes the app; it only runs on a GitHub Actions runner.' }

$name = "AdoPrView-v$Version-win-x64"
$zip = Join-Path $ArtifactDirectory "$name.zip"
$setup = Join-Path $ArtifactDirectory "$name-setup.exe"
$sumsFile = Join-Path $ArtifactDirectory "AdoPrView-v$Version-checksums.txt"

$sums = @(Get-Content -LiteralPath $sumsFile | Where-Object { $_ })
if ($sums.Count -ne 2) { throw "Expected 2 checksum lines, found $($sums.Count)." }
foreach ($line in $sums) {
    if ($line -notmatch '^([0-9a-f]{64})  (\S+)$') { throw "Malformed checksum line: $line" }
    $actual = (Get-FileHash -LiteralPath (Join-Path $ArtifactDirectory $Matches[2]) -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $Matches[1]) { throw "Checksum mismatch for $($Matches[2])." }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    $entries = @{}
    foreach ($entry in $archive.Entries) {
        if (-not $entry.Name) { continue }
        $stream = $entry.Open()
        try { $entries[$entry.FullName.Replace('/', '\')] = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
        finally { $stream.Dispose() }
    }
}
finally { $archive.Dispose() }
foreach ($required in 'AdoPrView.exe', 'hostfxr.dll', 'coreclr.dll', 'PresentationFramework.dll', 'WebView2Loader.dll',
        'wwwroot\index.html', 'LICENSE', 'THIRD-PARTY-NOTICES.txt') {
    if (-not $entries.ContainsKey($required)) { throw "ZIP is missing $required." }
}
if ($entries.Keys | Where-Object { $_ -like '*.pdb' }) { throw 'ZIP unexpectedly contains PDB files.' }
Write-Output "Checksums and ZIP inventory OK ($($entries.Count) files)."

$webView2 = 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
if (-not (Test-Path $webView2)) { throw 'The WebView2 Evergreen Runtime is not installed on this runner.' }

$data = Join-Path $env:LOCALAPPDATA 'AdoPrView'
$sentinel = Join-Path $data 'installer-smoke-sentinel.txt'
New-Item -ItemType Directory -Force -Path $data | Out-Null
Set-Content -LiteralPath $sentinel -Value 'user data must survive install and uninstall'
$dir = Join-Path $env:RUNNER_TEMP 'AdoPrView-install'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'ADO PR View.lnk'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{5D2F66AD-977D-4B94-BD69-094B8FD6E532}_is1'

function Invoke-Setup([string]$Log) {
    (Start-Process -FilePath $setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/DIR=`"$dir`"", "/LOG=`"$Log`"" -Wait -PassThru).ExitCode
}
function Invoke-Uninstall {
    $code = (Start-Process -FilePath (Join-Path $dir 'unins000.exe') -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait -PassThru).ExitCode
    Start-Sleep -Seconds 5
    $code
}

$code = Invoke-Setup (Join-Path $env:RUNNER_TEMP 'setup-install.log')
if ($code -ne 0) { throw "Silent install failed with exit code $code." }
foreach ($file in $entries.Keys) {
    $installed = Join-Path $dir $file
    if (-not (Test-Path -LiteralPath $installed)) { throw "Installed app is missing $file." }
    if ((Get-FileHash -LiteralPath $installed -Algorithm SHA256).Hash -ne $entries[$file]) { throw "Installed $file differs from the ZIP." }
}
if (-not (Test-Path -LiteralPath $shortcut)) { throw 'Start Menu shortcut was not created.' }
if ((Get-ItemProperty -LiteralPath $uninstallKey).DisplayVersion -ne $Version) { throw 'Per-user uninstall entry has the wrong version.' }
Write-Output 'Per-user install matches the ZIP byte-for-byte; shortcut and uninstall entry present.'

$png = Join-Path $env:RUNNER_TEMP 'installed-mock.png'
$capture = Start-Process -FilePath (Join-Path $dir 'AdoPrView.exe') -ArgumentList '--mock', '--screenshot', "`"$png`"" -PassThru
if (-not $capture.WaitForExit(120000)) { Stop-Process -Id $capture.Id; throw 'Installed app did not finish a mock screenshot within 120 seconds.' }
if (-not (Test-Path -LiteralPath $png) -or (Get-Item -LiteralPath $png).Length -lt 10KB) { throw 'Installed app did not render its mock dashboard.' }
Write-Output "Installed app rendered its mock dashboard ($((Get-Item -LiteralPath $png).Length) bytes)."

$running = Start-Process -FilePath (Join-Path $dir 'AdoPrView.exe') -ArgumentList '--mock' -PassThru
try {
    Start-Sleep -Seconds 5
    if ($running.HasExited) { throw "Installed app exited early with code $($running.ExitCode)." }
    $code = Invoke-Setup (Join-Path $env:RUNNER_TEMP 'setup-blocked.log')
    if ($code -eq 0) { throw 'Setup did not refuse to run while the dashboard was open.' }
    $code = Invoke-Uninstall
    if (-not (Test-Path -LiteralPath (Join-Path $dir 'AdoPrView.exe'))) { throw 'Uninstall removed the app while the dashboard was open.' }
    if ($running.HasExited) { throw 'Setup or uninstall closed the running dashboard.' }
    Write-Output 'Setup and uninstall both refused to touch a running dashboard.'
}
finally {
    if (-not $running.HasExited) { Stop-Process -Id $running.Id; $running.WaitForExit() }
}

$code = Invoke-Uninstall
if ($code -ne 0) { throw "Silent uninstall failed with exit code $code." }
if (Test-Path -LiteralPath (Join-Path $dir 'AdoPrView.exe')) { throw 'Uninstall left the app executable behind.' }
if (Test-Path -LiteralPath $shortcut) { throw 'Uninstall left the Start Menu shortcut behind.' }
if (Test-Path -LiteralPath $uninstallKey) { throw 'Uninstall left the uninstall entry behind.' }
if (-not (Test-Path -LiteralPath $sentinel)) { throw 'Install or uninstall removed user data.' }
Write-Output 'Uninstall removed the app, shortcut and uninstall entry and preserved user data.'
Remove-Item -LiteralPath $sentinel
