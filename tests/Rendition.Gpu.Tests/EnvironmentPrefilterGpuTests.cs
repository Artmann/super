using System.Numerics;
using Rendition.Passes;
using Rendition.Vulkan;
using Silk.NET.Vulkan;

namespace Rendition.Gpu.Tests;

[Collection(GpuCollection.Name)]
[Trait("Speed", "Slow")]
[Trait("Needs", "GPU")]
public sealed class EnvironmentPrefilterGpuTests(GpuFixture gpu)
{
    private const int ReflectionSize = 512;
    private const int ReflectionMips = 6;

    private static readonly Vector3 Ceiling = new(0.1f, 0.3f, 0.9f);
    private static readonly Vector3 Horizon = new(0.9f, 0.6f, 0.3f);
    private static readonly Vector3 Floor = new(0.2f, 0.15f, 0.1f);

    // The faces a cube's image stores in order, +X, -X, +Y, -Y, +Z, -Z, and the direction through each one's middle.
    private const int PositiveX = 0;
    private const int PositiveY = 2;
    private const int NegativeY = 3;

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void Rough_reflections_match_a_reference_integral_of_the_gradient(int mip)
    {
        int errors = gpu.ValidationErrors;
        using var environment = new EnvironmentMap(gpu.Context);
        environment.Bake(gpu.Context, new EnvironmentDescription { Ceiling = Ceiling, Horizon = Horizon, Floor = Floor }, default);

        float roughness = mip / (float)(ReflectionMips - 1);
        foreach (var (face, direction) in new[] { (PositiveX, Vector3.UnitX), (PositiveY, Vector3.UnitY), (NegativeY, -Vector3.UnitY) })
        {
            var texels = ReadFace(environment.ReflectionLevel(mip), face);
            var actual = Centre(texels, ReflectionSize >> mip);
            var expected = GgxReference(direction, roughness, Bands);
            AssertNear(expected, actual, $"mip {mip}, face {face}");
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void A_small_bright_panel_spreads_smoothly_in_the_rough_reflections()
    {
        const int mip = 3;
        var panelRadiance = new Vector3(40f);
        var grey = new Vector3(0.2f);
        using var environment = new EnvironmentMap(gpu.Context);
        environment.Bake(gpu.Context, new EnvironmentDescription
        {
            Ceiling = grey,
            Horizon = grey,
            Floor = grey,
            Panels = [new() { Direction = Vector3.UnitY, HalfWidth = 0.1f, HalfHeight = 0.1f, Radiance = panelRadiance }],
        }, default);

        int size = ReflectionSize >> mip;
        var texels = ReadFace(environment.ReflectionLevel(mip), PositiveY);
        for (int y = 1; y < size - 1; y++)
        {
            for (int x = 1; x < size - 1; x++)
            {
                float around = 0f;
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        around += texels[(y + dy) * size + x + dx].Y;
                    }
                }

                float mean = around / 9f;
                float own = texels[y * size + x].Y;
                Assert.True(own <= 1.5f * mean, $"Texel ({x}, {y}) is a speck: {own} against {mean} around it.");
            }
        }

        // The panel in the studio's colours: a square 0.2 across on the plane one unit up, softened over its last tenth.
        Vector3 Studio(Vector3 direction)
        {
            if (direction.Y <= 0f)
            {
                return grey;
            }

            var onPlane = direction / direction.Y;
            float edgeX = 1f - SmoothStep(0.9f, 1f, MathF.Abs(onPlane.X) / 0.1f);
            float edgeZ = 1f - SmoothStep(0.9f, 1f, MathF.Abs(onPlane.Z) / 0.1f);
            return Vector3.Lerp(grey, panelRadiance, edgeX * edgeZ);
        }

        var expected = GgxReference(Vector3.UnitY, mip / (float)(ReflectionMips - 1), Studio);
        var actual = Centre(texels, size);
        Assert.True(MathF.Abs(actual.Y - expected.Y) <= 0.1f * expected.Y, $"Expected about {expected}, got {actual}.");
    }

