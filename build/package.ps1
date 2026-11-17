<#
.SYNOPSIS
Builds the files for a PaperKnife for Windows release.

.DESCRIPTION
Publishes the app as a self-contained build (no .NET or Windows App SDK install needed) for
x64 and Arm64, then writes to the output folder:
  - PaperKnife-<version>-setup.exe         one installer for every 64-bit Windows PC
  - PaperKnife-<version>-<arch>-portable.zip  unzip-and-run copies
  - SHA256SUMS.txt
Inno Setup is downloaded through NuGet (build\tools.proj), so nothing is installed system-wide.

.EXAMPLE
pwsh build/package.ps1 -Version 0.1.0
#>
param(
    [string]$Version = '0.1.0',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\release')
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$Version = $Version.TrimStart('v')
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') {
    throw "Version '$Version' isn't of the form 1.2.3 or 1.2.3-label."
}

function Invoke-Checked([string]$File, [string[]]$Arguments) {
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$File failed with exit code $LASTEXITCODE."
    }
}

Invoke-Checked dotnet @('restore', (Join-Path $PSScriptRoot 'tools.proj'), '--verbosity', 'quiet')
$packages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $HOME '.nuget\packages' }
$iscc = Get-ChildItem (Join-Path $packages 'tools.innosetup\6.7.3') -Recurse -Filter ISCC.exe | Select-Object -First 1
if (-not $iscc) {
    throw 'ISCC.exe was not found in the Tools.InnoSetup package.'
}

New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$OutputDirectory = (Resolve-Path $OutputDirectory).Path
Get-ChildItem $OutputDirectory -File | Where-Object { $_.Name -like 'PaperKnife-*' -or $_.Name -eq 'SHA256SUMS.txt' } | Remove-Item

$published = @{}
foreach ($arch in 'x64', 'arm64') {
    $publish = Join-Path $root "artifacts\publish\win-$arch"
    if (Test-Path $publish) {
        Remove-Item $publish -Recurse -Force
    }

    Invoke-Checked dotnet @(
        'publish', (Join-Path $root 'src\PaperKnife.App\PaperKnife.App.csproj'),
        '-c', 'Release', '-r', "win-$arch", '--self-contained', 'true',
        "-p:Version=$Version", '-p:DebugType=None', '-p:DebugSymbols=false',
        '-o', $publish)

    # Without PaperKnife.pri (resources and compiled XAML) the app crashes at startup.
    foreach ($required in 'PaperKnife.exe', 'PaperKnife.pri', 'pdfium.dll', 'licenses\THIRD_PARTY_NOTICES.md') {
        if (-not (Test-Path (Join-Path $publish $required))) {
            throw "The win-$arch build is missing $required."
        }
    }

    Compress-Archive -Path (Join-Path $publish '*') -DestinationPath (Join-Path $OutputDirectory "PaperKnife-$Version-$arch-portable.zip") -CompressionLevel Optimal
    $published[$arch] = $publish
}

Invoke-Checked $iscc.FullName @(
    '/Q', "/DAppVersion=$Version", "/DFileVersion=$(($Version -split '-')[0])",
    "/DX64Dir=$($published['x64'])", "/DArm64Dir=$($published['arm64'])",
    "/O$OutputDirectory", (Join-Path $root 'installer\PaperKnife.iss'))

$sums = Get-ChildItem $OutputDirectory -File -Filter 'PaperKnife-*' | Sort-Object Name | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
}
Set-Content (Join-Path $OutputDirectory 'SHA256SUMS.txt') $sums -Encoding ascii

Get-ChildItem $OutputDirectory -File | Where-Object { $_.Name -like 'PaperKnife-*' -or $_.Name -eq 'SHA256SUMS.txt' } |
    ForEach-Object { '{0,-40} {1,8:N1} MB' -f $_.Name, ($_.Length / 1MB) }
