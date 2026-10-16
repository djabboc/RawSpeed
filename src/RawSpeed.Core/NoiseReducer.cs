using System.Buffers;

namespace RawSpeed.Core;

/// <summary>
/// CPU noise reduction in YCoCg: luminance uses undecimated multiscale B3-spline wavelets with
/// firm shrinkage; chroma uses three scales of luminance/chroma-guided bilateral filtering.
/// It is a spatial, display-referred filter, not an AI or sensor-reconstruction model.
/// </summary>
public static class NoiseReducer
{
    private const int TileSize = 256;
    private const int Halo = 32;
    private const int Side = TileSize + Halo * 2;

    /// <summary>Reduces only noise in source coordinates, ignoring all geometry and color controls.</summary>
    public static byte[] Reduce(byte[] rgba, int width, int height, EditSettings settings, CancellationToken cancellationToken = default)
    {
        ImageProcessor.ValidateSettings(settings);
        return Reduce(rgba, width, height, Math.Max(settings.Denoise, settings.LuminanceDenoise),
            Math.Max(settings.Denoise, settings.ColorDenoise), settings.DenoiseDetail, cancellationToken);
    }

    public static byte[] Reduce(byte[] rgba, int width, int height, double luminance, double color,
        double detail = 50, CancellationToken cancellationToken = default) =>
        ImageProcessor.Render(rgba, width, height, new EditSettings
        {
            LuminanceDenoise = luminance, ColorDenoise = color, DenoiseDetail = detail
        }, cancellationToken: cancellationToken).Pixels;

    internal static void ApplyInPlace(byte[] pixels, int width, int height, double luminance, double color,
        double detail, ParallelOptions options)
    {
        // Snapshot prevents one tile from sampling pixels already changed by another worker.
        byte[] original = ArrayPool<byte>.Shared.Rent(pixels.Length);
        try
        {
            Buffer.BlockCopy(pixels, 0, original, 0, pixels.Length);
            float sigma = EstimateNoise(original, width, height, options.CancellationToken);
            float threshold = sigma * (float)(luminance / 100) * (float)(3.7 - detail * 0.025);
            int levels = detail >= 85 ? 2 : 3;
            float chromaBlend = (float)(color / 100);
            float[] lumaWeights = RangeWeights((float)(11 + (100 - detail) * 0.12));
            float[] chromaWeights = RangeWeights((float)(8 + color * 0.50));
            int tilesAcross = (width + TileSize - 1) / TileSize;
            int tileCount = tilesAcross * ((height + TileSize - 1) / TileSize);
            Parallel.For(0, tileCount, options, () => new TileBuffers(), (tile, _, buffers) =>
            {
                int originX = tile % tilesAcross * TileSize, originY = tile / tilesAcross * TileSize;
                int innerWidth = Math.Min(TileSize, width - originX), innerHeight = Math.Min(TileSize, height - originY);
                int tw = innerWidth + Halo * 2, th = innerHeight + Halo * 2;
                LoadTile(original, width, height, originX, originY, tw, th, buffers);
                float[] y = buffers.Y;
                if (luminance > 0) y = WaveletLuminance(buffers, tw, th, threshold, levels, options.CancellationToken);
                if (color > 0)
                {
                    for (int level = 0; level < levels; level++)
                    {
                        options.CancellationToken.ThrowIfCancellationRequested();
                        int stride = 1 << level;
                        ChromaPass(buffers.Co, buffers.Cg, buffers.TempCo, buffers.TempCg, y, tw, th, stride,
                            true, chromaBlend, lumaWeights, chromaWeights, options.CancellationToken);
                        ChromaPass(buffers.TempCo, buffers.TempCg, buffers.Co, buffers.Cg, y, tw, th, stride,
                            false, chromaBlend, lumaWeights, chromaWeights, options.CancellationToken);
                    }
                }
                for (int row = 0; row < innerHeight; row++)
                {
                    options.CancellationToken.ThrowIfCancellationRequested();
                    for (int column = 0; column < innerWidth; column++)
                    {
                        int local = (row + Halo) * tw + column + Halo;
                        int output = ((originY + row) * width + originX + column) * 4;
                        float common = y[local] - buffers.Cg[local] * 0.5f;
                        pixels[output] = ToByte(common + buffers.Co[local]);
                        pixels[output + 1] = ToByte(y[local] + buffers.Cg[local] * 0.5f);
                        pixels[output + 2] = ToByte(common - buffers.Co[local]);
                    }
                }
                return buffers;
            }, buffers => buffers.Dispose());
        }
        finally { ArrayPool<byte>.Shared.Return(original); }
    }

