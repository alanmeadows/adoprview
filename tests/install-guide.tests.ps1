# Offline checks for the PowerShell in INSTALL.md. No network request is made and
# the real %LOCALAPPDATA% is never written: the settings merge runs against a
# disposable folder next to this script.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Off

$guide = Join-Path $PSScriptRoot '..\INSTALL.md'
$text = [IO.File]::ReadAllText($guide) -replace "`r`n", "`n"
$blocks = @([regex]::Matches($text, '(?ms)^[ \t]*```powershell\n(.*?)^[ \t]*```') | ForEach-Object { $_.Groups[1].Value })
$failures = 0
function Check([string]$Name, [scriptblock]$Body) {
    try { & $Body; Write-Host "ok   $Name" }
    catch { $script:failures++; Write-Host "FAIL $Name -- $($_.Exception.Message)" }
}
function Find-Block([string]$Marker) {
    $found = @($blocks | Where-Object { $_.Contains($Marker) })
    if ($found.Count -ne 1) { throw "Expected one INSTALL.md block containing '$Marker', found $($found.Count)." }
    $found[0]
}

Check 'every powershell block parses' {
    if ($blocks.Count -lt 5) { throw "Only $($blocks.Count) powershell blocks found." }
    foreach ($block in $blocks) {
        $errors = $null
        [void][Management.Automation.Language.Parser]::ParseInput($block, [ref]$null, [ref]$errors)
        if ($errors.Count) { throw $errors[0].Message }
    }
}

# --- ADO GET helper: refuses non-ADO origins before any request is sent. ---
$savedPat = [Environment]::GetEnvironmentVariable('AZURE_DEVOPS_EXT_PAT', 'Process')
try {
    [Environment]::SetEnvironmentVariable('AZURE_DEVOPS_EXT_PAT', 'offline-test-placeholder', 'Process')
    . ([scriptblock]::Create((Find-Block 'function Get-AdoJson')))
    foreach ($url in 'http://dev.azure.com/org/_apis/connectionData',
                     'https://example.invalid/org/_apis/connectionData',
                     'https://dev.azure.com:8443/org/_apis/connectionData',
                     'https://user@dev.azure.com/org/_apis/connectionData') {
        Check "GET helper refuses $url" {
            try { Get-AdoJson $url; throw 'request was not refused' }
            catch { if ($_.Exception.Message -notmatch 'Refusing to send ADO credentials') { throw } }
        }
    }
} finally {
    if ($http) { $http.Dispose() }
    [Environment]::SetEnvironmentVariable('AZURE_DEVOPS_EXT_PAT', $savedPat, 'Process')
    Remove-Variable auth -ErrorAction SilentlyContinue
}

# --- Review bot identity resolution against stubbed thread data. ---
$botBlock = Find-Block '$botIds'
Check 'review bot resolves exactly one identity' {
    function Get-AdoJson($Url) { [pscustomobject]@{ value = @(
        [pscustomobject]@{ comments = @([pscustomobject]@{ author = [pscustomobject]@{ displayName = 'Sample Review Bot'; id = '11111111-1111-1111-1111-111111111111' } }) },
        [pscustomobject]@{ comments = @([pscustomobject]@{ author = [pscustomobject]@{ displayName = 'A Person'; id = '22222222-2222-2222-2222-222222222222' } }) }) } }
    $Org = 'contoso'; $Project = 'Sample'; $RepoId = 'repo-id'; $PrId = 1; $BotName = 'Review Bot'
    . ([scriptblock]::Create($botBlock))
    if ($ReviewBot.authorId -ne '11111111-1111-1111-1111-111111111111' -or $ReviewBot.name -ne 'Review Bot' -or $ReviewBot.organization -ne 'contoso') { throw 'wrong bot' }
}
Check 'review bot ambiguity is refused' {
    function Get-AdoJson($Url) { [pscustomobject]@{ value = @(
        [pscustomobject]@{ comments = @(
            [pscustomobject]@{ author = [pscustomobject]@{ displayName = 'Review Bot A'; id = '11111111-1111-1111-1111-111111111111' } },
            [pscustomobject]@{ author = [pscustomobject]@{ displayName = 'Review Bot B'; id = '33333333-3333-3333-3333-333333333333' } }) }) } }
    $Org = 'contoso'; $Project = 'Sample'; $RepoId = 'repo-id'; $PrId = 1; $BotName = 'Review Bot'
    try { . ([scriptblock]::Create($botBlock)); throw 'ambiguity accepted' }
    catch { if ($_.Exception.Message -notmatch 'ambiguous') { throw } }
}

