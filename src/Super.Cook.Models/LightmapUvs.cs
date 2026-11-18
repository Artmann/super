using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Rendition;

namespace Super.Cook.Models;

/// <summary>
/// Gives every material's parts a second UV set fit for a lightmap. A material is the group the masks bake one mask
/// for, so each has its own 0 to 1, and two materials may overlap each other there. A material keeps the file's set
/// when it passes <see cref="LightmapUnwrap.Problem"/> for that material alone, and otherwise only that material is
/// unwrapped: at <see cref="LightmapUnwrap.ReferenceSize"/>, then twice and four times that when its charts don't
/// fit. A note says which materials got new ones, when they were unwrapped rather than recalled.
/// </summary>
internal static class LightmapUvs
{
    private const string Step = "lightmap-uv";

    /// <summary>The note's reason for a material none of whose meshes has a second UV set.</summary>
    private const string NoUv2 = "the file has none";

    /// <summary>Names the code that unwraps and the kept bytes' layout, for the step cache's keys: <see cref="LightmapUnwrap.Version"/> and this cooker's code.</summary>
    private static readonly string UnwrapVersion = LightmapUnwrap.Version + CodeId.Of(typeof(LightmapUvs).Assembly).ToString("N");

    /// <summary>
    /// Settles the second UV set of every material. Unwrapping splits vertices where charts meet, so this returns the
    /// vertices to use and rewrites the indices of <paramref name="meshes"/> in place, keeping every part's triangles in
    /// their order. It also returns the materials whose set it made, unwrapped or recalled. Null when a material can't be
    /// unwrapped, which was reported.
    /// </summary>
    public static (Vertex[] Vertices, HashSet<int> Made)? Make(
        ModelCook cook, Vertex[] vertices, ModelMeshes meshes, List<string> materialNames)
    {
        var groups = meshes.Parts.Where(part => part.IndexCount > 0).Select(part => part.Material).Distinct().Order().ToList();
        var made = new Dictionary<int, (Vertex[] Vertices, uint[] Indices)>();
        var notes = new List<(int Material, string Reason)>();
        foreach (int material in groups)
        {
            var (groupVertices, groupIndices) = Group(vertices, meshes, material);
            if (groupIndices.Length == 0
                || Reason(cook.Input, meshes.Uv2Meshes.GetValueOrDefault(material), groupVertices, groupIndices) is not { } reason)
            {
                continue;
            }

            if (Recall(cook.Input, groupVertices, groupIndices, out string? key) is { } recalled)
            {
                made[material] = recalled;
                continue;
            }

            if (Unwrap(cook.Input, groupVertices, groupIndices, out string? failure) is not { } unwrapped)
            {
                cook.MaterialError(materialNames[material], failure!);
                return null;
            }

            Keep(cook.Input, key, groupIndices, unwrapped);
            made[material] = unwrapped;
            notes.Add((material, reason));
        }

        Note(cook, notes, groups.Count, materialNames);
        if (made.Count == 0)
        {
            return (vertices, []);
        }

        return (Rebuild(vertices, meshes, made), made.Keys.ToHashSet());
    }

    /// <summary>Why a material needs a new second UV set, or null when its own will do.</summary>
    private static string? Reason(CookInput input, (int With, int Without) uv2Meshes, Vertex[] groupVertices, uint[] groupIndices)
    {
        var (with, without) = uv2Meshes;
        if (with == 0)
        {
            return NoUv2;
        }

        if (without > 0)
        {
            return $"{without} of its {with + without} meshes have none";
        }

        using (input.Time("check lightmap-uv"))
        {
            var positions = groupVertices.Select(vertex => vertex.Position).ToArray();
            var uvs = groupVertices.Select(vertex => vertex.Uv2).ToArray();
            return LightmapUnwrap.Problem(positions, uvs, groupIndices);
        }
    }

    /// <summary>
    /// A material's unwrap from the step cache, or null when it has none. <paramref name="key"/> is its key there, or
    /// null when there's no step cache.
    /// </summary>
    private static (Vertex[] Vertices, uint[] Indices)? Recall(CookInput input, Vertex[] groupVertices, uint[] groupIndices, out string? key)
    {
        key = null;
        if (!input.StepResults.HasCache)
        {
            return null;
        }

        using (input.Time("recall lightmap-uv"))
        {
            key = UnwrapKey(groupVertices, groupIndices);
            return input.StepResults.Recall(Step, key) is { } kept ? FromKept(kept, groupVertices, groupIndices.Length) : null;
        }
    }

