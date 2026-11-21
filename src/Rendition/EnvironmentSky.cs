using System.Numerics;
using Silk.NET.Vulkan;
using Rendition.Passes;
using Rendition.Vulkan;

namespace Rendition;

/// <summary>
/// The environment surfaces reflect and the sky behind it: the description the game set, the photographed skies it
/// made, and the clear sky the environment was last drawn with, redrawn in a frame when either changes.
/// </summary>
internal sealed class EnvironmentSky
{
    private readonly VulkanContext _context;
    private readonly FrameRing _ring;

    // The photographed skies made with Create, by handle, each with its sun and a hash of what it holds.
    private readonly List<(GpuTexture Cube, SkyImageSun Sun, ulong Content)> _images = [];

    // What the last Set gave, hashed, for Hash.
    private ulong _content;

    // The environment intensity the last Follow drew at, and a hash of its view's clouds, drawn or not, for Hash.
    private (float Intensity, ulong Clouds) _followed;

    /// <summary>The sky or the environment's description changed and its cubes aren't redrawn yet.</summary>
    private bool _pending;

    /// <param name="weather">The clouds' weather map and its repeating sampler, for the clouds the captures draw.</param>
    public EnvironmentSky(VulkanContext context, FrameRing ring, (Sampler Repeat, ImageView View) weather)
    {
        _context = context;
        _ring = ring;
        Map = new EnvironmentMap(context, weather);
        Set(new EnvironmentDescription
        {
            Ceiling = new Vector3(0.5f),
            Horizon = new Vector3(0.4f),
            Floor = new Vector3(0.2f),
        });
        Flush();
        Sh = new SkySh(context, Map.Sky, Map.Sampler);
    }

    /// <summary>The environment's cubes.</summary>
    public EnvironmentMap Map { get; }

    /// <summary>The sky cube projected to L1, which relit chunks read.</summary>
    public SkySh Sh { get; }

    /// <summary>Goes up with every <see cref="Set"/>, so the light probes see the sky has changed.</summary>
    public int Version { get; private set; }

    /// <summary>
    /// The clear sky and sun the environment was last drawn with. The lighting hashes cover what decides it through
    /// the view, so drawing it again leaves the version alone.
    /// </summary>
    public SkySun Baked { get; private set; }

    /// <summary>How many times <see cref="Follow"/> has drawn the environment again, for tests.</summary>
    public int Rebakes { get; private set; }

    /// <summary>
    /// A hash of what a baked space's light comes from besides its draws and lights: the environment's bands and panels
    /// from the last <see cref="Set"/>, and the clear sky's turbidity, the view's clouds and the environment intensity of
    /// the last frame drawn. It follows what they hold, so setting the same environment again gives the same hash.
    /// </summary>
    public ulong Hash => StableHasher.ForThread()
        .Add(_content)
        .Add(Baked.Turbidity)
        .Add(Baked.Image is { } image ? _images[image.Index].Content : 0UL)
        .Add(Baked.Rotation)
        .Add(Baked.Scale)
        .Add(_followed.Intensity)
        .Add(_followed.Clouds)
        .Finish();

    /// <summary>Replaces what surfaces reflect. The next frame redraws the environment before anything reads it.</summary>
    public void Set(in EnvironmentDescription environment)
    {
        Map.SetDescription(environment);
        _pending = true;
        Version++;
        _content = HashDescription(environment);

        static ulong HashDescription(in EnvironmentDescription environment)
        {
            var hasher = new StableHasher().Add(environment.Ceiling).Add(environment.Horizon).Add(environment.Floor);
            hasher.Add(environment.Panels.Length);
            foreach (ref readonly var panel in environment.Panels)
            {
                hasher.Add(panel.Direction).Add(panel.HalfWidth).Add(panel.HalfHeight).Add(panel.Radiance);
            }

            return hasher.Finish();
        }
    }

