using System.Numerics;
using System.Runtime.InteropServices;
using Rendition.Passes;
using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// The physical sky's tables, after Hillaire 2020: transmittance, multiple scattering and the sky seen from the ground.
/// The first two hang on the turbidity alone and are redrawn only when it changes; the sky-view table is redrawn each
/// time the sun moves, and Shaders/environment.comp draws the sky's cubes from it. All three shaders share one set:
/// binding 0 is the table written, 1 and 2 the transmittance and multiple scattering read.
/// </summary>
internal sealed unsafe class SkyAtmosphere : IDisposable
{
    private const Format TableFormat = Format.R16G16B16A16Sfloat;

    /// <summary>Must match the Air block in Shaders/atmosphere_*.comp.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct AirConstants
    {
        /// <summary>xyz: towards the sun.</summary>
        public Vector4 Sun;

        /// <summary>x: the aerosol's scattering per kilometre at the ground; y: its extinction.</summary>
        public Vector4 Aerosol;
    }

    /// <summary>One table: the image, the shader that draws it, its set and its size.</summary>
    private readonly record struct Table(RenderTarget Target, LivePipeline Shader, DescriptorSet Set, Extent2D Size);

    private readonly VulkanContext _context;
    private readonly DescriptorSetLayout _setLayout;
    private readonly DescriptorPool _pool;
    private readonly PipelineLayout _pipelineLayout;
    private readonly Table _transmittance;
    private readonly Table _multiscatter;
    private readonly Table _skyView;
    private float _turbidity = float.NaN;

    /// <summary>The sky seen from just above the ground, in the renderer's units, ready to sample after a record.</summary>
    public ImageView SkyView => _skyView.Target.View;

    public SkyAtmosphere(VulkanContext context, Sampler sampler)
    {
        _context = context;
        _setLayout = context.CreateSetLayout(
        [
            new(0, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit),
            new(1, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.ComputeBit),
            new(2, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.ComputeBit),
        ]);
        _pool = context.CreateDescriptorPool(3,
            new DescriptorPoolSize(DescriptorType.StorageImage, 3),
            new DescriptorPoolSize(DescriptorType.CombinedImageSampler, 6));
        _pipelineLayout = context.CreatePipelineLayout(
            [_setLayout],
            new PushConstantRange(ShaderStageFlags.ComputeBit, 0, (uint)sizeof(AirConstants)));

        // The sizes must match TRANSMITTANCE_SIZE, MULTISCATTER_SIZE and SKY_VIEW_SIZE in Shaders/atmosphere.glsl.
        // A shader that doesn't read a binding never touches it, so those are left unwritten.
        _transmittance = CreateTable(new Extent2D(256, 64), "atmosphere_transmittance.comp", null, null);
        var reads = new DescriptorImageInfo(sampler, _transmittance.Target.View, ImageLayout.ShaderReadOnlyOptimal);
        _multiscatter = CreateTable(new Extent2D(32, 32), "atmosphere_multiscatter.comp", reads, null);
        var scattered = new DescriptorImageInfo(sampler, _multiscatter.Target.View, ImageLayout.ShaderReadOnlyOptimal);
        _skyView = CreateTable(new Extent2D(192, 108), "atmosphere_skyview.comp", reads, scattered);

        // The cubes' shader binds the sky-view even for a sky that never draws it, so it starts out ready to sample.
        context.SubmitAndWait(cmd => context.Api.Transition(cmd, new ImageTransition(_skyView.Target.Image, ImageAspectFlags.ColorBit,
            ImageLayout.Undefined, ImageLayout.ShaderReadOnlyOptimal),
            (PipelineStageFlags2.None, AccessFlags2.None),
            (PipelineStageFlags2.AllCommandsBit, AccessFlags2.ShaderSampledReadBit)));
    }

