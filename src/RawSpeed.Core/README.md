# RawSpeed.Core

A dependency-free, thread-safe .NET 9 renderer for tightly packed **RGBA8** images. It receives decoded pixels; RAW decoding, metadata, color profile conversion and file export belong to the imaging/application layers.

```csharp
RenderedImage output = ImageProcessor.Render(
    rgba, width, height,
    new EditSettings { Exposure = 0.5, Temperature = 12, Denoise = 30 },
    lut: CubeLut.Load("look.cube"),
    lutStrength: 0.7,
    cancellationToken: cancellationToken);
```

`Render` always owns its returned pixel buffer and never modifies the caller's input. An all-default edit preserves every RGBA byte exactly. Do not modify the input while a render is running. `CubeLut` and settings are immutable and can be shared between render jobs. The three histogram channels occupy consecutive 256-bin ranges in `RenderedImage.Histogram`.

## Processing contract

1. Rotate clockwise in 90-degree steps, then flip horizontally.
2. Straighten by −45…45 degrees. Automatic zoom keeps the oriented canvas dimensions and avoids empty corners. This intentionally trims some edge content. Bilinear interpolation handles fractional coordinates.
3. Apply the normalized crop in the oriented, straightened canvas. Its bounding pixel rectangle rounds outward; very small valid rectangles retain at least one pixel.
4. Apply exposure, relative temperature/tint, tonal controls and the three-point curve. Exposure and white-balance gains are applied through the sRGB transfer function. Channel tables amortize expensive arithmetic over all pixels. Temperature is a relative adjustment, not an absolute Kelvin setting.
5. Apply hue rotation and luminance-based saturation, then a 3D LUT with an adjustable mix.
6. Apply two separable five-tap bilateral passes for fast spatial noise reduction.
7. Build exact per-channel histograms from the rendered output.

Exposure accepts −10…10 stops, hue −360…360 degrees, denoising 0…100, LUT strength 0…1, and other color/curve controls −100…100. Invalid or non-finite settings throw `ArgumentOutOfRangeException`. A crop must fit within the normalized image and have positive width and height.

`ImageProcessor.ValidateSettings(settings)` applies the same validation without rendering or allocating a pixel buffer. Persistence layers should call it before saving and after loading untrusted edit recipes.

Curves use Hermite interpolation through black, quarter tone, midtone, three-quarter tone and white. The three controls move the inner knots by up to ±0.2. `ImageProcessor.EvaluateCurve(input, settings)` exposes the identical transfer curve for UI plotting, without applying other adjustments.

## LUT support

`CubeLut.Load` and `CubeLut.Parse` support 3D `.cube` files with `TITLE`, `LUT_3D_SIZE`, optional `DOMAIN_MIN`/`DOMAIN_MAX`, comments, and red-fastest RGB samples. Grid sizes of 2…128 are supported. Sampling uses trilinear interpolation; inputs outside the domain clamp to its edges. LUT output is clamped only when converting to rendered RGB bytes.

The parser rejects missing/extra samples, duplicate domain/size headers, invalid domains, non-finite numbers, unsupported 1D/shaper files, and unreasonable file/line/grid sizes. Numeric parsing is culture-independent.

## Performance and limitations

Rows run through `Parallel.For`, capped at 16 workers and leaving one logical processor available when possible. The renderer checks cancellation through parallel row scheduling and between major stages. Noise reduction rents its scratch buffer from `ArrayPool<byte>` and returns it even after cancellation. It uses spatial and channel-distance weights to smooth small variations while preserving strong edges. This is a conventional CPU filter; it is not an AI model, sensor-domain reconstruction or a replacement for commercial deep-learning noise reduction.

This version operates on display-referred 8-bit pixels. It cannot recover information already clipped or quantized by RAW decoding, does not provide a scene-linear/HDR pipeline, and has no GPU path. A later high-bit-depth renderer can preserve the public non-destructive settings model while replacing the pixel-processing implementation.

Run the no-dependency tests and optional benchmark from the repository root:

```powershell
dotnet run --project tests/RawSpeed.Core.Tests -c Release
dotnet run --project tests/RawSpeed.Core.Tests -c Release -- --benchmark
```

The benchmark uses deterministic synthetic RGBA images and a 33³ cube, warms each case at least three times and for at least 300 ms, then reports the median of five complete renders, including buffer allocation and histogram generation. It excludes file I/O, RAW decoding, display upload and export. Machine load and JIT compilation can affect individual runs; application latency must be measured separately.

Illustrative local results on Windows 10, .NET 9.0.11, 32 logical processors, 2026-10-15:

| Operation | 1600 × 1067 | 6000 × 4000 |
| --- | ---: | ---: |
| Identity + histogram | 2.2 ms | 12.4 ms |
| Color + histogram | 8.0 ms | 27.0 ms |
| Color + 33³ LUT | 6.7 ms | 76.4 ms |
| Denoise at 60 | 20.0 ms | 262.3 ms |
| Straighten + color | 4.6 ms | 56.7 ms |

The color configurations differ between rows; those timings should not be used to infer the incremental cost of a single operation.
