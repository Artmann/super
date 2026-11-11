using System.Numerics;
using Rendition;

namespace Super;

/// <summary>
/// Gives a static draw's parts their <see cref="DrawItem.BakeId"/>s. An instance's hash holds the model's identity,
/// the tint and the transform, made relative to the baked chunk that owns the draw when there is one, so a chunk
/// placed anywhere gives the same ids. Each part's id is that hash with the part's number in the model. See "Bake
/// ids" in docs/design.md.
/// </summary>
internal static class BakeIdentity
{
    /// <summary>What a frame reuses for every static draw, so giving ids makes no garbage.</summary>
    internal sealed class Scratch
    {
        private BakeSphere[] _spheres = new BakeSphere[16];

        public StableHasher Hasher { get; } = new();

        /// <summary>Room for <paramref name="count"/> spheres, kept from the last call.</summary>
        public Span<BakeSphere> Spheres(int count)
        {
            if (_spheres.Length < count)
            {
                _spheres = new BakeSphere[Math.Max(count, _spheres.Length * 2)];
            }

            return _spheres.AsSpan(0, count);
        }
    }

    /// <summary>
    /// The instance hash. The model's identity covers its meshes and materials, so the part number is all a part adds.
    /// </summary>
    public static ulong Instance(StableHasher hasher, ulong model, Vector3? tint, in Matrix4x4 relative)
    {
        hasher.Reset();
        hasher.Add(model);
        if (tint is { } color)
        {
            hasher.Add(1).Add(color);
        }
        else
        {
            hasher.Add(0);
        }

        hasher.Add(relative);
        return hasher.Finish();
    }

    /// <summary>
    /// <paramref name="world"/> as seen from a rigid chunk: world times the chunk's inverse. Worked in doubles, so a chunk
    /// far from the origin gives the same numbers as one at it.
    /// </summary>
    public static Matrix4x4 Relative(in Matrix4x4 world, in Matrix4x4 chunk)
    {
        var result = default(Matrix4x4);
        for (int row = 0; row < 4; row++)
        {
            // A row with a 1 in its last column is a point, and takes off the chunk's translation; a direction doesn't.
            double w = world[row, 3];
            double dx = world[row, 0] - w * chunk.M41;
            double dy = world[row, 1] - w * chunk.M42;
            double dz = world[row, 2] - w * chunk.M43;

            // The inverse rotation is the transpose, so each local coordinate is a dot product with one of the chunk's rows.
            result[row, 0] = (float)(dx * chunk.M11 + dy * chunk.M12 + dz * chunk.M13);
            result[row, 1] = (float)(dx * chunk.M21 + dy * chunk.M22 + dz * chunk.M23);
            result[row, 2] = (float)(dx * chunk.M31 + dy * chunk.M32 + dz * chunk.M33);
            result[row, 3] = (float)w;
        }

        return result;
    }

    /// <summary>
    /// Sets the bake id of each shown part of a static draw. <paramref name="partBounds"/> holds every part of the model,
    /// shown or not, in the model's units; <paramref name="mask"/> picks the shown ones, in order, as
    /// <see cref="SceneRenderer.AddShown"/> copied them.
    /// </summary>
    public static void Assign(
        Span<DrawItem> shown,
        MeshMask? mask,
        ReadOnlySpan<BakeSphere> partBounds,
        ulong model,
        Vector3? tint,
        in Matrix4x4 transform,
        BakedSpace? space,
        ReadOnlySpan<BakedChunk> chunks,
        Scratch scratch)
    {
        var spheres = scratch.Spheres(shown.Length);
        int s = 0;
        for (int p = 0; p < partBounds.Length; p++)
        {
            if (mask is null || mask.Shows(p))
            {
                spheres[s++] = BakeAreas.Transform(partBounds[p], transform);
            }
        }

        int owner = BakeAreas.OwnerOfDraw(BakeAreas.Union(spheres), space, chunks);
        var placed = owner >= 0 ? Relative(transform, chunks[owner].Transform) : transform;
        ulong instance = Instance(scratch.Hasher, model, tint, placed);

        s = 0;
        for (int p = 0; p < partBounds.Length; p++)
        {
            if (mask is null || mask.Shows(p))
            {
                shown[s++].BakeId = BakeIds.Of(instance, p);
            }
        }
    }