    /// <summary>Unwraps a material, on larger pages while its charts don't fit. Null when none fit, and why in <paramref name="failure"/>.</summary>
    private static (Vertex[] Vertices, uint[] Indices)? Unwrap(CookInput input, Vertex[] groupVertices, uint[] groupIndices, out string? failure)
    {
        failure = null;
        using (input.Time("unwrap lightmap-uv"))
        {
            for (int size = LightmapUnwrap.ReferenceSize; size <= LightmapUnwrap.LargestSize; size *= 2)
            {
                try
                {
                    return LightmapUnwrap.Unwrap(groupVertices, groupIndices, size);
                }
                catch (InvalidOperationException exception)
                {
                    failure = exception.Message;
                }
            }
        }

        return null;
    }

    /// <summary>Keeps a material's unwrap in the step cache under <paramref name="key"/>, when there is one.</summary>
    private static void Keep(CookInput input, string? key, uint[] groupIndices, (Vertex[] Vertices, uint[] Indices) unwrapped)
    {
        if (key is null)
        {
            return;
        }

        var (newVertices, newIndices) = unwrapped;

        // Each corner's new vertex is a copy of its old one with new second UVs, which is all the cache needs to make it again.
        var remap = new uint[newVertices.Length];
        for (int corner = 0; corner < groupIndices.Length; corner++)
        {
            remap[newIndices[corner]] = groupIndices[corner];
        }

        using (input.Time("keep lightmap-uv"))
        {
            input.StepResults.Keep(Step, key, ToKept(newVertices, remap, newIndices));
        }
    }

    /// <summary>
    /// The model's vertices with the materials that were <paramref name="made"/> again swapped for their new ones. The
    /// materials kept hold on to their vertices, in their order, and the new ones follow, material by material. The
    /// indices are rewritten to match.
    /// </summary>
    private static Vertex[] Rebuild(Vertex[] vertices, ModelMeshes meshes, Dictionary<int, (Vertex[] Vertices, uint[] Indices)> made)
    {
        var (indices, parts) = (meshes.Indices, meshes.Parts);
        var replaced = new bool[vertices.Length];
        foreach (var part in parts.Where(part => made.ContainsKey(part.Material)))
        {
            for (int i = 0; i < part.IndexCount; i++)
            {
                replaced[indices[(int)part.FirstIndex + i]] = true;
            }
        }

        var result = KeepUnreplaced(vertices, indices, replaced);
        foreach (var (material, (newVertices, newIndices)) in made.OrderBy(pair => pair.Key))
        {
            uint first = (uint)result.Count;
            result.AddRange(newVertices);
            int corner = 0;
            foreach (var part in parts.Where(part => part.Material == material))
            {
                for (int i = 0; i < part.IndexCount; i++)
                {
                    indices[(int)part.FirstIndex + i] = first + newIndices[corner++];
                }
            }
        }

        return result.ToArray();
    }

    /// <summary>The vertices that aren't <paramref name="replaced"/>, in their order, with the indices that use them moved to match.</summary>
    private static List<Vertex> KeepUnreplaced(Vertex[] vertices, List<uint> indices, bool[] replaced)
    {
        var result = new List<Vertex>(vertices.Length);
        var moved = new uint[vertices.Length];
        for (int v = 0; v < vertices.Length; v++)
        {
            if (!replaced[v])
            {
                moved[v] = (uint)result.Count;
                result.Add(vertices[v]);
            }
        }

        for (int i = 0; i < indices.Count; i++)
        {
            if (!replaced[indices[i]])
            {
                indices[i] = moved[indices[i]];
            }
        }

        return result;
    }

    /// <summary>One material's parts on their own: the vertices they use, in the model's order, and their indices into those, part after part.</summary>
    private static (Vertex[] Vertices, uint[] Indices) Group(Vertex[] vertices, ModelMeshes meshes, int material)
    {
        var local = new Dictionary<uint, uint>();
        var sources = new List<Vertex>();
        var groupIndices = new List<uint>();
        foreach (var part in meshes.Parts.Where(part => part.Material == material))
        {
            for (int i = 0; i < part.IndexCount; i++)
            {
                uint index = meshes.Indices[(int)part.FirstIndex + i];
                if (!local.TryGetValue(index, out uint at))
                {
                    at = (uint)sources.Count;
                    local.Add(index, at);
                    sources.Add(vertices[index]);
                }

                groupIndices.Add(at);
            }
        }

        return (sources.ToArray(), groupIndices.ToArray());
    }

