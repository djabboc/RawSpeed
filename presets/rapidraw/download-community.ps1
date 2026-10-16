[CmdletBinding()]
param([string]$DestinationPath)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($DestinationPath)) { $DestinationPath = Join-Path $PSScriptRoot 'community-local/originals' }
$catalog = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'community-sources.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$destination = [System.IO.Path]::GetFullPath($DestinationPath)
[System.IO.Directory]::CreateDirectory($destination) | Out-Null
$baseUrl = 'https://raw.githubusercontent.com/CyberTimon/RapidRAW-Presets/' + $catalog.commit + '/'
foreach ($entry in $catalog.files) {
    if ([System.IO.Path]::GetFileName($entry.file) -ne $entry.file) { throw 'Unsafe catalog filename.' }
    $target = Join-Path $destination $entry.file
    if ((Test-Path -LiteralPath $target) -and ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ieq $entry.sha256)) {
        Write-Output ('Verified: ' + $entry.file)
        continue
    }
    $relativeUrl = if ($entry.file -eq 'manifest.json') { 'manifest.json' } else { 'presets/' + [Uri]::EscapeDataString($entry.file) }
    $temporary = $target + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        Invoke-WebRequest -UseBasicParsing -Uri ($baseUrl + $relativeUrl) -OutFile $temporary -TimeoutSec 60
        if ((Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -ine $entry.sha256) { throw ('SHA256 mismatch: ' + $entry.file) }
        Move-Item -LiteralPath $temporary -Destination $target -Force
        Write-Output ('Downloaded and verified: ' + $entry.file)
    }
    finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
}
Write-Output '20 original community recipes are ready for local use. Upstream supplies no separate license; public packages exclude these files.'