    /// <summary>
    /// Records the tables for this sky, leaving the sky-view ready for any shader to sample. Nothing is drawn for a
    /// sky without a turbidity, whose cubes don't read the tables.
    /// </summary>
    public void Record(CommandBuffer cmd, in SkySun sky)
    {
        if (!(sky.Turbidity > 0f))
        {
            return;
        }

        var (scattering, extinction) = Atmosphere.Aerosol(sky.Turbidity);
        var constants = new AirConstants
        {
            Sun = new Vector4(sky.Direction, 0f),
            Aerosol = new Vector4(scattering, extinction, 0f, 0f),
        };

        if (sky.Turbidity != _turbidity)
        {
            _turbidity = sky.Turbidity;
            Draw(cmd, _transmittance, constants);
            Draw(cmd, _multiscatter, constants);
        }

        Draw(cmd, _skyView, constants);
    }

    private Table CreateTable(Extent2D size, string shader, DescriptorImageInfo? transmittance, DescriptorImageInfo? multiscatter)
    {
        var target = new RenderTarget(_context, TableFormat, size, ImageUsageFlags.StorageBit, ImageAspectFlags.ColorBit);
        var set = _context.AllocateSet(_pool, _setLayout);
        var targetInfo = new DescriptorImageInfo(default, target.View, ImageLayout.General);
        var transmittanceInfo = transmittance.GetValueOrDefault();
        var multiscatterInfo = multiscatter.GetValueOrDefault();
        var writes = stackalloc WriteDescriptorSet[3];
        uint count = 0;
        writes[count++] = Descriptors.ImageWrite(set, 0, DescriptorType.StorageImage, &targetInfo);
        if (transmittance.HasValue)
        {
            writes[count++] = Descriptors.ImageWrite(set, 1, DescriptorType.CombinedImageSampler, &transmittanceInfo);
        }

        if (multiscatter.HasValue)
        {
            writes[count++] = Descriptors.ImageWrite(set, 2, DescriptorType.CombinedImageSampler, &multiscatterInfo);
        }

        _context.Api.UpdateDescriptorSets(_context.Device, count, writes, 0, null);
        return new Table(target, PipelineFactory.CreateCompute(_context, shader, _pipelineLayout), set, size);
    }

    /// <summary>Writes the whole of one table, waiting for earlier readers, and leaves it ready to sample.</summary>
    private void Draw(CommandBuffer cmd, in Table table, AirConstants constants)
    {
        var api = _context.Api;
        var image = table.Target.Image;
        var set = table.Set;
        api.Transition(cmd, new ImageTransition(image, ImageAspectFlags.ColorBit, ImageLayout.Undefined, ImageLayout.General),
            (PipelineStageFlags2.AllCommandsBit, AccessFlags2.ShaderSampledReadBit),
            (PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit));

        api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, table.Shader);
        api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _pipelineLayout, 0, 1, &set, 0, null);
        api.CmdPushConstants(cmd, _pipelineLayout, ShaderStageFlags.ComputeBit, 0, (uint)sizeof(AirConstants), &constants);
        api.CmdDispatch(cmd, (table.Size.Width + 7) / 8, (table.Size.Height + 7) / 8, 1);

        api.Transition(cmd, new ImageTransition(image, ImageAspectFlags.ColorBit, ImageLayout.General, ImageLayout.ShaderReadOnlyOptimal),
            (PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit),
            (PipelineStageFlags2.AllCommandsBit, AccessFlags2.ShaderSampledReadBit));
    }

    public void Dispose()
    {
        foreach (var table in (ReadOnlySpan<Table>)[_skyView, _multiscatter, _transmittance])
        {
            table.Shader.Destroy();
        }

        _context.Api.DestroyPipelineLayout(_context.Device, _pipelineLayout, null);
        _context.Api.DestroyDescriptorPool(_context.Device, _pool, null);
        _context.Api.DestroyDescriptorSetLayout(_context.Device, _setLayout, null);
        foreach (var table in (ReadOnlySpan<Table>)[_skyView, _multiscatter, _transmittance])
        {
            table.Target.Dispose();
        }
    }
}