    private static void LoadTile(byte[] source, int width, int height, int originX, int originY, int tw, int th, TileBuffers buffers)
    {
        for (int y = 0; y < th; y++)
        {
            int sourceY = Math.Clamp(originY + y - Halo, 0, height - 1);
            for (int x = 0; x < tw; x++)
            {
                int sourceX = Math.Clamp(originX + x - Halo, 0, width - 1);
                int input = (sourceY * width + sourceX) * 4, target = y * tw + x;
                float r = source[input], g = source[input + 1], b = source[input + 2];
                buffers.Y[target] = (r + 2 * g + b) * 0.25f;
                buffers.Co[target] = (r - b) * 0.5f;
                buffers.Cg[target] = g - (r + b) * 0.5f;
            }
        }
    }

    private static float[] WaveletLuminance(TileBuffers buffers, int width, int height, float threshold,
        int levels, CancellationToken token)
    {
        int count = width * height;
        Array.Clear(buffers.Details, 0, count);
        float[] current = buffers.Y, next = buffers.Next;
        for (int level = 0; level < levels; level++)
        {
            token.ThrowIfCancellationRequested();
            int stride = 1 << level;
            float edgeCutoff = Math.Clamp(threshold * 2.5f + 8, 12, 64);
            Blur(current, buffers.Temp, width, height, stride, true, edgeCutoff, token);
            Blur(buffers.Temp, next, width, height, stride, false, edgeCutoff, token);
            float scaleThreshold = threshold * (level == 0 ? 0.89f : level == 1 ? 0.20f : 0.09f);
            float upper = scaleThreshold * 2.5f;
            for (int i = 0; i < count; i++)
            {
                float coefficient = current[i] - next[i], magnitude = MathF.Abs(coefficient);
                // Unlike soft thresholding, sufficiently strong edges retain their full coefficients.
                float retained = magnitude <= scaleThreshold ? 0 : magnitude >= upper ? magnitude :
                    upper * (magnitude - scaleThreshold) / (upper - scaleThreshold);
                buffers.Details[i] += MathF.CopySign(retained, coefficient);
            }
            (current, next) = (next, current);
        }
        for (int i = 0; i < count; i++) current[i] = Math.Clamp(current[i] + buffers.Details[i], 0, 255);
        return current;
    }

