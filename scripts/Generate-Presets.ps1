param([string]$OutputDirectory = (Join-Path $PSScriptRoot '../presets'))
$ErrorActionPreference = 'Stop'
[IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($OutputDirectory)) | Out-Null
$culture = [Globalization.CultureInfo]::InvariantCulture
foreach ($name in @('Warm Film', 'Cool Evening')) {
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add('# Original RawSpeed creative sRGB LUT. Red channel varies fastest.')
    $lines.Add('TITLE "' + $name + '"')
    $lines.Add('LUT_3D_SIZE 17')
    $lines.Add('DOMAIN_MIN 0.0 0.0 0.0')
    $lines.Add('DOMAIN_MAX 1.0 1.0 1.0')
    for ($b = 0; $b -le 16; $b++) {
        for ($g = 0; $g -le 16; $g++) {
            for ($r = 0; $r -le 16; $r++) {
                $red = $r / 16.0; $green = $g / 16.0; $blue = $b / 16.0
                if ($name -eq 'Warm Film') {
                    $red = 0.035 + 0.97 * [Math]::Pow($red, 0.96)
                    $green = 0.02 + 0.95 * $green
                    $blue = 0.025 + 0.89 * [Math]::Pow($blue, 1.04)
                } else {
                    $red = 0.01 + 0.91 * [Math]::Pow($red, 1.03)
                    $green = 0.025 + 0.95 * $green
                    $blue = 0.045 + 0.96 * [Math]::Pow($blue, 0.96)
                }
                $values = @($red, $green, $blue) | ForEach-Object { [Math]::Max(0.0, [Math]::Min(1.0, $_)).ToString('F6', $culture) }
                $lines.Add(($values -join ' '))
            }
        }
    }
    [IO.File]::WriteAllLines((Join-Path $OutputDirectory ($name.Replace(' ', '-') + '.cube')), $lines, [Text.UTF8Encoding]::new($false))
}
