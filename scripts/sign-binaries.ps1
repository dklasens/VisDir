[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $LiteralPath
)

# Authenticode-signs release binaries with the CI code-signing certificate.
# No-op (by design) unless the environment provides the certificate:
#   SIGNING_PFX_B64       base64-encoded PFX containing the publisher key
#   SIGNING_PFX_PASSWORD  PFX password; may be empty for a passwordless key
# Without a certificate the build stays unsigned and the updater's
# Authenticode gate refuses it — that failure is intentional, not silent.
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrEmpty($env:SIGNING_PFX_B64)) {
    Write-Host 'SIGNING_PFX_B64 not set; skipping Authenticode signing.'
    return
}

$pfxPath = Join-Path ([System.IO.Path]::GetTempPath()) ("visdir-codesign-" + [Guid]::NewGuid().ToString('N') + '.pfx')
[System.IO.File]::WriteAllBytes($pfxPath, [Convert]::FromBase64String($env:SIGNING_PFX_B64))
try {
    $kitsBin = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Directory -ErrorAction Stop |
        Sort-Object Name -Descending | Select-Object -First 1
    $signtool = Join-Path $kitsBin.FullName 'x64\signtool.exe'
    if (-not (Test-Path -LiteralPath $signtool)) { throw "signtool not found under $($kitsBin.FullName)" }

    $targets = Get-ChildItem -LiteralPath $LiteralPath -Recurse -File -ErrorAction Stop |
        Where-Object { $_.Extension -eq '.exe' -or $_.Extension -eq '.dll' -or $_.Extension -eq '.msi' }
    foreach ($f in $targets) {
        $signArgs = @('sign', '/tr', 'http://timestamp.digicert.com', '/td', 'sha256', '/fd', 'sha256',
            '/f', $pfxPath)
        if (-not [string]::IsNullOrEmpty($env:SIGNING_PFX_PASSWORD)) {
            $signArgs += '/p'
            $signArgs += $env:SIGNING_PFX_PASSWORD
        }
        $signArgs += $f.FullName
        & $signtool @signArgs
        if ($LASTEXITCODE -ne 0) { throw "signtool failed for $($f.FullName)" }
    }
    Write-Host "Authenticode-signed $($targets.Count) binaries under $LiteralPath"
}
finally {
    Remove-Item -LiteralPath $pfxPath -Force -ErrorAction SilentlyContinue
}
