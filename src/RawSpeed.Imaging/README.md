# RawSpeed.Imaging

Image loading and export service for the RawSpeed WPF editor. Targets .NET 9 and uses
`Magick.NET-Q16-AnyCPU` 15.0.0 (Apache-2.0 wrapper, bundled native libraries have their
own notices). ImageMagick includes LibRaw, libjpeg, Little CMS, PNG and TIFF support.

## Data contract

`DecodedImage.Pixels` contains tightly packed **straight-alpha RGBA8, sRGB** pixels.
`Width` and `Height` are the actual buffer dimensions. `OriginalWidth` and
`OriginalHeight` describe the oriented source before downsampling. Treat a returned
buffer as caller-owned; do not modify an export buffer while `ExportAsync` is running.

`LoadPreviewAsync` and `LoadFullAsync` develop NEF/ORF sensor data with LibRaw, using
the camera white balance and AHD interpolation. Native development uses 16-bit
precision before conversion to the editor's 8-bit display buffer. JPEG previews use
libjpeg's reduced IDCT decoding when possible. Embedded ICC profiles are transformed
to sRGB, and EXIF orientation is applied exactly once.

`TryLoadEmbeddedPreviewAsync` is an optional fast first paint for RAW files. It reads
the largest camera-rendered thumbnail without sensor development, explicitly labels
the result as embedded, and returns null when unavailable. Its framing and color may
differ from the developed RAW. Replace this temporary preview with `LoadPreviewAsync`
before accepting edits. JPEG input returns null.

All native operations run on a worker thread behind a shared semaphore. Cancellation
is effective while waiting and at operation boundaries; it cannot interrupt an active
LibRaw call. Callers should discard obsolete results using a request version/token.

Export writes JPEG, PNG or TIFF with sRGB color information. JPEG/TIFF embed the ICC
profile; the PNG encoder may represent the same standard profile as sRGB/gAMA chunks.
A same-directory temporary
file is moved atomically to the final destination. Existing files are never replaced,
including source photographs. `ExportOptions` controls resizing, DPI, JPEG quality
and progressive encoding, PNG compression, TIFF compression, 8/16-bit output, sRGB /
Adobe RGB conversion, and optional safe capture metadata with separately selected GPS.
MetadataExtractor 2.9.3 reads standard RAW IFDs when LibRaw does not expose the source EXIF.
The old quality-based overload remains compatible and exports no capture metadata.

The quality parameter controls JPEG only. PNG defaults to fast lossless zlib level 2.
Without resizing or color conversion, PNG and TIFF preserve the input RGBA8 values.
16-bit output containers retain computed conversion/resampling precision, but cannot
recover source RAW precision already lost before the 8-bit editing buffer. JPEG
composites transparency over white. See [export options and shortcuts](../../docs/export-and-shortcuts.md)
for the complete metadata policy and supported options.

## Verification

Run from the repository root:

```powershell
dotnet run --project tools/RawSpeed.IoProbe -c Release -- .
```

The smoke harness develops the user-supplied NEF, ORF and JPEG in `example/`, verifies
buffer sizes and source SHA-256 stability, tests all export formats and no-overwrite,
checks cancellation and malformed input, and checks EXIF orientation and Adobe RGB
to sRGB conversion with generated fixtures. Timings and visual previews are written
to ignored `artifacts/io/` directories. These timings are local measurements, not a
general performance guarantee.