    private static void Blur(float[] source, float[] target, int width, int height, int stride, bool horizontal, float edgeCutoff, CancellationToken token)
    {
        for (int y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                int index = y * width + x;
                int a = horizontal ? y * width + Math.Max(0, x - 2 * stride) : Math.Max(0, y - 2 * stride) * width + x;
                int b = horizontal ? y * width + Math.Max(0, x - stride) : Math.Max(0, y - stride) * width + x;
                int c = horizontal ? y * width + Math.Min(width - 1, x + stride) : Math.Min(height - 1, y + stride) * width + x;
                int d = horizontal ? y * width + Math.Min(width - 1, x + 2 * stride) : Math.Min(height - 1, y + 2 * stride) * width + x;
                float center = source[index];
                float wa = MathF.Abs(source[a] - center) < edgeCutoff ? 1 : 0;
                float wb = MathF.Abs(source[b] - center) < edgeCutoff ? 4 : 0;
                float wc = MathF.Abs(source[c] - center) < edgeCutoff ? 4 : 0;
                float wd = MathF.Abs(source[d] - center) < edgeCutoff ? 1 : 0;
                target[index] = (wa * source[a] + wb * source[b] + 6 * center + wc * source[c] + wd * source[d]) / (wa + wb + 6 + wc + wd);
            }
        }
    }

    private static void ChromaPass(float[] co, float[] cg, float[] targetCo, float[] targetCg, float[] guide,
        int width, int height, int stride, bool horizontal, float blend, float[] lumaWeights, float[] colorWeights, CancellationToken token)
    {
        for (int y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                int index = y * width + x;
                float centerCo = co[index], centerCg = cg[index], centerY = guide[index];
                float sumCo = centerCo * 6, sumCg = centerCg * 6, sumWeight = 6;
                for (int tap = -2; tap <= 2; tap++)
                {
                    if (tap == 0) continue;
                    int sample = horizontal ? y * width + Math.Clamp(x + tap * stride, 0, width - 1) : Math.Clamp(y + tap * stride, 0, height - 1) * width + x;
                    int dy = Math.Min(511, (int)MathF.Abs(guide[sample] - centerY));
                    int dc = Math.Min(511, (int)MathF.Max(MathF.Abs(co[sample] - centerCo), MathF.Abs(cg[sample] - centerCg)));
                    float weight = (Math.Abs(tap) == 1 ? 4 : 1) * lumaWeights[dy] * colorWeights[dc];
                    sumWeight += weight; sumCo += co[sample] * weight; sumCg += cg[sample] * weight;
                }
                targetCo[index] = centerCo + (sumCo / sumWeight - centerCo) * blend;
                targetCg[index] = centerCg + (sumCg / sumWeight - centerCg) * blend;
            }
        }
    }

    private static float[] RangeWeights(float sigma)
    {
        var weights = new float[512];
        for (int i = 0; i < weights.Length; i++) weights[i] = i > sigma * 3 ? 0 : MathF.Exp(-i * i / (2 * sigma * sigma));
        return weights;
    }

    private static float EstimateNoise(byte[] pixels, int width, int height, CancellationToken token)
    {
        if (width < 3 || height < 3) return 3;
        Span<int> histogram = stackalloc int[1024];
        histogram.Clear();
        int samples = 0, rowStep = Math.Max(1, height / 256), columnStep = Math.Max(1, width / 512);
        for (int y = 1; y < height - 1; y += rowStep)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 1; x < width - 1; x += columnStep)
            {
                int i = (y * width + x) * 4;
                if (pixels[i + 3] < 128) continue;
                float laplacian = 4 * Luma(pixels, i) - Luma(pixels, i - 4) - Luma(pixels, i + 4) -
                    Luma(pixels, i - width * 4) - Luma(pixels, i + width * 4);
                histogram[Math.Min(1023, (int)MathF.Abs(laplacian))]++; samples++;
            }
        }
        int sum = 0;
        for (int bin = 0; bin < histogram.Length; bin++)
        {
            sum += histogram[bin];
            if (sum >= Math.Max(1, samples / 2)) return Math.Clamp(bin / 3.016f, 0.5f, 40);
        }
        return 3;
    }

    private static float Luma(byte[] pixels, int index) => (pixels[index] + 2 * pixels[index + 1] + pixels[index + 2]) * 0.25f;
    private static byte ToByte(float value) => (byte)Math.Clamp((int)MathF.Round(value), 0, 255);

    private sealed class TileBuffers : IDisposable
    {
        private readonly float[][] _buffers = Enumerable.Range(0, 8).Select(_ => ArrayPool<float>.Shared.Rent(Side * Side)).ToArray();
        internal float[] Y => _buffers[0];
        internal float[] Co => _buffers[1];
        internal float[] Cg => _buffers[2];
        internal float[] Next => _buffers[3];
        internal float[] Temp => _buffers[4];
        internal float[] Details => _buffers[5];
        internal float[] TempCo => _buffers[6];
        internal float[] TempCg => _buffers[7];
        public void Dispose() { foreach (float[] buffer in _buffers) ArrayPool<float>.Shared.Return(buffer); }
    }
}
