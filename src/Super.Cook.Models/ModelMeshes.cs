using System.Numerics;
using System.Runtime.InteropServices;
using Rendition;
using Silk.NET.Assimp;

namespace Super.Cook.Models;

/// <summary>
/// A model's meshes as they're read, laid end to end: their vertices, their triangles as indices into those, and a part
/// for each mesh. <see cref="Vertices"/> are as read, before the model is turned, scaled and given its second UV set.
/// </summary>
internal sealed class ModelMeshes
{
    public List<Vertex> Vertices { get; } = [];

    public List<uint> Indices { get; } = [];

    public List<ModelPart> Parts { get; } = [];

    /// <summary>The name of each part's mesh, when the meshes are placed on their own.</summary>
    public List<string> MeshNames { get; } = [];

    /// <summary>Each mesh's material and the run of vertices it added.</summary>
    public List<(int Material, int First, int Count)> Runs { get; } = [];

    /// <summary>For each material, how many of the meshes placed with it have a second UV set of their own, and how many don't.</summary>
    public Dictionary<int, (int With, int Without)> Uv2Meshes { get; } = [];

    /// <summary>
    /// Adds a mesh's vertices, placed by <paramref name="world"/>, and returns them so a rig can skin them before more are
    /// added. A graph's UVs are in metres, so with a <paramref name="graphWidth"/> above 0 they're divided by it.
    /// </summary>
    public unsafe Span<Vertex> AddVertices(Mesh* mesh, in Matrix4x4 world, float graphWidth)
    {
        int material = (int)mesh->MMaterialIndex;
        int first = Vertices.Count;
        Runs.Add((material, first, (int)mesh->MNumVertices));
        Uv2Meshes.TryGetValue(material, out var uv2Count);
        Uv2Meshes[material] = mesh->MTextureCoords[1] == null
            ? (uv2Count.With, uv2Count.Without + 1)
            : (uv2Count.With + 1, uv2Count.Without);

        // A mirroring placement turns the tangent frame over, so its sign flips.
        var placement = new Placement(world, NormalMatrix(world), world.GetDeterminant() < 0f);
        for (uint v = 0; v < mesh->MNumVertices; v++)
        {
            Vertices.Add(ReadVertex(mesh, v, placement, graphWidth));
        }

        return CollectionsMarshal.AsSpan(Vertices).Slice(first, (int)mesh->MNumVertices);
    }

    /// <summary>Adds a mesh's triangles, whose vertices start at <paramref name="baseVertex"/>, as a part of its own.</summary>
    public unsafe void AddTriangles(Mesh* mesh, uint baseVertex)
    {
        uint firstIndex = (uint)Indices.Count;
        for (uint f = 0; f < mesh->MNumFaces; f++)
        {
            var face = mesh->MFaces[f];
            if (face.MNumIndices != 3)
            {
                continue; // Stray points and lines survive triangulation.
            }

            Indices.Add(baseVertex + face.MIndices[0]);
            Indices.Add(baseVertex + face.MIndices[1]);
            Indices.Add(baseVertex + face.MIndices[2]);
        }

        Parts.Add(new ModelPart
        {
            FirstIndex = firstIndex,
            IndexCount = (uint)Indices.Count - firstIndex,
            Material = (int)mesh->MMaterialIndex,
        });
    }

    private static unsafe Vertex ReadVertex(Mesh* mesh, uint v, in Placement placement, float graphWidth)
    {
        var normal = mesh->MNormals != null ? mesh->MNormals[v] : Vector3.UnitY;
        var uv = mesh->MTextureCoords[0] != null ? mesh->MTextureCoords[0][v] : Vector3.Zero;
        if (graphWidth > 0f)
        {
            uv = GraphUv(uv, graphWidth);
        }

        var tangent = new Vector4(Vector3.UnitX, 1f);
        if (mesh->MTangents != null && mesh->MBitangents != null)
        {
            float sign = Vector3.Dot(Vector3.Cross(normal, mesh->MTangents[v]), mesh->MBitangents[v]) < 0f ? -1f : 1f;
            tangent = new Vector4(mesh->MTangents[v], sign);
        }

        var uvs2 = mesh->MTextureCoords[1];
        var direction = Vector3.TransformNormal(new Vector3(tangent.X, tangent.Y, tangent.Z), placement.World);
        return new Vertex(
            Vector3.Transform(mesh->MVertices[v], placement.World),
            Vector3.TransformNormal(normal, placement.Normals),
            new Vector4(direction, placement.Mirrored ? -tangent.W : tangent.W),
            new Vector2(uv.X, uv.Y))
        {
            Uv2 = uvs2 != null ? new Vector2(uvs2[v].X, uvs2[v].Y) : Vector2.Zero,
        };
    }

    // Normals keep pointing off the surface when a placement stretches it, so they use the inverse transpose.
    private static Matrix4x4 NormalMatrix(in Matrix4x4 world) =>
        Matrix4x4.Invert(world, out var inverse) ? Matrix4x4.Transpose(inverse) : Matrix4x4.Identity;

    // A graph's UVs are in metres, so they're divided by its width. FlipUVs has turned v upside down, so v is turned back
    // first and again after. That keeps v running the way it did, and lines a tile's edge up with the file's own UV origin,
    // so a brick wall laid out from its foot in Blender starts a row of bricks at its foot.
    private static Vector3 GraphUv(Vector3 uv, float width)
    {
        return new Vector3(uv.X / width, 1f - (1f - uv.Y) / width, uv.Z);
    }

    /// <summary>Where a mesh goes: the transform for its positions, the one for its normals, and whether it mirrors.</summary>
    private readonly record struct Placement(Matrix4x4 World, Matrix4x4 Normals, bool Mirrored);
}
