using System.Numerics;
using System.Runtime.InteropServices;

namespace Rendition;

/// <summary>
/// The lights a space's bake takes, and where each of the view's suns and local lights is among them, or -1. The
/// copies for the mask channels are Realtime where the bake doesn't take the light.
/// </summary>
internal sealed record SpaceBakeLights(
    List<GpuBakeLight> Lights, int[] SunIndex, int[] LocalIndex, DirectionalLight[] SunsForMask, LocalLight[] LocalsForMask);

/// <summary>
/// The lights a chunk's bake takes, moved into its space, where each of the view's local lights is among them, or -1,
/// and the copies for the mask channels, Realtime where the bake doesn't take the light.
/// </summary>
internal sealed record ChunkBakeLights(List<GpuBakeLight> Lights, int[] LightOf, LocalLight[] ForMask);

/// <summary>The lights a bake of the view's space or of one of its chunks takes, and their shadow mask channels.</summary>
internal static class BakeLights
{
    /// <summary>
    /// The lights the space's bake takes: Mixed and Baked ones that hold still, a local light only in the space's box.
    /// </summary>
    public static SpaceBakeLights TakeSpace(in FrameView view, BakedSpace space, AnimatedLights animated)
    {
        var suns = view.Lights;
        var locals = view.LocalLights;
        var lights = new SpaceBakeLights([], new int[suns.Length], new int[locals.Length], suns.ToArray(), locals.ToArray());
        for (int i = 0; i < suns.Length; i++)
        {
            lights.SunIndex[i] = -1;
            if (suns[i].Mode != LightMode.Realtime && !animated.IsAnimated(new LightKey(true, i)))
            {
                lights.SunIndex[i] = lights.Lights.Count;
                lights.Lights.Add(GpuBakeLight.Sun(suns[i], view.Shadows.LightAngle));
            }
            else
            {
                lights.SunsForMask[i].Mode = LightMode.Realtime;
            }
        }

        for (int i = 0; i < locals.Length; i++)
        {
            lights.LocalIndex[i] = -1;
            if (locals[i].Mode != LightMode.Realtime && !animated.IsAnimated(new LightKey(false, locals[i].Key))
                && BakeAreas.OwnerOfLight(locals[i].Position, space, view.BakedChunks) == BakeAreas.Space)
            {
                lights.LocalIndex[i] = lights.Lights.Count;
                lights.Lights.Add(GpuBakeLight.Local(locals[i]));
            }
            else
            {
                lights.LocalsForMask[i].Mode = LightMode.Realtime;
            }
        }

        return lights;
    }

    /// <summary>
    /// Gives each of the space's Mixed lights its mask channel, and lists the local lights' channels by key. Local
    /// lights that don't overlap can share one, so the channel goes on each light rather than each channel naming one
    /// light. The sun's channel 0 is implied, so only local lights are listed.
    /// </summary>
    public static List<(int LightKey, int Channel)> AssignSpaceChannels(
        in FrameView view, BakedSpace space, SpaceBakeLights lights, List<int> unassigned)
    {
        var locals = view.LocalLights;
        var assigned = ShadowMaskChannels.Assign(space, lights.SunsForMask, lights.LocalsForMask);
        var sunChannels = assigned.Suns;
        var localChannels = assigned.Lights;
        unassigned.Clear();
        unassigned.AddRange(assigned.Unassigned);
        var bakeLights = CollectionsMarshal.AsSpan(lights.Lights);
        var channels = new List<(int LightKey, int Channel)>();
        for (int i = 0; i < sunChannels.Length; i++)
        {
            if (sunChannels[i] >= 0)
            {
                bakeLights[lights.SunIndex[i]].MaskChannel = sunChannels[i];
            }
        }

        for (int i = 0; i < locals.Length; i++)
        {
            if (localChannels[i] >= 0)
            {
                bakeLights[lights.LocalIndex[i]].MaskChannel = localChannels[i];
                channels.Add((locals[i].Key, localChannels[i]));
            }
        }

        return channels;
    }

    /// <summary>
    /// The chunk's own lights, Mixed and Baked ones that hold still, moved into its space. Every other light is
    /// Realtime as far as the mask channels go, so only these get one.
    /// </summary>
    public static ChunkBakeLights TakeChunk(in FrameView view, int chunk, AnimatedLights animated)
    {
        var baked = view.BakedChunks[chunk];
        var locals = view.LocalLights;
        var lights = new ChunkBakeLights([], new int[locals.Length], locals.ToArray());
        for (int i = 0; i < locals.Length; i++)
        {
            lights.LightOf[i] = -1;
            if (locals[i].Mode != LightMode.Realtime && !animated.IsAnimated(new LightKey(false, locals[i].Key))
                && BakeAreas.OwnerOfLight(locals[i].Position, view.BakedSpace, view.BakedChunks) == chunk)
            {
                var light = locals[i];
                var (px, py, pz) = BakeAreas.IntoChunk(baked.Transform, light.Position.X, light.Position.Y, light.Position.Z, 1.0);
                var (dx, dy, dz) = BakeAreas.IntoChunk(baked.Transform, light.Direction.X, light.Direction.Y, light.Direction.Z, 0.0);
                light.Position = new Vector3((float)px, (float)py, (float)pz);
                light.Direction = new Vector3((float)dx, (float)dy, (float)dz);
                lights.LightOf[i] = lights.Lights.Count;
                lights.Lights.Add(GpuBakeLight.Local(light));
            }
            else
            {
                lights.ForMask[i].Mode = LightMode.Realtime;
            }
        }

        return lights;
    }

    /// <summary>
    /// Gives each of the chunk's Mixed lights its mask channel, and lists the channels by each light's chunk-space hash.
    /// Returns the lights the bake takes, with their channels.
    /// </summary>
    public static (GpuBakeLight[] Lights, List<(ulong LightHash, int Channel)> Channels) AssignChunkChannels(
        ReadOnlySpan<LocalLight> locals, in BakedChunk baked, ChunkBakeLights lights, List<int> unassigned)
    {
        var maskChannels = new int[locals.Length];
        ShadowMaskChannels.AssignChunk(baked, lights.ForMask, maskChannels, unassigned);
        var bakeLights = lights.Lights.ToArray();
        var chunkChannels = new List<(ulong LightHash, int Channel)>();
        for (int i = 0; i < locals.Length; i++)
        {
            if (maskChannels[i] >= 0 && lights.LightOf[i] >= 0)
            {
                bakeLights[lights.LightOf[i]].MaskChannel = maskChannels[i];
                chunkChannels.Add((BakeInputHash.Light(locals[i], baked.Transform), maskChannels[i]));
            }
        }

        return (bakeLights, chunkChannels);
    }
}
