[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string] $Runtime = 'win-x64',
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$publishRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts\publish'))
$publishDir = [System.IO.Path]::GetFullPath((Join-Path $publishRoot $Runtime))

if (-not $publishDir.StartsWith($publishRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to publish outside $publishRoot"
}
if (Test-Path -LiteralPath $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

$appProject = Join-Path $repoRoot 'src\VisDir.App\VisDir.App.csproj'
$scannerProject = Join-Path $repoRoot 'src\VisDir.Scanner\VisDir.Scanner.csproj'
$scannerDir = Join-Path $publishDir 'Scanner'

dotnet publish $appProject -c $Configuration -r $Runtime --self-contained true `
    -p:PublishReadyToRun=true -p:PublishSingleFile=false -p:DebugType=None `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw 'Application publish failed.' }

dotnet publish $scannerProject -c $Configuration -r $Runtime --self-contained true `
    -p:PublishReadyToRun=true -p:PublishSingleFile=false -p:DebugType=None `
    -o $scannerDir
if ($LASTEXITCODE -ne 0) { throw 'Scanner publish failed.' }

$worker = Join-Path $scannerDir 'VisDir.Scanner.exe'
if (-not (Test-Path -LiteralPath $worker)) { throw "Published worker is missing: $worker" }

$nativeSkia = Join-Path $publishDir "runtimes\$Runtime\native\libSkiaSharp.dll"
if (Test-Path -LiteralPath $nativeSkia) {
    Copy-Item -LiteralPath $nativeSkia -Destination (Join-Path $publishDir 'libSkiaSharp.dll') -Force
}

$checksums = Get-ChildItem -LiteralPath $publishDir -Recurse -File |
    Where-Object Name -ne 'checksums.sha256' |
    Sort-Object FullName |
    ForEach-Object {
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $relative = $_.FullName.Substring($publishDir.Length).TrimStart('\', '/').Replace('\', '/')
        "$hash  $relative"
    }
$checksums | Set-Content -LiteralPath (Join-Path $publishDir 'checksums.sha256') -Encoding utf8

$zipPath = Join-Path $publishRoot "VisDir-$Runtime.zip"
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
Compress-Archive -Path "$publishDir\*" -DestinationPath $zipPath -Force
Write-Host "Packaged $zipPath"

Write-Host "Published VisDir $Runtime to $publishDir"
