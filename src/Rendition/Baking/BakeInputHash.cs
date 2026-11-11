using System.Buffers;
using System.Numerics;

namespace Rendition;

/// <summary>
/// Hashes what a baked space or chunk is made from, so a frame can tell whether a saved bake still fits. Every hash is
/// a <see cref="StableHasher"/> fed a fixed sequence, so it is the same across runs, machines, cultures and draw
/// order. See "Is the bake fresh?" in docs/design.md.
/// </summary>
public static class BakeInputHash
{
    /// <summary>Goes up when the baker changes what it makes, so every saved bake is stale.</summary>
    public const int BakerVersion = 2;

    // Spans up to this many ids are sorted on the stack (2 KiB); longer ones in a pooled array.
    private const int StackLimit = 256;

    [ThreadStatic]
    private static StableHasher? _hasher;

    /// <summary>
    /// A Mixed or Baked light, as seen from its baked area. <paramref name="area"/> is where the area stands: the
    /// identity for a space, and the chunk's own rigid transform for a chunk. The light is taken into the area with
    /// <see cref="BakeAreas.IntoChunk"/>, in doubles, so every placed copy of a chunk gives the same hash. Realtime
    /// lights are never part of a bake, so they are never passed in.
    /// </summary>
    /// <remarks>
    /// The hash holds the mode, the position, range and radius (one that is 0 or less, or not a number, counted as
    /// <see cref="LocalLight.DefaultRadius"/>, as the renderer draws it), whether it casts a shadow, and for a spot its
    /// direction and both angles. Colour counts only for a Baked light: a Mixed light's direct light stays live.
    /// <see cref="LocalLight.Key"/> only tracks a shadow from frame to frame and <see cref="LocalLight.Volumetric"/>
    /// only lights the fog, so neither is in it.
    /// </remarks>
    public static ulong Light(in LocalLight light, in Matrix4x4 area)
    {
        var hasher = Hasher();
        hasher.Add((int)light.Mode);
        hasher.Add(Into(area, light.Position, 1.0));
        hasher.Add(light.Range);
        hasher.Add(Passes.LocalLights.RadiusOf(light));
        hasher.Add(light.CastsShadow ? 1 : 0);
        if (light.IsSpot)
        {
            hasher.Add(1);
            hasher.Add(Into(area, light.Direction, 0.0));
            hasher.Add(light.InnerAngle);
            hasher.Add(light.OuterAngle);
        }
        else
        {
            hasher.Add(0);
        }

        if (light.Mode == LightMode.Baked)
        {
            hasher.Add(light.Radiance);
        }

        return hasher.Finish();
    }

    /// <summary>
    /// A space's sun: its mode, direction and whether it casts a shadow, and its colour only when it is Baked, for the
    /// same reason as <see cref="Light"/>.
    /// </summary>
    public static ulong Sun(in DirectionalLight sun)
    {
        var hasher = Hasher();
        hasher.Add((int)sun.Mode);
        hasher.Add(sun.Direction);
        hasher.Add(sun.CastsShadow ? 1 : 0);
        if (sun.Mode == LightMode.Baked)
        {
            hasher.Add(sun.Radiance);
        }

        return hasher.Finish();
    }