    /// <summary>
    /// Records the frame's share of the environment's redraw: after a change of sky or description, its sharp levels
    /// and a slice of the rough reflections, and after that, while the sky holds still, the slices left. A change
    /// pending at the first frame draws every level, so a sky a game sets before it, and a screenshot of it, show no
    /// default.
    /// </summary>
    public void RecordFrame(CommandBuffer cmd)
    {
        if (_pending && _ring.FramesSubmitted == 0)
        {
            Map.Record(cmd, Baked);
        }
        else
        {
            Map.RecordFrame(cmd, Baked, _pending);
        }

        _pending = false;
    }

    /// <summary>Redraws the whole of a changed environment now and waits, for the paths that draw outside a frame.</summary>
    public void Flush()
    {
        if (_pending || Map.Unfinished)
        {
            _context.SubmitAndWait(cmd => Map.Record(cmd, Baked));
            _pending = false;
        }
    }

    /// <summary>
    /// Marks the environment for a redraw with the view's clear sky when it, its sun or its clouds have changed. The
    /// next frame records it, or <see cref="Flush"/> draws it now. A gradient sky gives the same empty
    /// <see cref="SkySun"/> whatever the sun does, so that never redraws. <paramref name="off"/> decides whether the
    /// clouds are drawn, through <see cref="RenderEffects.Clouds"/>; the hash follows the view's clouds either way, so
    /// turning them off doesn't make a bake stale.
    /// </summary>
    public void Follow(in FrameView view, RenderEffects off)
    {
        _followed = (view.Environment.Intensity, CloudModel.Hash(view));
        var sky = view.Environment.Sky.Image is { } image
            ? SkyModel.Photographed(image, Image(image).Sun, view.Environment.Sky.Rotation, view.Environment.Sky.Exposure)
            : CloudModel.CapturedSky(view, off);
        if (sky == Baked)
        {
            return;
        }

        if (sky.Image != Baked.Image)
        {
            // The redraws read the image through descriptor sets that recorded frames may still use.
            _context.WaitIdle();
            Map.SetImage(sky.Image is { } next ? _images[next.Index].Cube : null);
        }

        Baked = sky;
        _pending = true;
        Rebakes++;
    }

    /// <summary>
    /// Uploads a photographed sky, cooked from an equirectangular .hdr, for <see cref="AnalyticSky.Image"/>. It waits
    /// for the upload and stays until the renderer is disposed.
    /// </summary>
    public SkyImageHandle Create(SkyImageData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Faces.Length != 6)
        {
            throw new ArgumentException($"A sky image needs 6 faces, not {data.Faces.Length}.", nameof(data));
        }

        var first = data.Faces[0];
        var content = new System.IO.Hashing.XxHash64();
        foreach (var face in data.Faces)
        {
            if (face.Width != first.Width || face.Height != first.Width || face.Format != TextureFormat.Rgba16f
                || face.Mips.Length != first.Mips.Length)
            {
                throw new ArgumentException(
                    $"Every face of a sky image must be square Rgba16f, {first.Width} wide with {first.Mips.Length} mips.",
                    nameof(data));
            }

            foreach (var mip in face.Mips)
            {
                content.Append(mip);
            }
        }

        var sun = new SkyImageSun(data.SunDirection, data.SunRadiance, data.SunCosine);
        _images.Add((GpuTexture.Cube(_context, data.Faces), sun, content.GetCurrentHashAsUInt64()));
        return new SkyImageHandle(_images.Count - 1);
    }

    public void Dispose()
    {
        Sh.Dispose();
        Map.Dispose();
        foreach (var image in _images)
        {
            image.Cube.Dispose();
        }
    }

    private (GpuTexture Cube, SkyImageSun Sun, ulong Content) Image(SkyImageHandle image)
    {
        if (image.Index < 0 || image.Index >= _images.Count)
        {
            throw new ArgumentException($"Sky image {image.Index} was not made by this renderer.", nameof(image));
        }

        return _images[image.Index];
    }
}