    /// <summary>
    /// Says which materials got a second UV set made for lighting, and why. A model of one material, or one whose every
    /// material had none, gets one line with the reason alone. Otherwise the materials that had none share a line, and
    /// each of the rest has its own.
    /// </summary>
    private static void Note(ModelCook cook, List<(int Material, string Reason)> notes, int groupCount, List<string> materialNames)
    {
        if (notes.Count == 0)
        {
            return;
        }

        const string made = "made a second UV set for lighting";
        bool allNone = notes.Count == groupCount && notes.All(note => note.Reason == NoUv2);
        if (groupCount == 1 || allNone)
        {
            cook.Note($"{made}: {notes[0].Reason}");
            return;
        }

        var none = notes.Where(note => note.Reason == NoUv2).Select(note => materialNames[note.Material]).ToList();
        if (none.Count == 1)
        {
            cook.Note($"{made}: material {none[0]} has none");
        }
        else if (none.Count > 1)
        {
            cook.Note($"{made}: materials {string.Join(", ", none.SkipLast(1))} and {none[^1]} have none");
        }

        foreach (var (material, reason) in notes.Where(note => note.Reason != NoUv2))
        {
            cook.Note($"material {materialNames[material]}: {made}: {reason}");
        }
    }

    /// <summary>
    /// The step cache's key for one material's unwrap: everything <see cref="LightmapUnwrap.Unwrap"/> reads, which is the
    /// corners' positions and normals and the indices, with the unwrapper's and this cooker's builds and the largest page it may try.
    /// </summary>
    private static string UnwrapKey(Vertex[] vertices, uint[] indices)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(BitConverter.GetBytes(vertices.Length));
        foreach (var vertex in vertices)
        {
            var position = vertex.Position;
            var normal = vertex.Normal;
            hash.AppendData(MemoryMarshal.AsBytes(new ReadOnlySpan<Vector3>(in position)));
            hash.AppendData(MemoryMarshal.AsBytes(new ReadOnlySpan<Vector3>(in normal)));
        }

        hash.AppendData(BitConverter.GetBytes(indices.Length));
        hash.AppendData(MemoryMarshal.AsBytes(indices.AsSpan()));
        return string.Join('|', UnwrapVersion, LightmapUnwrap.LargestSize.ToString(CultureInfo.InvariantCulture), Convert.ToHexString(hash.GetHashAndReset()));
    }

    // The kept bytes: the vertex and index counts, then for each new vertex the old vertex it copies, then each new
    // vertex's second UVs, then the new indices. All of them count within the material, not the model.
    private static byte[] ToKept(Vertex[] unwrapped, uint[] remap, uint[] unwrappedIndices)
    {
        var uvs = unwrapped.Select(vertex => vertex.Uv2).ToArray();
        using var stream = new MemoryStream(8 + unwrapped.Length * 12 + unwrappedIndices.Length * 4);
        stream.Write(BitConverter.GetBytes(unwrapped.Length));
        stream.Write(BitConverter.GetBytes(unwrappedIndices.Length));
        stream.Write(MemoryMarshal.AsBytes(remap.AsSpan()));
        stream.Write(MemoryMarshal.AsBytes(uvs.AsSpan()));
        stream.Write(MemoryMarshal.AsBytes(unwrappedIndices.AsSpan()));
        return stream.ToArray();
    }

    // Makes a material's unwrapped vertices and indices again from kept bytes, or null when the bytes don't fit it.
    private static (Vertex[] Vertices, uint[] Indices)? FromKept(byte[] kept, Vertex[] vertices, int indexCount)
    {
        if (kept.Length < 8)
        {
            return null;
        }

        int vertexCount = BitConverter.ToInt32(kept, 0);
        if (vertexCount < 0 || BitConverter.ToInt32(kept, 4) != indexCount || kept.Length != 8 + (long)vertexCount * 12 + (long)indexCount * 4)
        {
            return null;
        }

        var remap = MemoryMarshal.Cast<byte, uint>(kept.AsSpan(8, vertexCount * 4));
        var uvs = MemoryMarshal.Cast<byte, Vector2>(kept.AsSpan(8 + vertexCount * 4, vertexCount * 8));
        var keptIndices = MemoryMarshal.Cast<byte, uint>(kept.AsSpan(8 + vertexCount * 12, indexCount * 4)).ToArray();
        var unwrapped = new Vertex[vertexCount];
        for (int i = 0; i < vertexCount; i++)
        {
            if (remap[i] >= vertices.Length)
            {
                return null;
            }

            unwrapped[i] = vertices[remap[i]] with { Uv2 = uvs[i] };
        }

        foreach (uint index in keptIndices)
        {
            if (index >= vertexCount)
            {
                return null;
            }
        }

        return (unwrapped, keptIndices);
    }
}
