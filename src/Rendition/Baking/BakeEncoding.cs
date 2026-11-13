using System.Numerics;
using System.Runtime.InteropServices;
using Rendition.Vulkan;

namespace Rendition;

/// <summary>
/// Turns what the <see cref="Baker"/> traced and read back into the pages a <c>.lightbake</c> file stores: a page's
/// light, a chunk's transfer and the probes' tiles, and the floats the filter's transfer runs move in and out. Runs on
/// the CPU, with no GPU work of its own.
/// </summary>
internal static class BakeEncoding
{
    /// <summary>
    /// The dilation averages ten floats a texel, irradiance rgb, direction xyz and mask xyzw, so a chunk's twenty floats
    /// of transfer go through it in this many runs.
    /// </summary>
    internal const int TransferRuns = Baker.TransferFloats / TransferRunFloats;

    private const int TransferRunFloats = 10;

    /// <summary>
    /// Writes one probe's staged tiles, as <see cref="ProbeVolume.RecordFold"/> left them in float32, into the bake's
    /// pages at the probe's tile: irradiance in RGBA16F with alpha 1, the depth's two moments in RGBA16F with blue and
    /// alpha 0, since the file has no two-channel format.
    /// </summary>
    internal static void WriteTile(ReadOnlySpan<float> staged, int probe, Span<byte> irradiance, Span<byte> depth)
    {
        const int irradianceTile = ProbeVolume.IrradianceTexels + 2;
        const int depthTile = ProbeVolume.DepthTexels + 2;
        int column = probe % BakedProbeAtlas.Columns;
        int row = probe / BakedProbeAtlas.Columns;
        var irradianceHalves = MemoryMarshal.Cast<byte, Half>(irradiance);
        var depthHalves = MemoryMarshal.Cast<byte, Half>(depth);
        for (int y = 0; y < irradianceTile; y++)
        {
            for (int x = 0; x < irradianceTile; x++)
            {
                int from = (x + y * irradianceTile) * 4;
                int to = ((column * irradianceTile + x) + (row * irradianceTile + y) * BakedProbeAtlas.IrradianceAtlasSize) * 4;
                irradianceHalves[to + 0] = (Half)staged[from + 0];
                irradianceHalves[to + 1] = (Half)staged[from + 1];
                irradianceHalves[to + 2] = (Half)staged[from + 2];
                irradianceHalves[to + 3] = (Half)1f;
            }
        }

        int depthStart = irradianceTile * irradianceTile * 4;
        for (int y = 0; y < depthTile; y++)
        {
            for (int x = 0; x < depthTile; x++)
            {
                int from = depthStart + (x + y * depthTile) * 2;
                int to = ((column * depthTile + x) + (row * depthTile + y) * BakedProbeAtlas.DepthAtlasSize) * 4;
                depthHalves[to + 0] = (Half)staged[from + 0];
                depthHalves[to + 1] = (Half)staged[from + 1];
                depthHalves[to + 2] = Half.Zero;
                depthHalves[to + 3] = Half.Zero;
            }
        }
    }

    /// <summary>
    /// A page's traced light as the <c>.lightbake</c> file stores it, 2048 texels square: irradiance in RGBA16F, the
    /// direction it mostly comes from in RGBA8 (<c>d * 0.5 + 0.5</c>, alpha <c>|d|</c>) and the shadow mask in RGBA8.
    /// A texel that isn't <see cref="TracedPage.Filled"/> holds no light, no direction and a mask of 1; run
    /// <see cref="Baker.FilterAndDilate"/> first so gutters and invalid texels are filled.
    /// </summary>
    internal static (LightBakePage Irradiance, LightBakePage Direction, LightBakePage Mask) Encode(TracedPage traced)
    {
        const int side = LightmapLayout.PageSize;
        var irradiance = new byte[side * side * 8];
        var direction = new byte[side * side * 4];
        var mask = new byte[side * side * 4];
        var halves = MemoryMarshal.Cast<byte, Half>(irradiance.AsSpan());

        // Outside the region, and on texels that aren't valid: unlit, no direction, nothing masked.
        for (int i = 0; i < side * side; i++)
        {
            direction[i * 4 + 0] = 128;
            direction[i * 4 + 1] = 128;
            direction[i * 4 + 2] = 128;
            mask[i * 4 + 0] = 255;
            mask[i * 4 + 1] = 255;
            mask[i * 4 + 2] = 255;
            mask[i * 4 + 3] = 255;
        }

        for (int row = 0; row < traced.Height; row++)
        {
            for (int column = 0; column < traced.Width; column++)
            {
                int here = row * traced.Width + column;
                if (traced.Filled[here])
                {
                    int at = (traced.Y + row) * side + traced.X + column;
                    EncodeTexel(traced, here, halves.Slice(at * 4, 4), direction.AsSpan(at * 4, 4), mask.AsSpan(at * 4, 4));
                }
            }
        }

        return (
            new LightBakePage(side, side, LightBakePageFormat.Rgba16f, irradiance),
            new LightBakePage(side, side, LightBakePageFormat.Rgba8, direction),
            new LightBakePage(side, side, LightBakePageFormat.Rgba8, mask));
    }