    /// <summary>
    /// A space's inputs. <paramref name="bakeIds"/> are its static draws' ids and <paramref name="lights"/> its lights'
    /// <see cref="Light"/> hashes, in any order; neither span is changed. <paramref name="sun"/> is the sun's
    /// <see cref="Sun"/> hash, or null when the space has none. <paramref name="placedChunks"/> holds a
    /// <see cref="PlacedChunk"/> hash, in any order, for each chunk that owns some of those draws: their ids are
    /// relative to the chunk, so only this says where they stand.
    /// </summary>
    public static BakeInputs Space(
        ReadOnlySpan<ulong> bakeIds, ReadOnlySpan<ulong> lights, BakedSpace space, ulong environment, ulong? sun,
        BakeSettings settings, ReadOnlySpan<ulong> placedChunks = default)
    {
        var hasher = Hasher();
        AddSorted(hasher, bakeIds);
        AddSorted(hasher, placedChunks);
        ulong draws = hasher.Finish();

        hasher = Hasher();
        AddSorted(hasher, lights);
        if (sun is { } sunHash)
        {
            hasher.Add(1);
            hasher.Add(sunHash);
        }
        else
        {
            hasher.Add(0);
        }

        ulong lightHash = hasher.Finish();

        hasher = Hasher();
        AddSettings(hasher, 0, settings);
        hasher.Add(space.ProbeSpacing);
        hasher.Add(space.Min);
        hasher.Add(space.Max);
        return new BakeInputs(draws, lightHash, environment, hasher.Finish());
    }

    /// <summary>
    /// A chunk's inputs, which hold nothing of where it stands or what it's called, so every placed copy shares one
    /// bake: the ids and the lights' hashes are already relative to the chunk, and its box is in its own space. The
    /// sky and sun are applied live to a chunk, so <see cref="BakeInputs.Environment"/> is zero and no sun is hashed.
    /// </summary>
    public static BakeInputs Chunk(
        ReadOnlySpan<ulong> bakeIds, ReadOnlySpan<ulong> lights, BakedChunk chunk, BakeSettings settings)
    {
        ulong draws = Sorted(bakeIds);
        ulong lightHash = Sorted(lights);
        var hasher = Hasher();
        AddSettings(hasher, 1, settings);
        hasher.Add(chunk.Min);
        hasher.Add(chunk.Max);
        return new BakeInputs(draws, lightHash, 0, hasher.Finish());
    }

    /// <summary>
    /// Where a chunk stands, with the draws it owns, for the space's <see cref="Space"/> hash: the chunk's transform,
    /// rounded as every float is, then <paramref name="chunkDraws"/>, the chunk's own <see cref="BakeInputs.Draws"/>.
    /// Moving or turning the chunk changes it, and so does swapping where two chunks stand.
    /// </summary>
    public static ulong PlacedChunk(in BakedChunk chunk, ulong chunkDraws) =>
        Hasher().Add(chunk.Transform).Add(chunkDraws).Finish();

    /// <summary>This thread's hasher, started again, so hashing makes no garbage after the first call.</summary>
    internal static StableHasher Hasher()
    {
        var hasher = _hasher ??= new StableHasher();
        hasher.Reset();
        return hasher;
    }

    private static void AddSettings(StableHasher hasher, int kind, BakeSettings settings)
    {
        hasher.Add(kind);
        hasher.Add(settings.TexelsPerMetre);
        hasher.Add(BakerVersion);
    }

    private static ulong Sorted(ReadOnlySpan<ulong> values)
    {
        var hasher = Hasher();
        AddSorted(hasher, values);
        return hasher.Finish();
    }

    // Hashes the count and then the values in ascending order, sorting a copy so the caller's span is untouched.
    private static void AddSorted(StableHasher hasher, ReadOnlySpan<ulong> values)
    {
        hasher.Add(values.Length);
        ulong[]? rented = null;
        Span<ulong> copy = values.Length <= StackLimit
            ? stackalloc ulong[StackLimit]
            : rented = ArrayPool<ulong>.Shared.Rent(values.Length);
        try
        {
            copy = copy[..values.Length];
            values.CopyTo(copy);
            copy.Sort();
            foreach (ulong value in copy)
            {
                hasher.Add(value);
            }
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<ulong>.Shared.Return(rented);
            }
        }
    }

    // A point (w 1) or a direction (w 0) in the area's own space, worked in doubles by the same inverse the owner
    // checks and the bake ids use.
    private static Vector3 Into(in Matrix4x4 area, Vector3 world, double w)
    {
        var (x, y, z) = BakeAreas.IntoChunk(area, world.X, world.Y, world.Z, w);
        return new Vector3((float)x, (float)y, (float)z);
    }
}
