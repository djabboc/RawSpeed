param([switch]$SelfContained, [string]$OutputDirectory = 'artifacts/publish/win-x64', [switch]$IncludeLocalPresets)
$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '..')
$staging = $null
try {
    # Keep directory replacement inside this repository's generated publish area.
    $workspaceRoot = [System.IO.Path]::GetFullPath((Get-Location).Path)
    $publishRoot = [System.IO.Path]::GetFullPath((Join-Path $workspaceRoot 'artifacts/publish'))
    $output = [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $OutputDirectory)).TrimEnd('\', '/')
    if (-not $output.StartsWith($publishRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'OutputDirectory must be a child directory of artifacts/publish.'
    }
    $ancestor = $output
    while ($ancestor -and $ancestor.Length -ge $workspaceRoot.Length) {
        if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
            throw 'Publish directories must not contain symbolic links or junctions.'
        }
        $ancestor = [System.IO.Path]::GetDirectoryName($ancestor)
    }
    if ((Test-Path -LiteralPath $output) -and -not (Test-Path -LiteralPath $output -PathType Container)) {
        throw 'OutputDirectory must be a directory.'
    }
    $parent = [System.IO.Path]::GetDirectoryName($output)
    [System.IO.Directory]::CreateDirectory($parent) | Out-Null
    $staging = Join-Path $parent ('.' + [System.IO.Path]::GetFileName($output) + '.staging-' + [guid]::NewGuid().ToString('N'))
    $standalone = $SelfContained.IsPresent.ToString().ToLowerInvariant()
    dotnet publish src/RawSpeed.App/RawSpeed.App.csproj -c Release -r win-x64 --self-contained $standalone -o $staging
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed; the previous output has been preserved.' }
    Copy-Item -LiteralPath README.md, THIRD-PARTY-NOTICES.md, CONTRIBUTING.md -Destination $staging
    Copy-Item -LiteralPath third-party-notices -Destination $staging -Recurse -Force
    Copy-Item -LiteralPath docs -Destination $staging -Recurse -Force
    if ($IncludeLocalPresets) {
        $localPresets = Join-Path (Get-Location) 'presets/rapidraw/community-local'
        if (Test-Path -LiteralPath $localPresets) {
            $presetOutput = Join-Path $staging 'presets/rapidraw'
            [System.IO.Directory]::CreateDirectory($presetOutput) | Out-Null
            Copy-Item -LiteralPath $localPresets -Destination $presetOutput -Recurse -Force
        }
    } elseif (Test-Path -LiteralPath (Join-Path $staging 'presets/rapidraw/community-local')) {
        throw 'Default publish unexpectedly contains local community presets.'
    }
    # Replace from a fresh directory; keep old output outside the directory being delivered.
    $previous = $null
    if (Test-Path -LiteralPath $output) {
        $previous = $output + '.previous-' + [guid]::NewGuid().ToString('N')
        [System.IO.Directory]::Move($output, $previous)
    }
    try { [System.IO.Directory]::Move($staging, $output); $staging = $null }
    catch {
        if ($previous -and -not (Test-Path -LiteralPath $output)) { [System.IO.Directory]::Move($previous, $output) }
        throw
    }
    Write-Output "Published to $output"
    if ($previous) { Write-Output "Previous output retained at $previous" }
} finally {
    # Only this invocation's unique staging path is removed, never an existing output.
    try {
        if ($staging -and (Test-Path -LiteralPath $staging)) {
            $resolved = [System.IO.Path]::GetFullPath($staging)
            if (-not $resolved.StartsWith($publishRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe staging cleanup path.' }
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    } finally { Pop-Location }
}
