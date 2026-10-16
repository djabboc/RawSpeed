# Color processing, denoising, and measured performance

This document describes the v0.2 Core implementation and measurements on the supplied Nikon photograph. The renderer operates on decoded, display-referred **RGBA8 sRGB** pixels. RAW development, metadata and export are implemented by `RawSpeed.Imaging`; the noise filter is a conventional CPU spatial algorithm, with no trained model or sensor-domain reconstruction.

## Processing order and APIs

`ImageProcessor.Render` performs source-coordinate noise reduction, geometry, color grading, 3D LUT application, then exact RGB histograms. It owns the output buffer and leaves its input unchanged. Default settings preserve all input bytes. Source buffers must remain immutable while rendering.

Geometry applies quarter rotation, horizontal flip, free rotation, then crop. `Straighten` accepts -180 through +180 degrees. Free rotation uses automatic zoom to keep the oriented canvas dimensions without empty corners; this trims edge content. Bilinear interpolation handles fractional positions. A normalized crop rounds outward to pixel boundaries and retains at least one pixel.

`ImageGeometry.Create(width, height, settings)` exposes the renderer's transform through `MapOutputToSource`, `MapSourceToOutput`, and `MapOutputCropToCanvas`. Coordinates identify pixel centers, with the top-left pixel at `(0, 0)`. Mapping a source point outside the current crop may produce an output coordinate outside the canvas.

Color order is exposure/relative white balance, luminance tonal controls, legacy curve controls, master curve, individual RGB curves, hue/saturation, then LUT. Shadows, midtones and highlights use separate smooth luminance masks. The tonal operation offsets the three RGB channels equally before clipping, preserving neutral grays. Exposure and white-balance gains use the sRGB transfer function. Temperature/tint are relative adjustments, not Kelvin measurements.

`ToneCurve` is immutable and structurally equal, so settings comparisons and JSON recipe round trips retain value semantics. It accepts 2–64 points with normalized coordinates. First and last X values are 0 and 1; endpoint Y values can move. Adjacent X values must differ by at least 0.000001. PCHIP cubic interpolation preserves each interval's direction without overshoot, including inverted/nonmonotonic curves. `EditSettings` exposes `MasterCurve`, `RedCurve`, `GreenCurve`, and `BlueCurve`. Existing three-slider recipes remain supported.

`ColorSampling.EstimateNeutralBalance` samples an opaque, unclipped neutral patch in the decoded source. Map a preview click back through the geometry before sampling. It returns absolute values for the relative temperature/tint settings, and rejects a patch with no usable samples.

## Noise reduction and cache contract

The filter separates YCoCg luminance and color. Luminance uses two or three scales of undecimated B3-like wavelet decomposition and firm coefficient shrinkage. Edge-gated smoothing preserves strong boundaries. A robust Laplacian median estimate sets the luminance threshold. Color uses two or three scales of separable luminance/chroma-guided bilateral filtering to remove small color blotches. Detail settings of 85 or greater use two scales; lower settings use three.

`LuminanceDenoise`, `ColorDenoise`, and `DenoiseDetail` accept 0–100. Higher detail values reduce smoothing. Legacy `Denoise` is combined with each amount using `max`, preserving old recipes. Tile processing uses 256-pixel interiors, 32-pixel halos, a source snapshot, pooled worker scratch buffers, and at most 16 workers. The snapshot avoids races between neighboring tiles. Cancellation is checked between stages and rows, and pooled buffers are returned even when a render is canceled. Alpha bytes are preserved; supported JPEG/RAW inputs are opaque.

The independent noise API ignores geometry and color controls:

```csharp
byte[] denoised = NoiseReducer.Reduce(source, width, height, settings, cancellationToken);
EditSettings remaining = settings with
{
    Denoise = 0,
    LuminanceDenoise = 0,
    ColorDenoise = 0
};
RenderedImage result = ImageProcessor.Render(denoised, width, height, remaining,
    lut, lutStrength, cancellationToken);
```

This result is pixel-equivalent to a direct render with the original settings, including crop, flip, rotation, and color adjustments. An application can retain one denoised source keyed by source identity and the four noise settings (`Denoise`, `LuminanceDenoise`, `ColorDenoise`, `DenoiseDetail`). Color or geometry edits then reuse it. Preview and full-resolution sources require separate cache entries. The current standalone noise API also calculates a histogram internally; measurements below include that cost.

## Reproducible tests and CPU timings

```powershell
dotnet run --project tests/RawSpeed.Core.Tests -c Release
dotnet run --project tests/RawSpeed.Core.Tests -c Release -- --benchmark
dotnet run --project tools/DenoiseProbe -c Release
```

