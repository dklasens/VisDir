[CmdletBinding()]
param(
    [string] $Version,
    [string] $WixPath,
    [switch] $NoRestore,
    [switch] $SkipPublish
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml] $buildProps = Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Build.props')
    $Version = [string]$buildProps.Project.PropertyGroup.VersionPrefix
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Invalid installer version: $Version" }
if (-not $SkipPublish) {
    if ($NoRestore) {
        & (Join-Path $repoRoot 'scripts\publish.ps1') -Runtime win-x64 -Configuration Release -NoRestore
    } else {
        & (Join-Path $repoRoot 'scripts\publish.ps1') -Runtime win-x64 -Configuration Release
    }
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
}

$wixCommand = if ([string]::IsNullOrWhiteSpace($WixPath)) {
    (Get-Command wix -ErrorAction SilentlyContinue).Source
} else {
    (Resolve-Path -LiteralPath $WixPath).Path
}
if ([string]::IsNullOrWhiteSpace($wixCommand)) {
    throw 'WiX v6 CLI was not found. Install it with: dotnet tool install --global wix --version 6.*'
}

$outputDir = Join-Path $repoRoot 'artifacts\installer'
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
& $wixCommand build (Join-Path $PSScriptRoot 'VisDir.wxs') `
    -d "ProductVersion=$Version" `
    -d "RepoRoot=$repoRoot" `
    -o (Join-Path $outputDir "VisDir-$Version-win-x64.msi")
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }
