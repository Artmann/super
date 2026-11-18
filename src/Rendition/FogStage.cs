using System.Globalization;
using System.Numerics;
using Silk.NET.Vulkan;
using Rendition.Passes;
using Rendition.Vulkan;

namespace Rendition;

/// <summary>
/// The air and the water the camera sees through: the volumetric fog's grid and fog volumes, and the water the camera
/// may be under.
/// </summary>
internal sealed unsafe class FogStage
{
    private readonly VulkanContext _context;
    private readonly FrameSwitches _switches;
    private readonly VolumetricFogVolume _volume;

    // Whether the frame being recorded draws the volumetric fog.
    private bool _drawn;

    public FogStage(VulkanContext context, DescriptorKit kit, FrameSwitches switches)
    {
        _context = context;
        _switches = switches;
        _volume = new VolumetricFogVolume(context, kit.FrameSetLayout, kit.LinearSampler);
    }

    /// <summary>The volumetric fog's summed volume, or the texel that stands in until it's made.</summary>
    public ImageView Result => _volume.Result;

    /// <summary>
    /// Makes the volumetric fog's volumes the first time a frame draws it, frees them while it's switched off, and
    /// remakes them when the cell size or slice count changes. Each waits for the GPU, which is rare. Returns whether
    /// the volumes changed, so the sets that read them must be written again.
    /// </summary>
    public bool KeepTargets(in FrameView view, Extent2D scene, (int CellSize, int Slices) grid)
    {
        int width = (int)scene.Width;
        int height = (int)scene.Height;
        if (!_volume.HasTargets && VolumetricFog.Pack(view.Weather.Fog, VolumesOf(view).Length, _switches.Off, _switches.UnderwaterReach(view)).X > 0f)
        {
            // The fog's volumes are made the first time it's drawn. Frames in flight bind the stand-in.
            _context.WaitIdle();
            _volume.SetSize(width, height, grid.CellSize, grid.Slices);
            return true;
        }

        if (_volume.HasTargets && VolumetricFog.CanFreeTargets(_switches.Off, _switches.UnderwaterReach(view)))
        {
            // Switched off, the volumes are freed, and made again with no history if it's switched back on.
            // Frames in flight still read them. Flipping the switch is rare.
            _context.WaitIdle();
            _volume.Release();
            return true;
        }

        if (_volume.HasTargets && _volume.Size != VolumetricFog.GridSize(width, height, grid.CellSize, grid.Slices))
        {
            // A new cell size or slice count remakes the grid, with no history. Frames in flight still read the old one.
            _context.WaitIdle();
            _volume.SetSize(width, height, grid.CellSize, grid.Slices);
            return true;
        }

        return false;
    }

    /// <summary>Makes the volumes again at the size of new targets, when there are any.</summary>
    public void Resize(Extent2D scene, (int CellSize, int Slices) grid)
    {
        if (_volume.HasTargets)
        {
            _volume.SetSize((int)scene.Width, (int)scene.Height, grid.CellSize, grid.Slices);
        }
    }

    /// <summary>
    /// Fills the volumetric fog's part of the frame block and writes the fog volumes it draws, and decides whether
    /// this frame draws the fog at all.
    /// </summary>
    public void Write(FrameUniforms* uniforms, in FrameView view, bool historyValid, (int CellSize, int Slices) grid)
    {
        var fogVolumes = VolumesOf(view);
        var volumetric = VolumetricFog.Pack(view.Weather.Fog, fogVolumes.Length, _switches.Off, _switches.UnderwaterReach(view));
        if (!_volume.HasTargets)
        {
            volumetric = Vector4.Zero;
        }

        volumetric.W = historyValid && _volume.HistoryReady ? 1f : 0f;
        uniforms->WeatherBlock.Volumetric = volumetric;
        _drawn = volumetric.X > 0f;
        uniforms->WeatherBlock.VolumetricGrid = new Vector4(grid.Slices, grid.CellSize, _drawn ? fogVolumes.Length : 0, 0f);
        if (_drawn)
        {
            _volume.WriteFogVolumes(fogVolumes);
        }
        else
        {
            // A frame without the fog leaves nothing for the next to carry on from.
            _volume.Skip();
        }
    }

    /// <summary>The water the camera is under, or none above it.</summary>
    public void WriteWater(FrameUniforms* uniforms, in FrameView view)
    {
        var water = _switches.Underwater(view) ? view.Weather.Water : default;
        uniforms->WaterBlock.WaterAbsorption = new Vector4(water.Absorption, water.Absorption != Vector3.Zero ? 1f : 0f);
        uniforms->WaterBlock.WaterShallow = new Vector4(water.ShallowColor, water.SurfaceHeight);
        uniforms->WaterBlock.WaterDeep = new Vector4(water.DeepColor, water.ShaftStrength);
        uniforms->WaterBlock.WaterLight = new Vector4(water.CausticStrength, water.CausticScale, water.ShaftDistance, 0f);
    }

    /// <summary>Fills the fog's air, once the shadow maps and the light lists are there, when the frame draws it.</summary>
    public void Record(CommandBuffer cmd, DescriptorSet frameSet)
    {
        if (_drawn)
        {
            _volume.Record(cmd, frameSet);
        }
    }

    /// <summary>Points the fog at the sun's shadow maps, the sky behind it and the light probes.</summary>
    public void SetSources(DescriptorImageInfo shadow, DescriptorImageInfo background, (DescriptorImageInfo Irradiance, DescriptorImageInfo Depth) probes) =>
        _volume.SetSources(shadow, background, probes.Irradiance, probes.Depth);

    public void Dispose() => _volume.Dispose();

    /// <summary>
    /// The view's fog volumes, or none when they're switched off. Throws for too many, or for one that can't be
    /// drawn, as <see cref="FogVolumeBatch.Check"/> says.
    /// </summary>
    private ReadOnlySpan<FogVolumeDraw> VolumesOf(in FrameView view)
    {
        var volumes = _switches.Off.HasFlag(RenderEffects.FogVolumes) ? [] : view.Extras.FogVolumes;
        if (volumes.Length > FogVolumeBatch.MaxVolumes)
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture,
                $"A frame can draw at most {FogVolumeBatch.MaxVolumes} fog volumes, but it was given {volumes.Length}."), nameof(view));
        }

        foreach (ref readonly var volume in volumes)
        {
            FogVolumeBatch.Check(volume.Shape, volume.Transform, volume.Settings);
        }

        return volumes;
    }
}
