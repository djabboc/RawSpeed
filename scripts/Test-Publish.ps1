$ErrorActionPreference = 'Stop'
$artifactRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts'))
$fixture = Join-Path $artifactRoot ('publish-tests-' + [guid]::NewGuid().ToString('N'))
function Assert-Publish($condition, [string]$message) { if (-not $condition) { throw $message } }
# Exercise the real packaging script with a generated repository and compiler output.
# No application package is built or distributed by these tests.
function dotnet {
    $output = $args[[Array]::IndexOf($args, '-o') + 1]
    [System.IO.Directory]::CreateDirectory($output) | Out-Null
    Set-Content -LiteralPath (Join-Path $output 'RawSpeed.dll') -Value 'new build'
    $global:LASTEXITCODE = if (Test-Path -LiteralPath '.fail-build') { 1 } else { 0 }
}
try {
    foreach ($directory in @('scripts', 'docs', 'third-party-notices', 'presets/rapidraw/community-local')) {
        [System.IO.Directory]::CreateDirectory((Join-Path $fixture $directory)) | Out-Null
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Publish.ps1') -Destination (Join-Path $fixture 'scripts/Publish.ps1')
    foreach ($file in @('README.md', 'THIRD-PARTY-NOTICES.md', 'CONTRIBUTING.md', 'docs/test.md', 'third-party-notices/test.txt', 'presets/rapidraw/community-local/local.rrpreset')) {
        Set-Content -LiteralPath (Join-Path $fixture $file) -Value 'fixture'
    }
    $publisher = Join-Path $fixture 'scripts/Publish.ps1'
    $output = Join-Path $fixture 'artifacts/publish/win-x64'
    & $publisher -IncludeLocalPresets
    Assert-Publish (Test-Path -LiteralPath (Join-Path $output 'presets/rapidraw/community-local/local.rrpreset')) 'Opt-in publish did not copy the local preset.'
    Set-Content -LiteralPath (Join-Path $output 'obsolete.dll') -Value 'old build'
    & $publisher
    Assert-Publish (-not (Test-Path -LiteralPath (Join-Path $output 'presets/rapidraw/community-local'))) 'Default publish retained local presets.'
    Assert-Publish (-not (Test-Path -LiteralPath (Join-Path $output 'obsolete.dll'))) 'Default publish retained an obsolete binary.'
    Assert-Publish (Test-Path -LiteralPath (Join-Path $output 'RawSpeed.dll')) 'Default publish is missing its generated binary.'
    $backup = @(Get-ChildItem -LiteralPath (Split-Path $output) -Directory -Filter 'win-x64.previous-*')
    Assert-Publish ($backup.Count -eq 1) 'Previous output was not retained.'
    Assert-Publish (Test-Path -LiteralPath (Join-Path $backup[0].FullName 'obsolete.dll')) 'Backup did not preserve prior files.'
    Write-Output 'PASS Reused output excludes local presets and stale binaries while preserving the prior directory.'

    Set-Content -LiteralPath (Join-Path $output 'RawSpeed.dll') -Value 'preserve on failure'
    Set-Content -LiteralPath (Join-Path $fixture '.fail-build') -Value 'fail'
    $failed = $false
    try { & $publisher } catch { $failed = $true }
    Assert-Publish $failed 'A failed compiler was accepted.'
    Assert-Publish ((Get-Content -LiteralPath (Join-Path $output 'RawSpeed.dll') -Raw).Trim() -eq 'preserve on failure') 'A failed publish changed the existing output.'
    Assert-Publish (@(Get-ChildItem -LiteralPath (Split-Path $output) -Directory -Force -Filter '*.staging-*').Count -eq 0) 'Failed staging output was not cleaned.'
    Write-Output 'PASS Failed publish preserves the previous output and removes its staging directory.'

    foreach ($invalid in @('.', 'artifacts/publish', '../outside')) {
        $failed = $false
        try { & $publisher -OutputDirectory $invalid } catch { $failed = $true }
        Assert-Publish $failed "Unsafe output was accepted: $invalid"
    }
    Write-Output 'PASS Publish refuses paths outside a child of artifacts/publish.'
    $global:LASTEXITCODE = 0
} finally {
    $resolved = [System.IO.Path]::GetFullPath($fixture)
    if (-not $resolved.StartsWith($artifactRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test cleanup path.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
