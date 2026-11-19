using System.Numerics;
using Silk.NET.Vulkan;
using Rendition.Passes;
using Rendition.Vulkan;
using static Rendition.PassStages;

namespace Rendition;

/// <summary>
/// The sun's shadow cascades for a frame: where each one's map lies, what each one draws, and recording them.
/// </summary>
internal sealed unsafe class SunShadows
{
    // How far a simpler model may stray in a shadow. Shadows are soft, so they can afford more than the camera.
    private const float ShadowDetailError = 2f;

    private readonly Vk _api;
    private readonly ShadowPass _pass;
    private readonly MeshStore _meshes;
    private readonly FrameSwitches _switches;
    private readonly DrawItem[][] _draws = new DrawItem[ShadowPass.MaxCascades][];
    private readonly int[] _counts = new int[ShadowPass.MaxCascades];
    private readonly Matrix4x4[] _matrices = new Matrix4x4[ShadowPass.MaxCascades];
    private int _cascades;

    // Whether a sun casts its shadow this frame, so its maps are drawn.
    private bool _drawn;

    public SunShadows(Vk api, ShadowPass pass, MeshStore meshes, FrameSwitches switches)
    {
        _api = api;
        _pass = pass;
        _meshes = meshes;
        _switches = switches;
    }

    /// <summary>
    /// The suns, and the shadow cascades of the first one that casts a shadow, at most
    /// <paramref name="maxCascades"/> of them. Returns whether one does. The sun in light slot
    /// <paramref name="bakedSun"/> is drawn by the bake instead.
    /// </summary>
    public bool Write(FrameUniforms* uniforms, in FrameView view, ReadOnlySpan<Vector4> casters, int maxCascades, int bakedSun)
    {
        int count = Math.Min(view.Lights.Length, FrameUniforms.MaxLights);
        uniforms->CountBlock.LightCount = count;
        _cascades = Math.Min(ShadowCascades.CascadeCount(view.Shadows), maxCascades);
        uniforms->CountBlock.ShadowCascades = _cascades;
        bool castsShadow = false;
        uniforms->ShadowBlock.ShadowDepthScales = Vector4.One;
        for (int i = 0; i < count; i++)
        {
            ref readonly var light = ref view.Lights[i];
            var direction = Vector3.Normalize(light.Direction);
            bool casts = !castsShadow && light.CastsShadow && view.Shadows.Radius > 0f && !_switches.Off.HasFlag(RenderEffects.SunShadows);
            if (casts)
            {
                uniforms->ShadowBlock.ShadowRadii = FitCascades(view, direction, casters, out uniforms->ShadowBlock.ShadowFade, out uniforms->ShadowBlock.ShadowDepthScales);
                uniforms->ShadowBlock.ShadowFade.W = ShadowCascades.ContactLength(view.Shadows, _switches.Off);
                for (int cascade = 0; cascade < _cascades; cascade++)
                {
                    uniforms->CameraBlock.ShadowMatrices[cascade] = _matrices[cascade];
                }

                castsShadow = true;
            }

            uniforms->Lights[i] = new GpuLight
            {
                Direction = new Vector4(direction, casts ? 1f : 0f),
                Radiance = new Vector4(light.Radiance, i == bakedSun ? 0f : -1f),
            };
        }

        // With no sun shadow there are no maps to look up, so nothing walks the cascades.
        uniforms->CountBlock.ShadowCascades = castsShadow ? _cascades : 0;
        _drawn = castsShadow;
        return castsShadow;
    }

    /// <summary>
    /// Picks each cascade's draws when a sun casts a shadow, and returns how many there are in all. A draw near swapping
    /// to a finer version is drawn twice, cross-fading, so every cascade has room for two of each.
    /// </summary>
    public int Select(in FrameView view, ReadOnlySpan<DrawItem> draws, bool castsShadow, float meshDetailScale)
    {
        if (_draws[0] is null || _draws[0].Length < draws.Length * 2)
        {
            for (int i = 0; i < _draws.Length; i++)
            {
                _draws[i] = new DrawItem[draws.Length * 2];
            }
        }

        Array.Clear(_counts);
        if (!castsShadow)
        {
            return 0;
        }

        int shadowCount = 0;
        for (int cascade = 0; cascade < _cascades; cascade++)
        {
            float texelsPerUnit = _pass.Resolution / (2f * ShadowCascades.CascadeRadius(view.Shadows, cascade));
            var shadow = new DetailView(
                _matrices[cascade], view.CameraPosition, 0f, texelsPerUnit, _switches.DetailError(ShadowDetailError, meshDetailScale), cull: _switches.Culls);
            _counts[cascade] = DrawSelection.Select(shadow, draws, _meshes.DetailSlots, _draws[cascade], _switches.WindReach, fades: _switches.FadeRules);
            shadowCount += _counts[cascade];
        }

        return shadowCount;
    }

