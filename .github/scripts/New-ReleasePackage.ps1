# Builds the self-contained Windows x64 release: a portable ZIP, a per-user
# Inno Setup installer and a SHA256 checksum file. The version always comes
# from src/AdoPrView/AdoPrView.csproj.
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\..\artifacts'),
    [string]$Iscc,
    [switch]$NoInstaller
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$project = Join-Path $root 'src\AdoPrView\AdoPrView.csproj'
$version = @(([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup | ForEach-Object { $_.SelectSingleNode('Version') } | Where-Object { $_ })[0].InnerText
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Unexpected project version '$version'." }
$name = "AdoPrView-v$version-win-x64"

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$out = (Resolve-Path $OutputDirectory).Path
$stage = Join-Path $out "stage\$name"
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
foreach ($old in "$name.zip", "$name-setup.exe", "AdoPrView-v$version-checksums.txt") {
    Remove-Item -LiteralPath (Join-Path $out $old) -ErrorAction SilentlyContinue
}

& dotnet publish $project -c Release -r win-x64 --self-contained true -o $stage `
    -p:DebugType=embedded -p:ContinuousIntegrationBuild=true --nologo
if ($LASTEXITCODE) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

foreach ($required in 'AdoPrView.exe', 'AdoPrView.dll', 'hostfxr.dll', 'coreclr.dll', 'PresentationFramework.dll',
        'Microsoft.Web.WebView2.Core.dll', 'WebView2Loader.dll', 'wwwroot\index.html',
        'DiffPlex.dll', 'GitHub.Copilot.SDK.dll', 'runtimes\win-x64\native\copilot-runtime.exe',
        'runtimes\win-x64\native\runtime.node', 'runtimes\win-x64\native\copilot_runtime.dll',
        'runtimes\win-x64\native\OneAuthInterop.LICENSE.txt') {
    if (-not (Test-Path -LiteralPath (Join-Path $stage $required) -PathType Leaf)) { throw "Publish output is missing $required." }
}
$runtimeConfig = Get-Content -LiteralPath (Join-Path $stage 'AdoPrView.runtimeconfig.json') -Raw | ConvertFrom-Json
if (-not $runtimeConfig.runtimeOptions.PSObject.Properties['includedFrameworks']) { throw 'Publish output is not self-contained.' }

# Copy package notices and the upstream license texts retained for packages
# that declare a license expression without including its text.
$deps = Get-Content -LiteralPath (Join-Path $stage 'AdoPrView.deps.json') -Raw | ConvertFrom-Json
$libraries = @($deps.libraries.PSObject.Properties.Name)
$packageRoots = @($env:NUGET_PACKAGES, (Join-Path $env:USERPROFILE '.nuget\packages')) | Where-Object { $_ }
$dotnetRoot = Split-Path -Parent (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source
$licenses = Join-Path $stage 'licenses'
$components = @(
    @{ Id = 'Microsoft.NETCore.App.Runtime.win-x64'; Library = 'runtimepack.Microsoft.NETCore.App.Runtime.win-x64'; Packs = $true },
    @{ Id = 'Microsoft.WindowsDesktop.App.Runtime.win-x64'; Library = 'runtimepack.Microsoft.WindowsDesktop.App.Runtime.win-x64'; Packs = $true },
    @{ Id = 'Microsoft.Web.WebView2'; Library = 'Microsoft.Web.WebView2'; Packs = $false },
    @{ Id = 'DiffPlex'; Library = 'DiffPlex'; Packs = $false; License = 'licenses\DiffPlex\LICENSE.txt' },
    @{ Id = 'GitHub.Copilot.SDK'; Library = 'GitHub.Copilot.SDK'; Packs = $false; License = 'licenses\GitHub.Copilot.SDK\LICENSE.txt' },
    @{ Id = 'Microsoft.Extensions.AI.Abstractions'; Library = 'Microsoft.Extensions.AI.Abstractions'; Packs = $false; License = 'licenses\Microsoft.Extensions\LICENSE.txt' },
    @{ Id = 'Microsoft.Extensions.DependencyInjection.Abstractions'; Library = 'Microsoft.Extensions.DependencyInjection.Abstractions'; Packs = $false; License = 'licenses\Microsoft.Extensions\LICENSE.txt' },
    @{ Id = 'Microsoft.Extensions.Logging.Abstractions'; Library = 'Microsoft.Extensions.Logging.Abstractions'; Packs = $false; License = 'licenses\Microsoft.Extensions\LICENSE.txt' }
)
$index = [Collections.Generic.List[string]]::new()
foreach ($component in $components) {
    $library = @($libraries | Where-Object { $_ -like "$($component.Library)/*" })
    if ($library.Count -ne 1) { throw "Cannot identify the resolved $($component.Id) version." }
    $componentVersion = $library[0].Split('/')[1]
    $candidates = @($packageRoots | ForEach-Object { Join-Path $_ "$($component.Id.ToLowerInvariant())\$componentVersion" })
    if ($component.Packs) { $candidates += Join-Path $dotnetRoot "packs\$($component.Id)\$componentVersion" }
    $source = $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Container } | Select-Object -First 1
    if (-not $source) { throw "Cannot find the restored $($component.Id) $componentVersion package." }
    $texts = @(Get-ChildItem -LiteralPath $source -File | Where-Object { $_.Name -match '^(LICENSE|NOTICE|THIRD-PARTY-NOTICES)(\.txt)?$' })
    if (-not ($texts | Where-Object { $_.Name -match '^LICENSE' }) -and $component.ContainsKey('License')) {
        $texts += Get-Item -LiteralPath (Join-Path $root $component.License)
    }
    if (-not ($texts | Where-Object { $_.Name -match '^LICENSE' })) { throw "No license text found for $($component.Id) $componentVersion." }
    $target = Join-Path $licenses $component.Id
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    foreach ($text in $texts) { Copy-Item -LiteralPath $text.FullName -Destination (Join-Path $target $text.Name) }
    $index.Add("- $($component.Id) $componentVersion`: licenses/$($component.Id)/ ($((@($texts.Name) | Sort-Object) -join ', '))")
    if ($component.Id -eq 'GitHub.Copilot.SDK') {
        $sdkProps = [xml](Get-Content -LiteralPath (Join-Path $source 'build\GitHub.Copilot.SDK.props') -Raw)
        $cliVersion = $sdkProps.Project.PropertyGroup.CopilotCliVersion
        $cliLicenses = Join-Path $licenses 'GitHub.Copilot.CLI'
        New-Item -ItemType Directory -Force -Path $cliLicenses | Out-Null
        Copy-Item -LiteralPath (Join-Path $root 'licenses\GitHub.Copilot.CLI\LICENSE.txt') -Destination $cliLicenses
        $index.Add("- GitHub Copilot CLI $cliVersion`: licenses/GitHub.Copilot.CLI/LICENSE.txt; bundled native notices remain beside their binaries.")
    }
}
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $stage 'LICENSE')
$notice = @"
ADO PR View $version (self-contained Windows x64)