# --- Settings merge against a disposable folder. ---
$mergeBlock = Find-Block 'function Assert-DashboardStopped'
$needle = "Join-Path `$env:LOCALAPPDATA 'AdoPrView'"
if (([regex]::Matches($mergeBlock, [regex]::Escape($needle))).Count -ne 1) { throw 'Merge block settings path changed; update this test.' }
$mergeBlock = $mergeBlock.Replace($needle, "Join-Path `$TestLocalAppData 'AdoPrView'")
# Other dashboard instances are irrelevant to a disposable folder.
$mergeBlock = [regex]::Replace($mergeBlock, '(?s)function Assert-DashboardStopped \{.*?\n\}\n', "function Assert-DashboardStopped { }`n")
if ($mergeBlock -match '\$env:LOCALAPPDATA' -or $mergeBlock -match 'Get-CimInstance') { throw 'Merge block was not isolated; refusing to run it.' }
$merge = [scriptblock]::Create($mergeBlock)

$TestLocalAppData = Join-Path $PSScriptRoot ('.install-guide-scratch-' + [Guid]::NewGuid().ToString('N'))
$settingsPath = Join-Path $TestLocalAppData 'AdoPrView\settings.json'
$repoA = [pscustomobject]@{ org = 'contoso'; project = 'Sample'; repo = 'sample-service'; repoId = 'aaaaaaaa-0000-0000-0000-000000000001'; projectId = 'pppppppp-0000-0000-0000-000000000001' }
$repoB = [pscustomobject]@{ org = 'contoso'; project = 'Sample'; repo = 'sample-api'; repoId = 'aaaaaaaa-0000-0000-0000-000000000002'; projectId = 'pppppppp-0000-0000-0000-000000000001' }
try {
    Check 'merge creates a new settings file with only the verified repo' {
        $VerifiedRepos = @($repoA)
        & $merge
        $s = [IO.File]::ReadAllText($settingsPath) | ConvertFrom-Json
        if (@($s.repos).Count -ne 1 -or $s.repos[0].repoId -ne $repoA.repoId) { throw 'repo not written' }
        if (@($s.whitelist).Count -ne 0 -or $s.refreshSeconds -ne 120) { throw 'unexpected defaults' }
    }
    Check 'rerun is idempotent' {
        $before = [IO.File]::ReadAllText($settingsPath)
        $VerifiedRepos = @($repoA)
        & $merge
        if ([IO.File]::ReadAllText($settingsPath) -cne $before) { throw 'rerun changed settings' }
    }
    Check 'merge preserves unknown fields and personal choices' {
        $existing = '{"repos":[{"org":"contoso","project":"Sample","repo":"sample-service","repoId":"old","projectId":"old","color":"teal"}],' +
            '"whitelist":["someone@example.com"],"hidden":["old:7"],"refreshSeconds":45,"triageInstructions":"Custom ask","futureOption":{"nested":[1,2]}}'
        [IO.File]::WriteAllText($settingsPath, $existing)
        $VerifiedRepos = @($repoA, $repoB)
        & $merge
        $s = [IO.File]::ReadAllText($settingsPath) | ConvertFrom-Json
        if (@($s.repos).Count -ne 2) { throw 'repo count' }
        if ($s.repos[0].repoId -ne $repoA.repoId -or $s.repos[0].color -ne 'teal') { throw 'existing repo not updated in place' }
        if ($s.whitelist[0] -ne 'someone@example.com' -or $s.hidden[0] -ne 'old:7' -or $s.refreshSeconds -ne 45) { throw 'preferences lost' }
        if ($s.triageInstructions -ne 'Custom ask' -or @($s.futureOption.nested).Count -ne 2) { throw 'unknown/custom fields lost' }
        $backups = @(Get-ChildItem -LiteralPath (Split-Path $settingsPath) -Filter 'settings.before-agent-*.json')
        if (-not ($backups | Where-Object { [IO.File]::ReadAllText($_.FullName) -ceq $existing })) { throw 'no backup of the replaced file' }
    }
    Check 'malformed settings are refused and left unchanged' {
        [IO.File]::WriteAllText($settingsPath, '{ not json')
        $VerifiedRepos = @($repoA)
        try { & $merge; throw 'malformed file accepted' }
        catch { if ($_.Exception.Message -notmatch 'unreadable') { throw } }
        if ([IO.File]::ReadAllText($settingsPath) -cne '{ not json') { throw 'malformed file modified' }
    }
} finally {
    if (Test-Path -LiteralPath $TestLocalAppData) { Remove-Item -LiteralPath $TestLocalAppData -Recurse -Force }
}

if ($failures) { Write-Host "$failures install-guide check(s) failed"; exit 1 }
Write-Host 'install-guide checks passed'
