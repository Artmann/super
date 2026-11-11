using System.Numerics;
using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// One part of a mesh as a ray sees it: the run of indices a draw item covers, and the material that decides whether
/// rays can pass through it.
/// </summary>
public readonly record struct TracePart(uint FirstIndex, uint IndexCount, MaterialHandle Material);

/// <summary>How rays treat an instance, from the materials of its parts.</summary>
[Flags]
public enum TraceFlags : byte
{
    None = 0,

    /// <summary>Some part has holes cut by its texture's alpha, so a hit there needs checking.</summary>
    Cutout = 1,

    /// <summary>Some part is see-through, such as glass or water.</summary>
    SeeThrough = 2,
}

/// <summary>One placed mesh in the scene rays are traced through, with every part of it the frame draws.</summary>
public readonly record struct TraceInstance(Matrix4x4 Transform, MeshHandle Mesh, TracePart[] Parts, TraceFlags Flags);

/// <summary>
/// One entry of a top-level structure's geometry table: a part of a mesh. A hit's entry is its instance's custom index
/// plus its geometry index.
/// </summary>
public readonly record struct TraceGeometry(MeshHandle Mesh, TracePart Part);

/// <summary>Chooses what in a frame's draws rays can hit. See "Ray tracing" in docs/design.md.</summary>
internal static unsafe class TraceScene
{
    /// <summary>
    /// One instance per distinct mesh and transform, in the order they're first drawn, with the parts in draw order.
    /// Ghosts are previews, not things; skinned and swimming draws move away from the shape their structure was built
    /// from; so all three are left out, along with draws of no triangles.
    /// </summary>
    public static List<TraceInstance> Collect(ReadOnlySpan<DrawItem> draws, Func<MaterialHandle, TraceFlags> flagsOf)
    {
        var order = new Dictionary<(MeshHandle, Matrix4x4), int>();
        var parts = new List<List<TracePart>>();
        var placed = new List<(MeshHandle Mesh, Matrix4x4 Transform, TraceFlags Flags)>();
        foreach (ref readonly var draw in draws)
        {
            if (draw.Ghost is not null || draw.Bones.Count != 0 || draw.Swim.Amplitude != 0f || draw.IndexCount == 0)
            {
                continue;
            }

            var key = (draw.Mesh, draw.Transform);
            if (!order.TryGetValue(key, out int index))
            {
                index = placed.Count;
                order[key] = index;
                placed.Add((draw.Mesh, draw.Transform, TraceFlags.None));
                parts.Add([]);
            }

            parts[index].Add(new TracePart(draw.FirstIndex, draw.IndexCount, draw.Material));
            placed[index] = placed[index] with { Flags = placed[index].Flags | flagsOf(draw.Material) };
        }

        var instances = new List<TraceInstance>(placed.Count);
        for (int i = 0; i < placed.Count; i++)
        {
            instances.Add(new TraceInstance(placed[i].Transform, placed[i].Mesh, [.. parts[i]], placed[i].Flags));
        }

        return instances;
    }

    /// <summary>
    /// System.Numerics multiplies row vectors (<c>v * M</c>, translation in <c>M41..M43</c>). Vulkan's 3×4 is row-major
    /// and acts on column vectors, so row r of the result is column r of <paramref name="transform"/>.
    /// </summary>
    public static TransformMatrixKHR ToVulkan(in Matrix4x4 transform)
    {
        var result = new TransformMatrixKHR();
        var m = new Span<float>(&result, 12);
        m[0] = transform.M11; m[1] = transform.M21; m[2] = transform.M31; m[3] = transform.M41;
        m[4] = transform.M12; m[5] = transform.M22; m[6] = transform.M32; m[7] = transform.M42;
        m[8] = transform.M13; m[9] = transform.M23; m[10] = transform.M33; m[11] = transform.M43;
        return result;
    }
}
