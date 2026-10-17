param([switch]$WithSamples, [switch]$Benchmark)
$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '..')
try {
    & (Join-Path $PSScriptRoot 'Test-Publish.ps1')
    dotnet build RawSpeed.sln -c Release -warnaserror
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    $arguments = @('run', '--project', 'tests/RawSpeed.Core.Tests', '-c', 'Release', '--no-build')
    if ($Benchmark) { $arguments += @('--', '--benchmark') }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
    dotnet run --project tests/RawSpeed.App.Tests -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'App tests failed.' }
    dotnet run --project tests/RawSpeed.CatalogTests -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Filter catalog tests failed.' }
    dotnet run --project tests/RawSpeed.Export.Tests -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Export and preferences tests failed.' }
    dotnet run --project tools/RawSpeed.UiProbe -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'WPF component checks failed.' }
    if ($WithSamples) {
        dotnet run --project tests/RawSpeed.IntegrationTests -c Release --no-build
        if ($LASTEXITCODE -ne 0) { throw 'Sample integration tests failed.' }
    }
} finally { Pop-Location }
