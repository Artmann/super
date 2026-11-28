using System.Numerics;
using System.Runtime.InteropServices;
using Rendition.Gpu;
using Rendition.Gpu.Vulkan;
using Rendition.Passes;
using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// The physical sky's tables, after Hillaire 2020: transmittance, multiple scattering and the sky seen from the ground.
/// The first two hang on the turbidity alone and are redrawn only when it changes; the sky-view table is redrawn each
/// time the sun moves, and Shaders/environment.comp draws the sky's cubes from it. Each table is drawn by a compute pass
/// of the GPU layer and rests in <see cref="ImageLayout.General"/>, where its readers sample it.
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

    /// <summary>One table: the image, the pass that draws it, its group and its size.</summary>
    private readonly record struct Table(RenderTarget Target, ComputePass Pass, ImageGroup Group, Extent2D Size);

    private readonly VulkanGpu _gpu;
    private readonly Table _transmittance;
    private readonly Table _multiscatter;
    private readonly Table _skyView;
    private readonly CommandList _commands = new();
    private float _turbidity = float.NaN;

    /// <summary>The sky seen from just above the ground, in the renderer's units, which rests in <see cref="ImageLayout.General"/>.</summary>
    public ImageView SkyView => _skyView.Target.View;

    public SkyAtmosphere(VulkanContext context, VulkanGpu gpu)
    {
        _gpu = gpu;
        var sampler = gpu.CreateSampler(new SamplerDescription(SamplerFilter.Linear));

        // The sizes must match TRANSMITTANCE_SIZE, MULTISCATTER_SIZE and SKY_VIEW_SIZE in Shaders/atmosphere.glsl.
        _transmittance = CreateTable(context, (new Extent2D(256, 64), "atmosphere_transmittance.comp"), sampler);
        _multiscatter = CreateTable(context, (new Extent2D(32, 32), "atmosphere_multiscatter.comp"), sampler);
        _skyView = CreateTable(context, (new Extent2D(192, 108), "atmosphere_skyview.comp"), sampler);
        var transmittance = new GroupImage("transmittance", _transmittance.Target.View, Use: GroupImageUse.Stored);
        _transmittance.Group.Make(_transmittance.Pass.Pipeline, 0, [Target(_transmittance)]);
        _multiscatter.Group.Make(_multiscatter.Pass.Pipeline, 0, [Target(_multiscatter), transmittance]);
        _skyView.Group.Make(_skyView.Pass.Pipeline, 0,
            [Target(_skyView), transmittance, new("multiscatter", _multiscatter.Target.View, Use: GroupImageUse.Stored)]);

        // The tables rest in General from the start, and the cubes' shader binds the sky-view even for a sky that
        // never draws it.
        context.SubmitAndWait(cmd =>
        {
            foreach (var table in (ReadOnlySpan<Table>)[_transmittance, _multiscatter, _skyView])
            {
                context.Api.Transition(cmd, new ImageTransition(table.Target.Image, ImageAspectFlags.ColorBit,
                    ImageLayout.Undefined, ImageLayout.General),
                    (PipelineStageFlags2.None, AccessFlags2.None),
                    (PipelineStageFlags2.AllCommandsBit, AccessFlags2.ShaderSampledReadBit | AccessFlags2.ShaderStorageWriteBit));
            }
        });
    }

    /// <summary>
    /// Records the tables for this sky, which the layer's barrier after each pass makes ready for any shader to
    /// sample. Nothing is drawn for a sky without a turbidity, whose cubes don't read the tables.
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

        _commands.Reset();
        if (sky.Turbidity != _turbidity)
        {
            _turbidity = sky.Turbidity;
            Draw(_transmittance, constants);
            Draw(_multiscatter, constants);
        }

        Draw(_skyView, constants);
        _gpu.Play(_commands, cmd);
    }

    private static GroupImage Target(in Table table) => new("target", table.Target.View, Use: GroupImageUse.Written);

    private Table CreateTable(VulkanContext context, (Extent2D Size, string Shader) table, SamplerId sampler)
    {
        var target = new RenderTarget(context, TableFormat, table.Size, ImageUsageFlags.StorageBit, ImageAspectFlags.ColorBit);
        return new Table(target, new ComputePass(_gpu, table.Shader), new ImageGroup(_gpu, sampler), table.Size);
    }

    /// <summary>Writes the whole of one table.</summary>
    private void Draw(in Table table, AirConstants constants) =>
        table.Pass.Record(_commands, [table.Group.Id], new ReadOnlySpan<byte>(&constants, sizeof(AirConstants)),
            ((int)(table.Size.Width + 7) / 8, (int)(table.Size.Height + 7) / 8, 1));

    public void Dispose()
    {
        foreach (var table in (ReadOnlySpan<Table>)[_skyView, _multiscatter, _transmittance])
        {
            table.Group.Dispose();
            table.Pass.Dispose();
            table.Target.Dispose();
        }
    }
}
