param([switch]$SelfContained, [string]$OutputDirectory = 'artifacts/publish/win-x64', [switch]$IncludeLocalPresets)
$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '..')
try {
    $output = [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $OutputDirectory))
    $standalone = $SelfContained.IsPresent.ToString().ToLowerInvariant()
    dotnet publish src/RawSpeed.App/RawSpeed.App.csproj -c Release -r win-x64 --self-contained $standalone -o $output
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    Copy-Item -LiteralPath README.md, THIRD-PARTY-NOTICES.md, CONTRIBUTING.md -Destination $output
    Copy-Item -LiteralPath third-party-notices -Destination $output -Recurse -Force
    Copy-Item -LiteralPath docs -Destination $output -Recurse -Force
    if ($IncludeLocalPresets) {
        $localPresets = Join-Path (Get-Location) 'presets/rapidraw/community-local'
        if (Test-Path -LiteralPath $localPresets) {
            Copy-Item -LiteralPath $localPresets -Destination (Join-Path $output 'presets/rapidraw') -Recurse -Force
        }
    }
    Write-Output "Published to $output"
} finally { Pop-Location }