ADO PR View is licensed under the MIT License; see LICENSE.

This package redistributes the following components under their own licenses:
$($index -join "`n")
- marked and DOMPurify: wwwroot/vendor/marked.min.js and purify.min.js keep
  their original license headers.

The Microsoft Edge WebView2 Runtime is not included; Windows provides it as
the separately installed WebView2 Evergreen Runtime.
"@
[IO.File]::WriteAllText((Join-Path $stage 'THIRD-PARTY-NOTICES.txt'), ($notice -replace "`r`n", "`n" -replace "`n", "`r`n") + "`r`n")

$zip = Join-Path $out "$name.zip"
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
$assets = @($zip)

if (-not $NoInstaller) {
    if (-not $Iscc) {
        $Iscc = @((Get-Command ISCC.exe -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Source),
            (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
            (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
    }
    if (-not $Iscc) { throw 'Inno Setup 6 (ISCC.exe) was not found; pass -Iscc or -NoInstaller.' }
    & $Iscc /Qp "/DAppVersion=$version" "/DStageDir=$stage" "/DOutputDir=$out" "/DOutputName=$name-setup" (Join-Path $root 'installer\AdoPrView.iss')
    if ($LASTEXITCODE) { throw "Inno Setup failed with exit code $LASTEXITCODE." }
    $assets += Join-Path $out "$name-setup.exe"
}

$sums = foreach ($asset in $assets) { '{0}  {1}' -f (Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash.ToLowerInvariant(), (Split-Path -Leaf $asset) }
[IO.File]::WriteAllText((Join-Path $out "AdoPrView-v$version-checksums.txt"), ($sums -join "`n") + "`n")
Write-Output "Version $version; $(@(Get-ChildItem -LiteralPath $stage -Recurse -File).Count) files staged."
$sums
if ($env:GITHUB_OUTPUT) {
    Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value "version=$version", "name=$name"
}
