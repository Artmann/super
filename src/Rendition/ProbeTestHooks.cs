using System.Numerics;
using Rendition.Vulkan;
using Silk.NET.Vulkan;

namespace Rendition;

/// <summary>
/// What tests do to the live light probes: clear or remake their atlases, mark probes for an update, write a tile, and
/// read the soft light the lighting pass would. Each waits for the GPU, so never on the frame path.
/// <paramref name="lastFrameSet"/> gives the frame set whose uniforms the last frame wrote, and
/// <paramref name="atlasesChanged"/> points the sets that read the atlases at them again.
/// </summary>
internal sealed unsafe class ProbeTestHooks(
    VulkanContext context, LightProbes probes, DescriptorKit kit, Func<DescriptorSet> lastFrameSet, Action atlasesChanged)
{
    /// <summary>Clears every probe tile, in every layer, so each probe is uncaptured.</summary>
    public void Clear()
    {
        Ensure();
        context.WaitIdle();
        context.SubmitAndWait(probes.Volume.RecordClear);
        probes.SettledOnce = false;
    }

    /// <summary>
    /// For tests that share a renderer: makes the probes' atlases, cascades and scheduler anew, as a new renderer would
    /// have them, so every probe is uncaptured and the scheduler's frames count from 0 again.
    /// </summary>
    public void Reset()
    {
        context.WaitIdle();
        if (probes.Release())
        {
            atlasesChanged();
        }

        Ensure();
    }

    /// <summary>Marks every probe within <paramref name="range"/> of <paramref name="center"/> for an update.</summary>
    public void MarkDirty(Vector3 center, float range) => probes.Batch.Scheduler?.LightChanged(center, range);

    /// <summary>
    /// Writes a probe's tiles as if it had been captured seeing <paramref name="irradiance"/> all round and nothing
    /// within 100 m, through the staging buffer and the blend, so storage can be tested without a capture.
    /// </summary>
    public void WriteTile(ProbeId id, Vector3 irradiance)
    {
        const int irradianceTexels = (ProbeVolume.IrradianceTexels + 2) * (ProbeVolume.IrradianceTexels + 2);
        const int depthTexels = (ProbeVolume.DepthTexels + 2) * (ProbeVolume.DepthTexels + 2);

        Ensure();
        context.WaitIdle();
        using var source = GpuBuffer.CreateHostVisible(context, ProbeVolume.StagedTileBytes, BufferUsageFlags.TransferSrcBit);
        var irradianceTile = new Span<Vector4>(source.Mapped, irradianceTexels);
        irradianceTile.Fill(new Vector4(irradiance, 0f));
        var depthTile = new Span<Vector2>((Vector4*)source.Mapped + irradianceTexels, depthTexels);
        depthTile.Fill(new Vector2(100f, 100f * 100f));

        var volume = probes.Volume;
        context.SubmitAndWait(cmd =>
        {
            var copy = new BufferCopy(0, 0, ProbeVolume.StagedTileBytes);
            context.Api.CmdCopyBuffer(cmd, source.Handle, volume.StagingTiles, 1, &copy);
            volume.RecordBlend(cmd, [id], 1f);
        });
    }

    /// <summary>
    /// The light probes' soft light at each point, facing its normal, from the same probeIrradiance the lighting pass
    /// calls, with the uniforms of the last frame rendered. -1 where no probe can tell, which the lighting turns into the
    /// sky.
    /// </summary>
    public Vector3[] SampleIrradiance(ReadOnlySpan<(Vector3 Point, Vector3 Normal)> samples)
    {
        context.WaitIdle();
        int count = samples.Length;
        ulong bytes = (ulong)Math.Max(count, 1) * 32;
        using var input = GpuBuffer.CreateHostVisible(context, bytes, BufferUsageFlags.StorageBufferBit);
        using var output = GpuBuffer.CreateHostVisible(context, bytes / 2, BufferUsageFlags.StorageBufferBit);
        var points = new Span<Vector4>(input.Mapped, count * 2);
        for (int i = 0; i < count; i++)
        {
            points[i * 2] = new Vector4(samples[i].Point, 1f);
            points[i * 2 + 1] = new Vector4(samples[i].Normal, 0f);
        }

        return SampleProbes(input, output, count);
    }

    // Makes the atlases if they aren't already, and points the sets that read them at them.
    private void Ensure()
    {
        if (probes.Ensure())
        {
            atlasesChanged();
        }
    }

    /// <summary>
    /// Runs probe_sample.comp over <paramref name="count"/> points in <paramref name="input"/>, with a pipeline of its
    /// own, and reads what it wrote to <paramref name="output"/>.
    /// </summary>
    private Vector3[] SampleProbes(GpuBuffer input, GpuBuffer output, int count)
    {
        var setLayout = context.CreateSetLayout(
        [
            new(0, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit),
            new(1, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit),
            new(11, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.ComputeBit),
            new(12, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.ComputeBit),
        ]);
        var pool = context.CreateDescriptorPool(
            1,
            new DescriptorPoolSize(DescriptorType.StorageBuffer, 2),
            new DescriptorPoolSize(DescriptorType.CombinedImageSampler, 2));
        var pipelineLayout = context.CreatePipelineLayout(
            [kit.FrameSetLayout, setLayout],
            new PushConstantRange(ShaderStageFlags.ComputeBit, 0, sizeof(int)));
        var pipeline = PipelineFactory.CreateCompute(context, "probe_sample.comp", pipelineLayout);

        try
        {
            var set = context.AllocateSet(pool, setLayout);
            WriteSampleSet(set, input, output);
            var sets = stackalloc DescriptorSet[] { lastFrameSet(), set };
            context.SubmitAndWait(cmd => RecordSample(cmd, pipeline, pipelineLayout, sets, count));
            return ReadIrradiance(output, count);
        }
        finally
        {
            pipeline.Destroy();
            context.DestroyLayouts(pipelineLayout, pool, setLayout);
        }
    }

    // The irradiance probe_sample.comp wrote for each point, the first three of each four floats.
    private static Vector3[] ReadIrradiance(GpuBuffer output, int count)
    {
        var results = new Vector3[count];
        var read = new ReadOnlySpan<Vector4>(output.Mapped, count);
        for (int i = 0; i < count; i++)
        {
            results[i] = new Vector3(read[i].X, read[i].Y, read[i].Z);
        }

        return results;
    }

    // Points probe_sample.comp's set at the points, the results and the probes' atlases.
    private void WriteSampleSet(DescriptorSet set, GpuBuffer input, GpuBuffer output)
    {
        var inputInfo = new DescriptorBufferInfo(input.Handle, 0, Vk.WholeSize);
        var outputInfo = new DescriptorBufferInfo(output.Handle, 0, Vk.WholeSize);
        var irradianceInfo = new DescriptorImageInfo(kit.LinearSampler, probes.Volume.IrradianceView, ImageLayout.General);
        var depthInfo = new DescriptorImageInfo(kit.LinearSampler, probes.Volume.DepthView, ImageLayout.General);
        var writes = stackalloc WriteDescriptorSet[]
        {
            Descriptors.BufferWrite(set, 0, &inputInfo),
            Descriptors.BufferWrite(set, 1, &outputInfo),
            Descriptors.ImageWrite(set, 11, DescriptorType.CombinedImageSampler, &irradianceInfo),
            Descriptors.ImageWrite(set, 12, DescriptorType.CombinedImageSampler, &depthInfo),
        };
        context.Api.UpdateDescriptorSets(context.Device, 4, writes, 0, null);
    }

    // Runs probe_sample.comp over the points, and makes what it wrote visible to the CPU.
    private void RecordSample(
        CommandBuffer cmd, LivePipeline pipeline, PipelineLayout pipelineLayout, DescriptorSet* sets, int count)
    {
        var api = context.Api;
        int pushed = count;
        api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, pipeline);
        api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, pipelineLayout, 0, 2, sets, 0, null);
        api.CmdPushConstants(cmd, pipelineLayout, ShaderStageFlags.ComputeBit, 0, sizeof(int), &pushed);
        api.CmdDispatch(cmd, (uint)(count + 63) / 64, 1, 1);
        var barrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.ComputeShaderBit,
            SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
            DstStageMask = PipelineStageFlags2.HostBit,
            DstAccessMask = AccessFlags2.HostReadBit,
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            MemoryBarrierCount = 1,
            PMemoryBarriers = &barrier,
        };
        api.CmdPipelineBarrier2(cmd, &dependency);
    }
}
