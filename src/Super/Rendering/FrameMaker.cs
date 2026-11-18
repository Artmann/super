using System.Numerics;
using Rendition;

namespace Super;

/// <summary>
/// Turns a <see cref="View"/> into the <see cref="FrameView"/> Rendition draws: the camera, the suns, the sky, the
/// effects and what <see cref="DressedDraws"/> and <see cref="DecalMaterials"/> made for the frame. It also keeps the
/// renderer's environment in step with the colours around the view.
/// </summary>
internal sealed class FrameMaker
{
    private const int MaxLights = 8;

    private readonly Renderer _renderer;
    private readonly DressedDraws _draws;
    private readonly DecalMaterials _decals;
    private readonly DirectionalLight[] _lights = new DirectionalLight[MaxLights];

    /// <summary>Kept, since asking for a new one each frame would be garbage.</summary>
    private readonly Func<SkyImage, SkyImageHandle> _skyOf;

    private SkyBands _bakedBands = ViewSky.Studio;
    private Content? _content;

    /// <summary>Starts the renderer's environment as the studio's grey room.</summary>
    public FrameMaker(Renderer renderer, UploadedItems items, DressedDraws draws, DecalMaterials decals)
    {
        _renderer = renderer;
        _draws = draws;
        _decals = decals;
        _skyOf = photographed => items.Require<SkyOnGpu>(photographed.Index, _content!).Handle;
        SetEnvironment(ViewSky.Studio);
    }

    /// <summary>
    /// Surfaces pick up the color of the water or sky around them. Baking is slow, so it only happens when the colors
    /// change.
    /// </summary>
    public void KeepEnvironment(View view, RenderEffects off)
    {
        var bands = ViewSky.SurroundingColors(view, off);
        if (bands != _bakedBands)
        {
            SetEnvironment(bands);
            _bakedBands = bands;
        }
    }

    /// <summary>The frame the view asks for, at the request's size and time.</summary>
    public FrameView Create(View view, Content content, FrameRequest request, RenderEffects off, DebugView debugView)
    {
        var shot = view.Shot;
        float aspect = request.Width / (float)Math.Max(request.Height, 1);
        var frame = new FrameView
        {
            Width = request.Width,
            Height = request.Height,
            View = Matrix4x4.CreateLookAt(shot.Position, shot.Target, shot.Up),
            Projection = shot.Matrix(aspect),
            CameraPosition = shot.Eye,
            Time = request.Time,
            DebugView = debugView,
            Lights = Lights(view.Suns),
            Bones = _draws.Bones,
            Shadows = FrameShadows(view, aspect),
            Occlusion = view.Occlusion,
            Bounce = view.Bounce,
            Outlines = view.OutlineSettings,
            LightProbes = view.LightProbeVolume,
            BakedSpace = view.BakedSpace,
            BakedChunks = view.PlacedBakedChunks,
            Post = view.Post,
            Water = view.WaterSettings,
            Puddles = view.PuddleSettings,
            Wind = view.WindSettings,
            Overlay = view.Ui is { } ui ? ui.Quads : [],
            Particles = view.DrawnParticles,
            Decals = _decals.Drawn,
            Transition = view.Transition,
            Off = off,
            LocalLights = view.DrawnLights,
            FogVolumes = off.HasFlag(RenderEffects.FogVolumes) || off.HasFlag(RenderEffects.VolumetricFog) ? default : view.DrawnFogVolumes,
        };
        _content = content;
        return WithSky(frame, view, off);
    }

    /// <summary>Which sun casts the shadows: the one marked to, or else the first.</summary>
    internal static int ShadowSun(ReadOnlySpan<SunLight> suns)
    {
        for (int i = 0; i < suns.Length; i++)
        {
            if (suns[i].CastsShadows)
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary>The sun's shadows as the view asks for them, round its camera's target, for a picture this wide over its height.</summary>
    internal static ShadowSettings FrameShadows(View view, float aspect)
    {
        var shot = view.Shot;
        return new ShadowSettings
        {
            Center = shot.Target,
            Radius = shot.ShadowRadius(view.ShadowRadius, aspect),
            Cascades = view.ShadowCascades,
            LightAngle = view.SunSize,
            ContactLength = view.ShadowContact,
        };
    }

    /// <summary>The frame with the view's sky: the environment's brightness, the clear sky, the background and its haze, and the fog.</summary>
    private FrameView WithSky(FrameView frame, View view, RenderEffects off)
    {
        var sky = view.SkySettings;
        return frame with
        {
            EnvironmentIntensity = sky is null ? 0.8f : sky.Value.Brightness > 0f ? sky.Value.Brightness : 1f,
            Sky = ViewSky.FrameSky(view, off, _skyOf),
            BackgroundRadiance = new Vector3(0.15f, 0.16f, 0.18f),
            ShowEnvironment = sky is not null,
            BackgroundFadeStart = sky?.HazeStart ?? 0f,
            BackgroundFadeEnd = sky?.HazeEnd ?? 0f,
            BackgroundFadeOrigin = ViewSky.HazeOrigin(sky, view.Shot),
            BackgroundFadeColor = sky?.HazeColor ?? Vector3.Zero,
            Fog = ViewSky.FrameFog(sky),
        };
    }

    /// <summary>The view's suns as the frame's lights, at most <see cref="MaxLights"/>, with one casting the shadows.</summary>
    private ReadOnlySpan<DirectionalLight> Lights(ReadOnlySpan<SunLight> suns)
    {
        int count = Math.Min(suns.Length, MaxLights);
        int shadowSun = ShadowSun(suns);
        for (int i = 0; i < count; i++)
        {
            _lights[i] = new DirectionalLight
            {
                Direction = suns[i].Direction,
                Radiance = suns[i].Color,
                CastsShadow = i == shadowSun,
                Mode = suns[i].Mode,
            };
        }

        return _lights.AsSpan(0, count);
    }

    private void SetEnvironment(SkyBands bands) => _renderer.SetEnvironment(new EnvironmentDescription
    {
        Ceiling = bands.Ceiling,
        Horizon = bands.Horizon,
        Floor = bands.Floor,
    });
}
