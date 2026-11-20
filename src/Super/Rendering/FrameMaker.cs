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

    private readonly UploadedItems _items;
    private SkyBands _bakedBands = ViewSky.Studio;
    private Content? _content;

    /// <summary>Starts the renderer's environment as the studio's grey room.</summary>
    public FrameMaker(Renderer renderer, UploadedItems items, DressedDraws draws, DecalMaterials decals)
    {
        _renderer = renderer;
        _draws = draws;
        _decals = decals;
        _items = items;
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
            Picture = new PictureSettings
            {
                DebugView = debugView,
                Post = WithRendererItems(view.Effects.Post, content),
                Transition = view.Effects.Transition,
                LensFlare = view.Lights.LensFlareSettings,
                Paint = view.Effects.Look.Paint,
            },
            Lights = Lights(view.Lights.Suns),
            Bones = _draws.Bones,
            Shadows = FrameShadows(view, aspect),
            ScreenSpace = new ScreenSpaceSettings
            {
                Occlusion = view.Indirect.Occlusion,
                Bounce = view.Indirect.Bounce,
                Outlines = view.Effects.OutlineSettings,
            },
            LightProbes = view.Indirect.LightProbeVolume,
            Baked = new BakedAreas { Space = view.Indirect.BakedSpace, Chunks = view.Indirect.PlacedBakedChunks },
            Extras = Extras(view, off),
            Off = off,
            LocalLights = view.Lights.DrawnLights,
        };
        _content = content;
        return WithSky(frame, view, off);
    }

    /// <summary>
    /// The finish with its colour table and its paper, which the game names by their content index, as the renderer
    /// numbers them.
    /// </summary>
    private PostSettings WithRendererItems(in PostSettings post, Content content)
    {
        var graded = post;
        if (post.Grade.Lut is { } named)
        {
            graded.Grade.Lut = _items.Require<LutOnGpu>(named.Index, content).Handle;
        }

        if (post.Screen.Canvas is { } paper)
        {
            graded.Screen.Canvas = _items.Require<TextureOnGpu>(paper.Index, content).Handle;
        }

        return graded;
    }

    /// <summary>Which sun casts the shadows: the one marked to, or else the first.</summary>
    internal static int ShadowSun(ReadOnlySpan<Sun> suns)
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
            Radius = shot.ShadowRadius(view.Lights.ShadowRadius, aspect),
            Cascades = view.Lights.ShadowCascades,
            LightAngle = view.Lights.SunSize,
            ContactLength = view.Lights.ShadowContact,
        };
    }

    /// <summary>The frame with the view's sky: the environment's brightness, the clear sky, the background and its haze, and the weather: fog, water, puddles and wind.</summary>
    private FrameView WithSky(FrameView frame, View view, RenderEffects off)
    {
        var sky = view.Surroundings.SkySettings;
        return frame with
        {
            Environment = new EnvironmentSettings
            {
                Intensity = sky is null ? 0.8f : sky.Value.Brightness > 0f ? sky.Value.Brightness : 1f,
                Sky = ViewSky.FrameSky(view, off, _skyOf),
                ShowAsBackground = sky is not null,
            },
            Background = new BackgroundSettings
            {
                Radiance = new Vector3(0.15f, 0.16f, 0.18f),
                FadeStart = sky?.HazeStart ?? 0f,
                FadeEnd = sky?.HazeEnd ?? 0f,
                FadeOrigin = ViewSky.HazeOrigin(sky, view.Shot),
                FadeColor = sky?.HazeColor ?? Vector3.Zero,
            },
            Weather = new WeatherSettings
            {
                Fog = ViewSky.FrameFog(sky),
                Water = view.Surroundings.WaterSettings,
                Puddles = view.Surroundings.PuddleSettings,
                Wind = view.Surroundings.WindSettings,
            },
        };
    }

    /// <summary>What the frame draws besides its draws: the view's overlay, particles, decals and fog volumes.</summary>
    private FrameExtras Extras(View view, RenderEffects off) => new()
    {
        Overlay = view.Ui is { } ui ? ui.Quads : [],
        Particles = view.Effects.DrawnParticles,
        Decals = _decals.Drawn,
        FogVolumes = off.HasFlag(RenderEffects.FogVolumes) || off.HasFlag(RenderEffects.VolumetricFog) ? default : view.Effects.DrawnFogVolumes,
    };

    /// <summary>The view's suns as the frame's lights, at most <see cref="MaxLights"/>, with one casting the shadows.</summary>
    private ReadOnlySpan<DirectionalLight> Lights(ReadOnlySpan<Sun> suns)
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
