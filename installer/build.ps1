[CmdletBinding()]
param([string] $Version = '0.2.0')

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
& (Join-Path $repoRoot 'scripts\publish.ps1') -Runtime win-x64 -Configuration Release
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

$wix = Get-Command wix -ErrorAction SilentlyContinue
if (-not $wix) {
    throw 'WiX v4 CLI was not found. Install it with: dotnet tool install --global wix'
}

$outputDir = Join-Path $repoRoot 'artifacts\installer'
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
& $wix.Source build (Join-Path $PSScriptRoot 'VisDir.wxs') `
    -d "ProductVersion=$Version" `
    -o (Join-Path $outputDir "VisDir-$Version-win-x64.msi")
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }
