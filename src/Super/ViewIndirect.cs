using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Rendition;

namespace Super;

/// <summary>
/// The light that bounces in a frame, reached through <see cref="View.Indirect"/>: how corners darken and lit
/// surfaces light their neighbours on screen, the light probes, the baked spaces and chunks, and the reflection
/// probes. Like the rest of a view, it holds only what this frame says.
/// </summary>
public sealed class ViewIndirect
{
    private readonly List<BakedChunk> _bakedChunks = [];
    private readonly List<ReflectionProbeRequest> _reflectionProbes = [];

    internal ViewIndirect()
    {
    }

    internal OcclusionSettings Occlusion { get; private set; } = new();
    internal BounceSettings Bounce { get; private set; } = new();
    internal ProbeVolumeSettings? LightProbeVolume { get; private set; }
    internal BakedSpace? BakedSpace { get; private set; }

    /// <summary>The baked chunks placed so far this frame, in the order they were placed.</summary>
    internal IReadOnlyList<BakedChunk> BakedChunks => _bakedChunks;

    internal ReadOnlySpan<BakedChunk> PlacedBakedChunks => CollectionsMarshal.AsSpan(_bakedChunks);

    /// <summary>The reflection probes placed so far this frame, in the order they were placed.</summary>
    public ReadOnlySpan<ReflectionProbeRequest> ReflectionProbes => CollectionsMarshal.AsSpan(_reflectionProbes);

    /// <summary>
    /// Sets how nearby surfaces darken the soft light from the sky in corners, creases and under
    /// things. <paramref name="radius"/> is how far, in metres, a point looks for surfaces that hide
    /// the sky from it; <paramref name="strength"/>, from 0 to 1, is how dark they make it. The
    /// defaults suit rooms and streets seen from head height.
    /// </summary>
    public void AmbientOcclusion(float radius = OcclusionSettings.DefaultRadius, float strength = OcclusionSettings.DefaultStrength)
    {
        if (!(radius > 0f))
        {
            throw new ArgumentOutOfRangeException(nameof(radius), radius, "The ambient occlusion radius must be more than zero.");
        }

        if (!(strength is >= 0f and <= 1f))
        {
            throw new ArgumentOutOfRangeException(nameof(strength), strength, "The ambient occlusion strength must be from 0 to 1.");
        }

        Occlusion = new OcclusionSettings(radius, strength);
    }

    /// <summary>
    /// Sets the light bounced from the surfaces on screen, which lets a sunlit floor tint the wall beside it and a
    /// glowing sign light the bricks round it. <paramref name="strength"/>, from 0 to 2, scales it, where 1 is the
    /// true amount and 0 turns it off for this view; <paramref name="radius"/>, from more than 0 up to 8, is how far,
    /// in metres, a point looks for surfaces that light it. Without a call it is 1 and 2 metres.
    /// </summary>
    public void ScreenBounce(float strength = BounceSettings.DefaultStrength, float radius = BounceSettings.DefaultRadius)
    {
        if (!(strength is >= 0f and <= BounceSettings.MostStrength))
        {
            throw new ArgumentOutOfRangeException(nameof(strength), strength, "The screen bounce strength must be from 0 to 2.");
        }

        if (!(radius is > 0f and <= BounceSettings.MostRadius))
        {
            throw new ArgumentOutOfRangeException(nameof(radius), radius, "The screen bounce radius must be more than 0 and at most 8 metres.");
        }

        Bounce = new BounceSettings(strength, radius);
    }

    /// <summary>
    /// Fills the box from <paramref name="min"/> to <paramref name="max"/> with light probes
    /// <paramref name="spacing"/> metres apart, which give the surfaces inside the light bounced
    /// off the walls, floor and everything else around them, in place of the sky's. A probe
    /// keeps track of how far it sees in each direction, so light doesn't leak through walls. The
    /// probes are drawn again over the next frames whenever the sun, the sky or the box changes.
    /// At most <see cref="ProbeGrid.MaxProbes"/> fit. See docs/rendering.md.
    /// </summary>
    public void LightProbes(Vector3 min, Vector3 max, float spacing)
    {
        if (!(float.IsFinite(spacing) && spacing > 0f))
        {
            throw new ArgumentOutOfRangeException(nameof(spacing), spacing, "The light probes' spacing must be more than 0 metres.");
        }

        for (int axis = 0; axis < 3; axis++)
        {
            if (!(float.IsFinite(min[axis]) && float.IsFinite(max[axis]) && max[axis] > min[axis]))
            {
                string name = axis == 0 ? "x" : axis == 1 ? "y" : "z";
                throw new ArgumentException(
                    $"The light probes' box must reach further along {name} than it starts, but it runs from {min[axis].ToString(CultureInfo.InvariantCulture)} to {max[axis].ToString(CultureInfo.InvariantCulture)}.",
                    nameof(max));
            }
        }

        var settings = new ProbeVolumeSettings(min, max, spacing);
        long total = ProbeGrid.Total(settings);
        if (total > ProbeGrid.MaxProbes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(spacing), spacing,
                $"The box holds {total.ToString(CultureInfo.InvariantCulture)} light probes at this spacing, but at most {ProbeGrid.MaxProbes.ToString(CultureInfo.InvariantCulture)} fit. Space them further apart or make the box smaller.");
        }

        LightProbeVolume = settings;
    }

    /// <summary>
    /// Names the one fixed place this frame whose lighting is baked as it stands, sun included: the box from
    /// <paramref name="min"/> to <paramref name="max"/>, holding baked probes <paramref name="probeSpacing"/> metres
    /// apart, at most <see cref="BakeFiles.MaxSpaceProbes"/>, with lightmaps of <paramref name="texelsPerMetre"/>
    /// texels for each metre of surface. Its bake is <c>lighting/&lt;name&gt;.lightbake</c>, made with
    /// <c>--bake</c>, and a bake made at another density is stale. A second space in one frame throws. While the bake
    /// is fresh, its lightmaps light the draws it holds in place of its <c>Baked</c> lights, which are no longer drawn
    /// live, its shadow mask shadows its <c>Mixed</c> lights, and its probes light other draws inside the box. A
    /// missing, stale or unreadable bake leaves the space lit live. See docs/rendering.md.
    /// </summary>
    public void BakedLighting(
        string name, Vector3 min, Vector3 max, float probeSpacing,
        int texelsPerMetre = BakeSettings.DefaultTexelsPerMetre)
    {
        BakeFiles.CheckName(name);
        if (BakedSpace is { } named)
        {
            throw new InvalidOperationException(
                $"The baked space \"{named.Name}\" was already named this frame, so \"{name}\" can't be. A frame has at most one baked space.");
        }

        CheckTexelsPerMetre("baked space", texelsPerMetre);

        if (!(float.IsFinite(probeSpacing) && probeSpacing > 0f))
        {
            throw new ArgumentOutOfRangeException(
                nameof(probeSpacing), probeSpacing, "The baked space's probe spacing must be more than 0 metres.");
        }

        CheckBox("baked space", min, max);
        long total = ProbeGrid.Total(new ProbeVolumeSettings(min, max, probeSpacing));
        if (total > BakeFiles.MaxSpaceProbes)
        {
            throw new ArgumentException(
                $"The baked space \"{name}\" holds {total.ToString(CultureInfo.InvariantCulture)} probes at this spacing, but at most {BakeFiles.MaxSpaceProbes.ToString(CultureInfo.InvariantCulture)} fit. Space them further apart or make the box smaller.",
                nameof(probeSpacing));
        }

        BakedSpace = new BakedSpace(name, min, max, probeSpacing, texelsPerMetre);
    }

    /// <summary>
    /// Places a piece of the world that is baked once in its own space: <paramref name="transform"/> places it and
    /// may only turn and move it, and <paramref name="min"/> to <paramref name="max"/> is its box in its own space.
    /// Its lightmaps have <paramref name="texelsPerMetre"/> texels for each metre of surface. The same name may be
    /// placed many times, and every copy with the same contents shares one bake. At most
    /// <see cref="BakeFiles.MaxChunks"/> a frame. With no fresh bake the chunk is lit live. The bake's light blends into
    /// the live light over <paramref name="edgeBlend"/> metres in from the box's four sides, its own ±x and ±z, so it
    /// meets what stands beside it without a step: 0 for a hard edge, and at most half the box's smaller side across x
    /// and z. See docs/rendering.md.
    /// </summary>
    public void BakedChunk(
        string name, Matrix4x4 transform, Vector3 min, Vector3 max,
        int texelsPerMetre = BakeSettings.DefaultTexelsPerMetre, float edgeBlend = global::Rendition.BakedChunk.DefaultEdgeBlend)
    {
        BakeFiles.CheckName(name);
        if (_bakedChunks.Count == BakeFiles.MaxChunks)
        {
            throw new InvalidOperationException(
                $"At most {BakeFiles.MaxChunks.ToString(CultureInfo.InvariantCulture)} baked chunks fit in a frame.");
        }

        BakeFiles.CheckChunkTransform(_bakedChunks.Count, name, transform, nameof(transform));
        CheckBox("baked chunk", min, max);
        CheckTexelsPerMetre("baked chunk", texelsPerMetre);
        BakeFiles.CheckEdgeBlend(name, edgeBlend, min, max, nameof(edgeBlend));
        _bakedChunks.Add(new BakedChunk(name, transform, min, max, texelsPerMetre, edgeBlend));
    }

    /// <summary>
    /// Places a reflection probe: a photograph of the scene in every direction from
    /// <paramref name="position"/>, which shiny surfaces and glass inside the box from
    /// <paramref name="boxMin"/> to <paramref name="boxMax"/> reflect in place of the sky. The box
    /// should match the room or yard round it, since a reflection is bent to land on its walls.
    /// Towards the box's sides the probe fades out over <paramref name="blend"/> metres. Where
    /// boxes overlap, the probe with the higher <paramref name="priority"/> blends first, and
    /// between equal priorities the smaller box does. The key names it across frames, so place it
    /// every frame. It is photographed a frame or a few after it's first placed, again whenever it
    /// moves or its box changes, and otherwise as <paramref name="update"/> says. With
    /// <see cref="ProbeUpdate.OnRequest"/>, change <paramref name="version"/> to ask for a new picture.
    /// Photographs are taken during play without stopping the game, one a frame. At most
    /// <see cref="RendererReflections.MaxProbes"/> fit. See docs/rendering.md.
    /// </summary>
    public void ReflectionProbe(
        int key, Vector3 position, Vector3 boxMin, Vector3 boxMax, float blend = 0.5f, float priority = 0f,
        ProbeUpdate update = ProbeUpdate.OnChange, int version = 0)
    {
        if (_reflectionProbes.Count == RendererReflections.MaxProbes)
        {
            throw new InvalidOperationException(
                $"At most {RendererReflections.MaxProbes.ToString(CultureInfo.InvariantCulture)} reflection probes fit in a frame.");
        }

        foreach (var placed in _reflectionProbes)
        {
            if (placed.Key == key)
            {
                throw new ArgumentException(
                    $"Reflection probe {key.ToString(CultureInfo.InvariantCulture)} was already placed this frame. Give each probe its own key.",
                    nameof(key));
            }
        }

        ReflectionProbeRequest.CheckSettings(blend, priority, update);
        CheckBox("reflection probe", boxMin, boxMax, nameof(boxMax));
        if (!(Vector3.Clamp(position, boxMin, boxMax) == position))
        {
            throw new ArgumentException(
                $"The reflection probe's position {Format(position)} must be inside its box, from {Format(boxMin)} to {Format(boxMax)}.",
                nameof(position));
        }

        _reflectionProbes.Add(new ReflectionProbeRequest(key, position, boxMin, boxMax, blend, priority, update, version));
    }

    /// <summary>Forgets the last frame's probes, bakes and settings as a frame starts.</summary>
    internal void Clear()
    {
        Occlusion = new OcclusionSettings();
        Bounce = new BounceSettings();
        LightProbeVolume = null;
        BakedSpace = null;
        _bakedChunks.Clear();
        _reflectionProbes.Clear();
    }

    private static void CheckTexelsPerMetre(string what, int texelsPerMetre)
    {
        if (texelsPerMetre < BakeSettings.MinTexelsPerMetre || texelsPerMetre > BakeSettings.MaxTexelsPerMetre)
        {
            throw new ArgumentOutOfRangeException(
                nameof(texelsPerMetre), texelsPerMetre,
                $"The {what}'s lightmap density must be {BakeSettings.MinTexelsPerMetre.ToString(CultureInfo.InvariantCulture)} to {BakeSettings.MaxTexelsPerMetre.ToString(CultureInfo.InvariantCulture)} texels a metre, not {texelsPerMetre.ToString(CultureInfo.InvariantCulture)}.");
        }
    }

    private static void CheckBox(string what, Vector3 min, Vector3 max, string maxName = "max")
    {
        for (int axis = 0; axis < 3; axis++)
        {
            if (!(float.IsFinite(min[axis]) && float.IsFinite(max[axis]) && max[axis] > min[axis]))
            {
                string name = axis == 0 ? "x" : axis == 1 ? "y" : "z";
                throw new ArgumentException(
                    $"The {what}'s box must reach further along {name} than it starts, but it runs from {min[axis].ToString(CultureInfo.InvariantCulture)} to {max[axis].ToString(CultureInfo.InvariantCulture)}.",
                    maxName);
            }
        }
    }

    private static string Format(Vector3 value) => value.ToString(null, CultureInfo.InvariantCulture);
}