    // One filled texel's light, its mean direction and its mask, written into its four channels of each page.
    private static void EncodeTexel(TracedPage traced, int here, Span<Half> irradiance, Span<byte> direction, Span<byte> mask)
    {
        for (int c = 0; c < 3; c++)
        {
            irradiance[c] = Light(traced.Irradiance[here * 4 + c]);
        }

        irradiance[3] = (Half)1f;
        var d = new Vector3(traced.Direction[here * 4], traced.Direction[here * 4 + 1], traced.Direction[here * 4 + 2]);
        float weight = traced.Direction[here * 4 + 3];
        d = weight > 1e-8f ? d / weight : Vector3.Zero;
        direction[0] = Unorm(d.X * 0.5f + 0.5f);
        direction[1] = Unorm(d.Y * 0.5f + 0.5f);
        direction[2] = Unorm(d.Z * 0.5f + 0.5f);
        direction[3] = Unorm(d.Length());
        for (int c = 0; c < 4; c++)
        {
            mask[c] = Unorm(traced.Mask[here * 4 + c]);
        }
    }

    /// <summary>
    /// The page with what a filter run of its light read back: its irradiance, direction and mask, and
    /// <see cref="TracedPage.Filled"/> marking the texels that hold light.
    /// </summary>
    internal static TracedPage Filtered(TracedPage traced, FilterRun run)
    {
        var (irradiance, direction, mask) = run.Read();
        int count = traced.Width * traced.Height;
        var filled = new bool[count];
        for (int i = 0; i < count; i++)
        {
            filled[i] = irradiance[i * 4 + 3] > 0.5f;
        }

        return traced with { Irradiance = irradiance, Direction = direction, Mask = mask, Filled = filled };
    }

    /// <summary>
    /// What one run of a chunk's transfer dilation uploads: ten of each valid texel's transfer floats, made finite, in
    /// the filter's irradiance rgb, direction xyz and mask xyzw, with irradiance w marking the texels that hold a value.
    /// The transfer isn't smoothed, so run the filter without prepare or smoothing; <see cref="ReadTransferRun"/> takes
    /// its result. Dilated with the same <see cref="BakeFilter.DilationRings"/> rings as the light, gutters and invalid
    /// texels take their neighbours' transfer. See "Chunk transfer" in docs/design.md.
    /// </summary>
    /// <exception cref="ArgumentException">The page was traced without its transfer.</exception>
    internal static (float[] Irradiance, float[] Direction, float[] Mask) TransferRun(TracedPage traced, int run)
    {
        if (traced.Transfer is not { } transfer)
        {
            throw new ArgumentException("The page was traced without its transfer.", nameof(traced));
        }

        int count = traced.Width * traced.Height;
        var irradiance = new float[count * 4];
        var direction = new float[count * 4];
        var mask = new float[count * 4];
        for (int t = 0; t < count; t++)
        {
            if (!traced.Valid[t])
            {
                continue;
            }

            int from = t * Baker.TransferFloats + run * TransferRunFloats;
            for (int c = 0; c < 3; c++)
            {
                irradiance[t * 4 + c] = Finite(transfer[from + c]);
                direction[t * 4 + c] = Finite(transfer[from + 3 + c]);
            }

            irradiance[t * 4 + 3] = 1f;
            for (int c = 0; c < 4; c++)
            {
                mask[t * 4 + c] = Finite(transfer[from + 6 + c]);
            }
        }

        return (irradiance, direction, mask);
    }

    /// <summary>
    /// Puts what run <paramref name="run"/> of the transfer dilation read back into <paramref name="transfer"/>, laid
    /// out as <see cref="TracedPage.Transfer"/>, and marks in <paramref name="filled"/> the texels that hold transfer.
    /// </summary>
    internal static void ReadTransferRun(FilterRun filter, int run, float[] transfer, bool[] filled)
    {
        var (a, b, m) = filter.Read();
        int count = filter.Width * filter.Height;
        for (int t = 0; t < count; t++)
        {
            filled[t] = a[t * 4 + 3] > 0.5f;
            int to = t * Baker.TransferFloats + run * TransferRunFloats;
            for (int c = 0; c < 3; c++)
            {
                transfer[to + c] = a[t * 4 + c];
                transfer[to + 3 + c] = b[t * 4 + c];
            }

            for (int c = 0; c < 4; c++)
            {
                transfer[to + 6 + c] = m[t * 4 + c];
            }
        }
    }