    /// <summary>The triangles the cascades draw this frame.</summary>
    public long Triangles()
    {
        long triangles = 0;
        for (int cascade = 0; cascade < _cascades; cascade++)
        {
            triangles += DrawSelection.Triangles(_draws[cascade].AsSpan(0, _counts[cascade]));
        }

        return triangles;
    }

    /// <summary>
    /// Draws the sun's shadow cascades. Targets are shared between frames in flight, so each first use waits for the
    /// previous frame's sampling to finish. Contents are discarded via Undefined. When no sun casts a shadow the maps
    /// aren't drawn, or even cleared, and nothing reads them, but the lighting pass still binds them.
    /// </summary>
    public void Record(CommandBuffer cmd, DescriptorSet frameSet, InstanceBuffer instances)
    {
        var shadowMap = _pass.Target.Image;
        if (_drawn)
        {
            _api.Transition(cmd, new ImageTransition(shadowMap, ImageAspectFlags.DepthBit,
                ImageLayout.Undefined, ImageLayout.DepthAttachmentOptimal) { LayerCount = ShadowPass.MaxCascades },
                (ShadowReadStages, SampledAccess), (DepthStages, DepthWrite | AccessFlags2.DepthStencilAttachmentReadBit));
            for (int cascade = 0; cascade < _cascades; cascade++)
            {
                var casters = _draws[cascade].AsSpan(0, _counts[cascade]);
                _pass.Record(cmd, frameSet, new SceneGeometry(casters, _meshes.MeshSlots, _meshes.DetailSlots, instances), cascade);
            }

            _api.Transition(cmd, new ImageTransition(shadowMap, ImageAspectFlags.DepthBit,
                ImageLayout.DepthAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal) { LayerCount = ShadowPass.MaxCascades },
                (DepthStages, DepthWrite), (ShadowReadStages, SampledAccess));
        }
        else
        {
            _api.Transition(cmd, new ImageTransition(shadowMap, ImageAspectFlags.DepthBit,
                ImageLayout.Undefined, ImageLayout.ShaderReadOnlyOptimal) { LayerCount = ShadowPass.MaxCascades },
                (ShadowReadStages, SampledAccess), (ShadowReadStages, SampledAccess));
        }
    }

    /// <summary>
    /// Places each cascade's map. In a perspective view each ball takes the next slice of
    /// what the camera sees, starting at the camera, so none of it is spent behind. Its radius
    /// never changes as the camera turns, and its centre moves in whole texels, so edges don't
    /// shimmer. A parallel view keeps every cascade centred on the settings' centre. Returns
    /// each cascade's radius, and how shadows fade out with distance.
    /// </summary>
    private Vector4 FitCascades(
        in FrameView view, Vector3 direction, ReadOnlySpan<Vector4> casters, out Vector4 fade, out Vector4 depthScales)
    {
        var radii = Vector4.Zero;
        depthScales = Vector4.One;
        float? spread = ShadowCascades.Spread(view.Projection);
        Matrix4x4.Invert(view.View, out var world);
        var eye = world.Translation;
        var forward = Vector3.Normalize(-new Vector3(world.M31, world.M32, world.M33));
        float start = 0f;
        for (int cascade = 0; cascade < _cascades; cascade++)
        {
            float radius = ShadowCascades.CascadeRadius(view.Shadows, cascade);
            var center = view.Shadows.Center;
            float reach = radius;
            if (spread is { } opening)
            {
                (float along, start) = ShadowCascades.FitSlice(start, radius, opening);
                center = eye + forward * along;

                // Reach towards the sun at least as far as a cascade round the target would,
                // so tall things out of view, like the edge of a wood in a low sun, still cast.
                reach = radius + MathF.Max(Vector3.Dot(view.Shadows.Center - center, direction), 0f);

                // And far enough to take in whatever stands up-sun of it, like a tree out of
                // frame whose shade falls on a wall in it.
                reach = ShadowCascades.CasterReach(center, radius, direction, reach, casters);
            }

            _matrices[cascade] = ShadowCascades.CreateMatrix(direction, center, radius, reach, _pass.Resolution);
            radii[cascade] = radius;
            depthScales[cascade] = ShadowCascades.DepthScale(radius, reach);
        }

        // The last slice ends where shadows do, and they fade out over its last part rather
        // than stopping at a line. A parallel view has no such end, so only the map's edge fades.
        fade = spread is null
            ? new Vector4(float.MaxValue, float.MaxValue, ShadowPass.HandOver, 0f)
            : new Vector4(start * (1f - ShadowPass.FadeShare), start, ShadowPass.HandOver, 0f);
        return radii;
    }
}