The first command runs all 21 original and advanced Core cases. Coverage includes geometry round trips, curve immutability/JSON/shape, luminance masks, neutral sampling, independent noise controls, correlated color noise, sharp boundaries, tile boundaries, alpha, cancellation, concurrency, source immutability, and cached/direct equivalence. Synthetic flat-field remaining variance was 0.0043 for luma and 0.0040 for chroma; correlated 4-pixel chroma blocks retained 0.0819. These fixtures exercise behavior and are not claims about photographic quality.

Measurements: Windows 10 build 19045, .NET 9.0.11, 32 logical processors, 2026-10-16. The run used optimized `CoreWork` configuration to avoid concurrent application build locks. The synthetic benchmark warms each case at least three times and for at least 300 ms, then reports the median of five renders. Allocation and histogram computation are included; decoding, file I/O, display upload and export are excluded. The data consists of deterministic random RGB bytes with opaque alpha and an identity 33³ cube where applicable.

| Operation | 1600 × 1067 | 6000 × 4000 |
| --- | ---: | ---: |
| Identity + histogram | 1.0 ms | 11.2 ms |
| Exposure, WB, contrast, saturation, legacy curve | 15.8 ms | 76.6 ms |
| Exposure, saturation, 33³ LUT | 5.5 ms | 72.9 ms |
| Master and independent RGB curves | 2.7 ms | 43.9 ms |
| Luminance denoise 60 | 20.8 ms | 227.0 ms |
| Color denoise 60 | 38.6 ms | 478.2 ms |
| Both noise amounts 60 | 54.0 ms | 647.4 ms |
| Free rotation 5° + exposure | 4.9 ms | 64.2 ms |

Color configurations differ between rows, so their subtraction does not estimate the incremental cost of an individual control. These are local observations, not a hardware-independent latency guarantee. Color lookup tables amortize expensive functions across pixels; image rows and noise tiles run in parallel.

## Supplied Nikon NEF evaluation

`DenoiseProbe` decodes `example/nikon.NEF`, evaluates noise at preview/full sizes, exports three 512 × 512 pairs of 1:1 PNG crops, creates a labeled comparison sheet and overview, and writes a JSON report. It accepts an optional repository root and source path. Each run creates a separate directory under ignored `artifacts/denoise/`; no source or previous result is overwritten. It checks the source SHA-256 after evaluation.

Run directory: `artifacts/denoise/20261016-011803-553/`.

- Source SHA-256: `F7AB652D248160C0E75F0C9AFA26A5AF275770BE1ED6A2B6DFB495BE9D65538F`.
- Developed dimensions: 4940 × 3292 (16.26 MP), LibRaw camera white balance to sRGB.
- Decode: 1434.9 ms in this run, separate from filtering timings.
- Noise settings: luminance 70, color 85, detail 50.
- Preview 1600 × 1066: 50.7 ms median of five runs after warm-up.
- Full source: 394.4 ms median of three runs.
- Both comparison sides apply +2 EV **after** noise processing, using identical color settings.

| 512-pixel region | Luma high-pass RMS before → after | Chroma high-pass RMS before → after |
| --- | ---: | ---: |
| Sky `(1284, 658)` | 5.948 → 2.107 | 4.501 → 0.778 |
| Shadow `(790, 2107)` | 10.291 → 6.136 | 8.249 → 0.871 |
| Detail `(2915, 1811)` | 9.359 → 5.300 | 7.715 → 1.133 |

The measurement subtracts the four-neighbor mean from each Y, Co, and Cg sample, then calculates RMS. Chroma combines Co/Cg squared residuals. Values are in 8-bit code units after the common display adjustment. High-frequency image texture contributes to these values; reducing them can also mean detail loss. The photograph has no clean ground truth, so these are observational proxies, not PSNR/SSIM, quality scores, or evidence of parity with commercial AI denoisers.

Visual inspection of the PNG pairs shows a substantial decrease in colored speckles and visible structural boundaries, with residual luminance speckles and some softness. The bright sky clips under the deliberately strong +2 EV display adjustment; inspect the shadow/detail pairs for dark-region behavior. Exact crop files, `comparison.png`, `context.png`, and `report.json` are kept locally rather than committed with the photograph.

## Known limits

This filter cannot reconstruct clipped/quantized RAW values, remove all hot pixels, recover lost detail, or match sensor-trained deep denoising merely by increasing its strength. Global noise estimation can mistake fine texture for noise. Preview downsampling changes noise statistics, so preview/full-resolution denoising need not look identical. The editing buffer is 8-bit display-referred RGB; higher-bit-depth export containers do not restore discarded precision. There is no GPU path, scene-linear/HDR editing, or calibrated camera-noise model in this implementation.