    /// <summary>
    /// A chunk's dilated transfer as the <c>.lightbake</c> file stores it, on a page of <paramref name="side"/> texels
    /// cut from the top left of the baker's page: the sky's coefficients for red, green and blue and the sun's in
    /// RGBA16F, and the sun's tint in RGBA8. The tint is the sun transfer's colour divided by its largest channel, and
    /// the sun's coefficients are scaled to match, so tint times sun gives each colour's transfer. A texel that isn't
    /// <see cref="TracedPage.Filled"/> holds no transfer and a white tint. Dilate it first through the filter,
    /// <see cref="TransferRun"/> then <see cref="ReadTransferRun"/>.
    /// </summary>
    internal static LightBakeTransfer EncodeTransfer(TracedPage traced, int side)
    {
        if (traced.Transfer is not { } transfer)
        {
            throw new ArgumentException("The page was traced without its transfer.", nameof(traced));
        }

        var sh = new byte[4][];
        for (int s = 0; s < 4; s++)
        {
            sh[s] = new byte[side * side * 8];
        }

        var tint = new byte[side * side * 4];
        Array.Fill(tint, (byte)255);
        for (int row = 0; row < traced.Height; row++)
        {
            for (int column = 0; column < traced.Width; column++)
            {
                int x = traced.X + column;
                int y = traced.Y + row;
                int here = row * traced.Width + column;
                if (x < side && y < side && traced.Filled[here])
                {
                    EncodeTransferTexel(transfer.AsSpan(here * Baker.TransferFloats, Baker.TransferFloats), sh, tint, y * side + x);
                }
            }
        }

        return new LightBakeTransfer(
            new LightBakePage(side, side, LightBakePageFormat.Rgba16f, sh[0]),
            new LightBakePage(side, side, LightBakePageFormat.Rgba16f, sh[1]),
            new LightBakePage(side, side, LightBakePageFormat.Rgba16f, sh[2]),
            new LightBakePage(side, side, LightBakePageFormat.Rgba16f, sh[3]),
            new LightBakePage(side, side, LightBakePageFormat.Rgba8, tint));
    }

    // One filled texel's transfer, its TransferFloats floats, written at texel `at` of the four coefficient pages and
    // the tint page.
    private static void EncodeTransferTexel(ReadOnlySpan<float> transfer, byte[][] sh, byte[] tint, int at)
    {
        for (int s = 0; s < 3; s++)
        {
            var halves = MemoryMarshal.Cast<byte, Half>(sh[s].AsSpan());
            for (int c = 0; c < 4; c++)
            {
                halves[at * 4 + c] = (Half)Finite(transfer[s * 4 + c]);
            }
        }

        // The sun was summed by luminance, its colour apart: rescale so the brightest tint channel is 1.
        float r = MathF.Max(Finite(transfer[16]), 0f);
        float g = MathF.Max(Finite(transfer[17]), 0f);
        float b = MathF.Max(Finite(transfer[18]), 0f);
        float largest = MathF.Max(r, MathF.Max(g, b));
        float luminance = Luminance(r, g, b);
        float scale = largest > 0f && luminance > 0f ? largest / luminance : 0f;
        var sun = MemoryMarshal.Cast<byte, Half>(sh[3].AsSpan());
        for (int c = 0; c < 4; c++)
        {
            sun[at * 4 + c] = (Half)Finite(transfer[12 + c] * scale);
        }

        if (largest > 0f)
        {
            tint[at * 4 + 0] = Unorm(r / largest);
            tint[at * 4 + 1] = Unorm(g / largest);
            tint[at * 4 + 2] = Unorm(b / largest);
        }
    }

    /// <summary>The top left <paramref name="side"/> texels square of a page, for a chunk's page baked in that corner.</summary>
    internal static LightBakePage Crop(LightBakePage page, int side)
    {
        if (side == page.Width && side == page.Height)
        {
            return page;
        }

        int texel = page.Format == LightBakePageFormat.Rgba16f ? 8 : 4;
        var pixels = new byte[side * side * texel];
        for (int row = 0; row < side; row++)
        {
            Array.Copy(page.Pixels, row * page.Width * texel, pixels, row * side * texel, side * texel);
        }

        return new LightBakePage(side, side, page.Format, pixels);
    }

    // The same weights as luminance() in bake_paths.glsl.
    internal static float Luminance(float r, float g, float b) => 0.2126f * r + 0.7152f * g + 0.0722f * b;

    private static byte Unorm(float value) => (byte)MathF.Round(Math.Clamp(value, 0f, 1f) * 255f);

    // Irradiance as a half float the lighting can add up: not a number and anything below 0 are no light, and anything
    // past a half's largest, 65504, is held there rather than becoming infinity, which bloom would spread.
    private static Half Light(float value) => float.IsNaN(value) ? Half.Zero : (Half)Math.Clamp(value, 0f, 65504f);

    // Not a number becomes 0, and anything past a half's range its limit, so a half float can hold it.
    private static float Finite(float value) => float.IsNaN(value) ? 0f : Math.Clamp(value, -65504f, 65504f);
}
