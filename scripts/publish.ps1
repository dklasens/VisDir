[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string] $Runtime = 'win-x64',
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [switch] $NoRestore
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
$publishArguments = @(
    'publish', $appProject, '-c', $Configuration, '-r', $Runtime, '--self-contained', 'true',
    '-p:PublishReadyToRun=true', '-p:PublishSingleFile=false', '-p:DebugType=None',
    '-o', $publishDir
)
if ($NoRestore) { $publishArguments += '--no-restore' }
dotnet @publishArguments
if ($LASTEXITCODE -ne 0) { throw 'Application publish failed.' }

$workerAssembly = Join-Path $publishDir 'VisDir.Scanner.dll'
if (-not (Test-Path -LiteralPath $workerAssembly)) { throw "Published worker assembly is missing: $workerAssembly" }

$nativeSkia = Join-Path $publishDir "runtimes\$Runtime\native\libSkiaSharp.dll"
if (Test-Path -LiteralPath $nativeSkia) {
    Copy-Item -LiteralPath $nativeSkia -Destination (Join-Path $publishDir 'libSkiaSharp.dll') -Force
}

$checksumName = "checksums-$Runtime.sha256"
$checksums = Get-ChildItem -LiteralPath $publishDir -Recurse -File |
    Where-Object Name -ne $checksumName |
    Sort-Object FullName |
    ForEach-Object {
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $relative = $_.FullName.Substring($publishDir.Length).TrimStart('\', '/').Replace('\', '/')
        "$hash  $relative"
    }
$checksums | Set-Content -LiteralPath (Join-Path $publishDir $checksumName) -Encoding utf8

foreach ($line in Get-Content -LiteralPath (Join-Path $publishDir $checksumName)) {
    $parts = $line -split '  ', 2
    $filePath = Join-Path $publishDir $parts[1].Replace('/', '\')
    $actual = (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $parts[0]) { throw "Checksum verification failed for $($parts[1])" }
}

$zipPath = Join-Path $publishRoot "VisDir-$Runtime.zip"
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
Compress-Archive -Path "$publishDir\*" -DestinationPath $zipPath -Force
Write-Host "Packaged $zipPath"

Write-Host "Published VisDir $Runtime to $publishDir"
