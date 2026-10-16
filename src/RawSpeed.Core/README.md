# RawSpeed.Core

A dependency-free, thread-safe .NET 9 renderer for tightly packed **RGBA8 sRGB** images. It receives decoded pixels; RAW development, metadata, ICC conversion and file export belong to the imaging/application layers.

```csharp
var settings = new EditSettings
{
    Exposure = 0.5,
    Temperature = 12,
    LuminanceDenoise = 40,
    ColorDenoise = 60,
    DenoiseDetail = 50,
    MasterCurve = new ToneCurve([new(0, 0), new(0.3, 0.25), new(0.7, 0.75), new(1, 1)])
};
RenderedImage output = ImageProcessor.Render(rgba, width, height, settings,
    lut: CubeLut.Load("look.cube"), lutStrength: 0.7,
    cancellationToken: cancellationToken);
```

`Render` owns its returned pixel buffer and never modifies the input. Default settings preserve every RGBA byte exactly. Do not modify the source while a render is running. Settings, `ToneCurve`, and `CubeLut` are immutable and can be shared between jobs. The three histogram channels occupy consecutive 256-bin ranges in `RenderedImage.Histogram`.

## Processing contract

1. Reduce luminance/color noise in the original decoded source coordinates.
2. Rotate clockwise in 90-degree steps, then flip horizontally.
3. Apply free rotation (`Straighten`) from -180 to +180 degrees. Automatic zoom retains the oriented canvas dimensions and avoids empty corners, intentionally trimming edges. Fractional coordinates use bilinear interpolation.
4. Apply the normalized crop in the oriented, straightened canvas. The bounding pixel rectangle rounds outward; very small valid crops retain at least one pixel.
5. Apply exposure and relative white balance through the sRGB transfer function. Apply luminance-targeted shadows, midtones, highlights, and contrast. Tonal changes offset RGB channels equally before clipping, preserving neutral grays.
6. Apply legacy curve controls, master curve, then independent red/green/blue curves.
7. Apply hue/saturation, then a 3D LUT with adjustable mix.
8. Build exact RGB histograms from the output.

Exposure accepts -10 to +10 stops, hue -360 to +360 degrees, noise amounts/detail 0–100, LUT strength 0–1, and other color/legacy curve controls -100 to +100. Temperature/tint are relative controls, not absolute Kelvin values. A crop must have positive dimensions and fit within the normalized image. Invalid or non-finite settings throw. `ImageProcessor.ValidateSettings(settings)` applies the same validation without allocating an image; persistence layers should call it before saving and after loading recipes.

## Curves, geometry, and neutral sampling

`ToneCurve` accepts 2–64 normalized `CurvePoint` values. The first/last X coordinates are 0/1; their Y values may move. Adjacent X coordinates must increase by at least 0.000001. Shape-preserving PCHIP cubic interpolation supports both increasing and decreasing intervals without overshoot. Points are copied at construction, the public list is read-only, equality is structural, and JSON serialization preserves value semantics. `EditSettings` exposes `MasterCurve`, `RedCurve`, `GreenCurve`, and `BlueCurve`.

Legacy `CurveShadows`, `CurveMidtones`, and `CurveHighlights` retain their five-knot Hermite transfer function for older recipes. `ImageProcessor.EvaluateCurve(input, settings)` evaluates that function followed by the master curve. The overload taking `ToneCurve?` evaluates just the supplied curve; null means identity. Other color adjustments are excluded from these plotting APIs.

`ImageGeometry.Create(width, height, settings)` returns the exact renderer transform. Use `MapOutputToSource` for a preview pick, `MapSourceToOutput` for overlays, and `MapOutputCropToCanvas` to compose a selection with the current crop. Coordinates describe pixel centers; the top-left pixel is `(0, 0)`. A point outside the crop may map outside the output.

`ColorSampling.EstimateNeutralBalance(rgba, width, height, x, y, radius)` samples the original decoded source and returns absolute values for the relative temperature/tint controls. Map the preview click through `ImageGeometry` first. Transparent, clipped, or nearly black samples are skipped; a patch with no usable samples is rejected. The selected patch must represent a neutral surface.

## Noise reduction and reuse

YCoCg separates luminance from color. Luminance uses undecimated multiscale B3-like wavelets, firm shrinkage, edge-gated smoothing, and a robust Laplacian median noise estimate. Color uses multiscale separable luminance/chroma-guided bilateral filtering. Detail values below 85 use three scales; higher values use two and lower smoothing thresholds. Legacy `Denoise` combines with each new amount through `max`.

Processing uses 256-pixel tiles with 32-pixel halos, a source snapshot to avoid races, pooled worker scratch buffers, and at most 16 workers. Cancellation is checked between stages/rows; pools are returned on failure or cancellation. Alpha is preserved. JPEG/RAW inputs are opaque; transparent pixel RGB values are not excluded from neighboring filter samples.

An application can cache one denoised source and reuse it while changing geometry/color:

```csharp
byte[] denoised = NoiseReducer.Reduce(rgba, width, height, settings, cancellationToken);
var remaining = settings with { Denoise = 0, LuminanceDenoise = 0, ColorDenoise = 0 };
RenderedImage result = ImageProcessor.Render(denoised, width, height, remaining,
    lut, lutStrength, cancellationToken);
```

This is pixel-equivalent to direct rendering. The cache key must include source identity and all four noise controls. Preview/full-resolution buffers require separate entries. The settings overload ignores geometry/color controls; the numeric overload accepts luminance, color, detail and cancellation directly. At present the standalone noise API also builds an internal histogram.

## LUT support

`CubeLut.Load` and `CubeLut.Parse` support 3D `.cube` files with `TITLE`, `LUT_3D_SIZE`, optional `DOMAIN_MIN`/`DOMAIN_MAX`, comments, and red-fastest RGB samples. Grid sizes of 2–128 are supported. Sampling uses trilinear interpolation and clamps inputs outside the domain. Output is clamped when converted to bytes.

The parser rejects missing/extra samples, duplicate domain/size headers, invalid domains, non-finite numbers, unsupported 1D/shaper files, and unreasonable file/line/grid sizes. Numeric parsing is culture-independent.

## Verification and limitations

```powershell
dotnet run --project tests/RawSpeed.Core.Tests -c Release
dotnet run --project tests/RawSpeed.Core.Tests -c Release -- --benchmark
dotnet run --project tools/DenoiseProbe -c Release
```

The 21 Core cases cover rendering, curves, color masks, geometry, sampling, denoising, alpha, cancellation, concurrent use, LUT validation, and cached/direct equivalence. [Measured performance and real Nikon NEF evaluation](../../docs/denoise-and-performance.md) describe methodology, sample artifacts, and limits. Benchmarks include allocation/histograms and exclude decode, I/O, display upload and export.

The pipeline is display-referred 8-bit RGB, with no GPU, scene-linear/HDR processing, calibrated sensor-noise model, or AI inference. It cannot restore clipped/quantized information. Noise reduction can soften detail, and global noise estimation can mistake texture for noise. Preview downsampling changes noise statistics, so preview/full results can differ. This implementation does not claim parity with commercial deep-learning denoisers.