    /// <summary>
    /// What a model is, as a stable hash: a built-in shape's name, a cooked file's hash, or everything a mesh made in
    /// code is drawn with: its vertices, indices, surface, textures and shader.
    /// </summary>
    public static ulong Model(ModelRef model, IReadOnlyList<object> items, IContentFiles files)
    {
        var hasher = new StableHasher();
        if (model.IsBuiltIn)
        {
            return hasher.Add(0).Add(model.Index == 0 ? "cube" : Shapes.BuiltInNames[-model.Index - 1]).Finish();
        }

        switch (items[model.Index - 1])
        {
            case string name:
                return hasher.Add(1).Add(files.HashOf(name)).Finish();
            case GeneratedMesh mesh:
                hasher.Add(2);
                AddMesh(hasher, mesh, items);
                return hasher.Finish();
            default:
                throw new InvalidOperationException($"Item {model.Index} is not a model, so it can't be drawn static.");
        }
    }

    private static void AddMesh(StableHasher hasher, GeneratedMesh mesh, IReadOnlyList<object> items)
    {
        hasher.Add(mesh.Vertices.Length);
        foreach (ref readonly var vertex in mesh.Vertices.AsSpan())
        {
            hasher.Add(vertex.Position).Add(vertex.Normal);
            hasher.Add(vertex.Tangent.X).Add(vertex.Tangent.Y).Add(vertex.Tangent.Z).Add(vertex.Tangent.W);
            hasher.Add(vertex.Uv.X).Add(vertex.Uv.Y).Add(vertex.Uv2.X).Add(vertex.Uv2.Y);
            hasher.Add(unchecked((int)vertex.Joints)).Add(unchecked((int)vertex.Weights));
        }

        hasher.Add(mesh.Indices.Length);
        foreach (uint index in mesh.Indices)
        {
            hasher.Add(unchecked((int)index));
        }

        hasher.Add(mesh.Simplify ? 1 : 0);
        if (mesh.OwnedTexture is { } owned)
        {
            hasher.Add(1).Add(owned.Size).Add(owned.Pixels);
        }
        else
        {
            hasher.Add(0);
        }

        var surface = mesh.Surface;
        hasher.Add(surface.Color).Add(surface.Roughness).Add(surface.Metallic);
        AddTexture(hasher, surface.ColorTexture, items);
        AddTexture(hasher, surface.NormalTexture, items);
        AddTexture(hasher, surface.SurfaceTexture, items);
        hasher.Add(surface.TileWidth is null ? 0 : 1).Add(surface.TileWidth ?? 0f);
        hasher.Add(surface.Opacity).Add(surface.Ior);
        AddTexture(hasher, surface.HeightTexture, items);
        hasher.Add(surface.HeightDepth);
        AddTexture(hasher, surface.DetailColorTexture, items);
        AddTexture(hasher, surface.DetailNormalTexture, items);
        hasher.Add(surface.DetailScale);
        hasher.Add(surface.DetailMask ? 1 : 0).Add(surface.WorldVariation ? 1 : 0).Add(surface.Dithered ? 1 : 0);
        hasher.Add(surface.Puddles ? 1 : 0).Add(surface.Foliage ? 1 : 0);
        hasher.Add(surface.Translucency).Add(surface.Wind);
        hasher.Add(surface.Subsurface.Radius).Add(surface.Subsurface.Color).Add(surface.Subsurface.Transmission);
        if (surface.Shader is { } shader)
        {
            hasher.Add(1).Add(((ShaderItem)items[shader.Index - 1]).Name);
        }
        else
        {
            hasher.Add(0);
        }

        hasher.Add(surface.Parameters.Length);
        foreach (var parameter in surface.Parameters)
        {
            hasher.Add(parameter.X).Add(parameter.Y).Add(parameter.Z).Add(parameter.W);
        }

        hasher.Add(surface.ShaderTextures.Length);
        foreach (var texture in surface.ShaderTextures)
        {
            AddTexture(hasher, texture, items);
        }

        hasher.Add(surface.ShaderArrays.Length);
        foreach (var array in surface.ShaderArrays)
        {
            var layers = ((TextureArrayItem)items[array.Index - 1]).Layers;
            hasher.Add(layers.Length);
            foreach (var layer in layers)
            {
                AddTexture(hasher, layer);
            }
        }
    }

    private static void AddTexture(StableHasher hasher, TextureRef? texture, IReadOnlyList<object> items)
    {
        if (texture is { } named)
        {
            hasher.Add(1);
            AddTexture(hasher, (TextureItem)items[named.Index - 1]);
        }
        else
        {
            hasher.Add(0);
        }
    }

    private static void AddTexture(StableHasher hasher, TextureItem texture)
    {
        if (texture.Name is { } name)
        {
            hasher.Add(1).Add(texture.Files.HashOf(name));
        }
        else if (texture.Generated is { } generated)
        {
            hasher.Add(2).Add(generated.Size).Add(generated.Pixels);
            hasher.Add((int)texture.Kind).Add(texture.NormalMap ? 1 : 0);
        }
        else
        {
            hasher.Add(0);
        }
    }
}