    [Fact]
    public void Recording_into_a_command_buffer_waits_for_nothing_and_fills_the_cubes()
    {
        int errors = gpu.ValidationErrors;
        var colour = new Vector3(0.3f, 0.6f, 0.15f);
        using var environment = new EnvironmentMap(gpu.Context);
        environment.SetDescription(new EnvironmentDescription { Ceiling = colour, Horizon = colour, Floor = colour });

        int waits = gpu.Context.Waits.SubmitAndWaitCount;
        gpu.Context.SubmitAndWait(cmd => environment.Record(cmd, default));

        Assert.Equal(1, gpu.Context.Waits.SubmitAndWaitCount - waits);
        var (irradiance, reflection) = environment.Flat;
        Assert.True(Vector3.Distance(colour, irradiance) < 2e-3f, $"Expected {colour}, got {irradiance}.");
        Assert.True(Vector3.Distance(colour, reflection) < 2e-3f, $"Expected {colour}, got {reflection}.");
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void A_change_redraws_the_sharp_levels_at_once_and_the_rough_ones_a_slice_a_frame()
    {
        int errors = gpu.ValidationErrors;
        var before = new Vector3(0.9f, 0.2f, 0.1f);
        var after = new Vector3(0.1f, 0.4f, 0.8f);
        using var environment = new EnvironmentMap(gpu.Context);
        environment.Bake(gpu.Context, Even(before), default);

        environment.SetDescription(Even(after));
        gpu.Context.SubmitAndWait(cmd => environment.RecordFrame(cmd, default, changed: true));

        AssertNear(after, Centre(ReadFace(environment.ReflectionLevel(0), PositiveX), ReflectionSize), "the sharpest level");
        AssertNear(after, environment.Flat.Irradiance, "the soft light");
        AssertNear(before, Centre(ReadFace(environment.ReflectionLevel(ReflectionMips - 1), PositiveX),
            ReflectionSize >> (ReflectionMips - 1)), "the roughest level, a slice later");
        Assert.True(environment.Unfinished);

        for (int frame = 1; frame < RoughSlices.Count; frame++)
        {
            gpu.Context.SubmitAndWait(cmd => environment.RecordFrame(cmd, default, changed: false));
        }

        Assert.False(environment.Unfinished);
        for (int mip = 1; mip < ReflectionMips; mip++)
        {
            AssertNear(after, Centre(ReadFace(environment.ReflectionLevel(mip), PositiveX), ReflectionSize >> mip), $"mip {mip}");
        }

        AssertNear(after, environment.Flat.Reflection, "the flat reflection");
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    private static EnvironmentDescription Even(Vector3 radiance) =>
        new() { Ceiling = radiance, Horizon = radiance, Floor = radiance };

    private static Vector3 Bands(Vector3 direction) => direction.Y >= 0f
        ? Vector3.Lerp(Horizon, Ceiling, MathF.Sqrt(direction.Y))
        : Vector3.Lerp(Horizon, Floor, MathF.Sqrt(-direction.Y));

    private static float SmoothStep(float from, float to, float value)
    {
        float t = Math.Clamp((value - from) / (to - from), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>
    /// What a surface of this roughness facing <paramref name="normal"/> reflects from the studio, seen straight on: GGX
    /// half-vectors from 4096 Hammersley points, each reflected direction weighted by its cosine.
    /// </summary>
    private static Vector3 GgxReference(Vector3 normal, float roughness, Func<Vector3, Vector3> studio)
    {
        const int samples = 4096;
        var tangent = Vector3.Normalize(Vector3.Cross(MathF.Abs(normal.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX, normal));
        var bitangent = Vector3.Cross(normal, tangent);
        float alpha = roughness * roughness;
        var sum = Vector3.Zero;
        float total = 0f;
        for (uint i = 0; i < samples; i++)
        {
            float u = i / (float)samples;
            float v = ReverseBits(i) * 2.3283064365386963e-10f;
            float phi = 2f * MathF.PI * u;
            float cosTheta = MathF.Sqrt((1f - v) / (1f + (alpha * alpha - 1f) * v));
            float sinTheta = MathF.Sqrt(1f - cosTheta * cosTheta);
            var half = tangent * (sinTheta * MathF.Cos(phi)) + bitangent * (sinTheta * MathF.Sin(phi)) + normal * cosTheta;
            var light = Vector3.Reflect(-normal, half);
            float weight = Vector3.Dot(normal, light);
            if (weight > 0f)
            {
                sum += studio(Vector3.Normalize(light)) * weight;
                total += weight;
            }
        }

        return sum / total;
    }

    private static uint ReverseBits(uint bits)
    {
        bits = (bits << 16) | (bits >> 16);
        bits = ((bits & 0x00ff00ffu) << 8) | ((bits & 0xff00ff00u) >> 8);
        bits = ((bits & 0x0f0f0f0fu) << 4) | ((bits & 0xf0f0f0f0u) >> 4);
        bits = ((bits & 0x33333333u) << 2) | ((bits & 0xccccccccu) >> 2);
        return ((bits & 0x55555555u) << 1) | ((bits & 0xaaaaaaaau) >> 1);
    }

    private static void AssertNear(Vector3 expected, Vector3 actual, string where)
    {
        for (int channel = 0; channel < 3; channel++)
        {
            float e = expected[channel];
            float a = actual[channel];
            float allowed = e < 0.15f ? 0.005f : 0.03f * e;
            Assert.True(MathF.Abs(a - e) <= allowed, $"{where}: expected {expected}, got {actual}.");
        }
    }

    /// <summary>The 2 x 2 texels round a face's middle, as a linear lookup straight at it blends them.</summary>
    private static Vector3 Centre(Vector3[] texels, int size)
    {
        int corner = size / 2 - 1;
        return (texels[corner * size + corner] + texels[corner * size + corner + 1]
            + texels[(corner + 1) * size + corner] + texels[(corner + 1) * size + corner + 1]) / 4f;
    }

    private unsafe Vector3[] ReadFace(CubeLevel level, int face)
    {
        int size = (int)level.Size;
        ulong bytes = (ulong)(size * size * 4 * sizeof(ushort));
        using var buffer = GpuBuffer.CreateHostVisible(gpu.Context, bytes, BufferUsageFlags.TransferDstBit);
        var api = gpu.Context.Api;
        var handle = buffer.Handle;
        gpu.Context.SubmitAndWait(cmd =>
        {
            api.Transition(cmd, level.Image, ImageAspectFlags.ColorBit,
                ImageLayout.ShaderReadOnlyOptimal, ImageLayout.TransferSrcOptimal,
                PipelineStageFlags2.AllCommandsBit, AccessFlags2.MemoryWriteBit,
                PipelineStageFlags2.CopyBit, AccessFlags2.TransferReadBit,
                baseMip: (uint)level.Mip, layerCount: 6);
            var region = new BufferImageCopy
            {
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, (uint)level.Mip, (uint)face, 1),
                ImageExtent = new Extent3D((uint)size, (uint)size, 1),
            };
            api.CmdCopyImageToBuffer(cmd, level.Image, ImageLayout.TransferSrcOptimal, handle, 1, &region);
            api.Transition(cmd, level.Image, ImageAspectFlags.ColorBit,
                ImageLayout.TransferSrcOptimal, ImageLayout.ShaderReadOnlyOptimal,
                PipelineStageFlags2.CopyBit, AccessFlags2.None,
                PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit,
                baseMip: (uint)level.Mip, layerCount: 6);
        });

        var halves = new ReadOnlySpan<Half>(buffer.Mapped, size * size * 4);
        var texels = new Vector3[size * size];
        for (int i = 0; i < texels.Length; i++)
        {
            texels[i] = new Vector3((float)halves[i * 4], (float)halves[i * 4 + 1], (float)halves[i * 4 + 2]);
        }

        return texels;
    }
}
