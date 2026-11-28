using System.Runtime.InteropServices;
using Rendition.Gpu.Vulkan;
using Rendition.Vulkan;
using Silk.NET.Vulkan;

namespace Rendition.Gpu.Tests;

[Collection(GpuCollection.Name)]
[Trait("Needs", "GPU")]
public sealed class MipmappedSamplerGpuTests(GpuFixture gpu)
{
    private const uint SourceSize = 16;
    private const uint SourceMips = 5;

    // Must match the Filter block in Shaders/environment_filter.comp.
    [StructLayout(LayoutKind.Sequential)]
    private struct FilterConstants
    {
        public int Irradiance;
        public float Roughness;
        public float TexelAngle;
        public float LodBias;
        public uint Samples;
    }

    [Fact]
    public void A_mipmapped_sampler_reads_the_lower_mips_a_plain_one_never_does()
    {
        int errors = gpu.ValidationErrors;

        // The source's sharpest mip is black and every other white, and the bias sends every sample two mips down.
        float mipmapped = FilteredBrightness(mipmapped: true);
        float plain = FilteredBrightness(mipmapped: false);

        Assert.InRange(mipmapped, 0.5f, 10f);
        Assert.InRange(plain, 0f, 0.001f);
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    private unsafe float FilteredBrightness(bool mipmapped)
    {
        var context = gpu.Context;
        var api = context.Api;
        using var layer = new VulkanGpu(context);
        using var source = new EnvironmentCube(context, SourceSize, SourceMips);
        using var target = new EnvironmentCube(context, 8, 1);
        using var filter = new CubeShader<FilterConstants>((context, layer), "environment_filter.comp");
        using var group = new ImageGroup(layer, layer.CreateSampler(new SamplerDescription(SamplerFilter.Linear, Mipmapped: mipmapped)));
        group.Make(filter.Pipeline, 0, [("source", source.Sampled), new GroupImage("target", target.Storage(0), Use: GroupImageUse.Written)]);
        using var readback = GpuBuffer.CreateHostVisible(context, CubeCentres.Bytes, BufferUsageFlags.TransferDstBit);
        var handle = readback.Handle;

        context.SubmitAndWait(cmd =>
        {
            Fill(api, cmd, source.Image);
            filter.Record(cmd, group.Id, target.Level(0), new FilterConstants
            {
                Irradiance = 1,
                TexelAngle = 4f * MathF.PI / (6f * SourceSize * SourceSize),
                LodBias = 2f,
                Samples = 64,
            });
            target.Ready(cmd, 0);
            CubeCentres.Record(api, cmd, target.Level(0), handle, 0);
        });

        var mean = CubeCentres.Mean(new ReadOnlySpan<Half>(readback.Mapped, CubeCentres.Halves));
        return (mean.X + mean.Y + mean.Z) / 3f;
    }

    // Clears the sharpest mip to black and the rest to white, leaving them all ready to sample.
    private static unsafe void Fill(Vk api, CommandBuffer cmd, Image image)
    {
        api.Transition(cmd, new ImageTransition(image, ImageAspectFlags.ColorBit, ImageLayout.Undefined, ImageLayout.TransferDstOptimal)
            { MipCount = SourceMips, LayerCount = 6 },
            (PipelineStageFlags2.None, AccessFlags2.None), (PipelineStageFlags2.ClearBit, AccessFlags2.TransferWriteBit));
        var black = new ClearColorValue(0f, 0f, 0f, 1f);
        var white = new ClearColorValue(1f, 1f, 1f, 1f);
        var sharpest = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 6);
        var rest = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 1, SourceMips - 1, 0, 6);
        api.CmdClearColorImage(cmd, image, ImageLayout.TransferDstOptimal, &black, 1, &sharpest);
        api.CmdClearColorImage(cmd, image, ImageLayout.TransferDstOptimal, &white, 1, &rest);
        api.Transition(cmd, new ImageTransition(image, ImageAspectFlags.ColorBit, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal)
            { MipCount = SourceMips, LayerCount = 6 },
            (PipelineStageFlags2.ClearBit, AccessFlags2.TransferWriteBit), (PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderSampledReadBit));
    }
}
