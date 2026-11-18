using System.Numerics;
using System.Security.Cryptography;
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
    /// <summary>
    /// The instance hash. The model's identity covers its meshes and materials, so the part number is all a part adds.
    /// <paramref name="chunk"/> is the name of the chunk that owns the draw, or null for none: the renderer finds a copy's
    /// lightmap by its id, so the same model at the same spot in two kinds of chunk must not share one.
    /// </summary>
    public static ulong Instance(StableHasher hasher, ulong model, Vector3? tint, in Matrix4x4 relative, string? chunk = null)
    {
        hasher.Reset();
        hasher.Add(model);
        // Only a chunk's draws add it, so a space's ids, and the space bakes made from them, stay as they were.
        if (chunk is not null)
        {
            hasher.Add(chunk);
        }

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
    /// <paramref name="world"/> as seen from a rigid chunk: <see cref="BakeAreas.Relative"/>, the rule the chunk's bake
    /// moves its draws by too.
    /// </summary>
    public static Matrix4x4 Relative(in Matrix4x4 world, in Matrix4x4 chunk) => BakeAreas.Relative(world, chunk);

    /// <summary>
    /// Sets the bake id of each shown part of a static draw. <paramref name="partBounds"/> holds every part of the model,
    /// shown or not, in the model's units; <paramref name="mask"/> picks the shown ones, in order, as
    /// <see cref="MeshMaskParts.AddShown"/> copied them.
    /// </summary>
    public static void Assign(
        Span<DrawItem> shown, MeshMask? mask, ReadOnlySpan<BakeSphere> partBounds, in BakeIdDraw draw, in BakeIdAreas areas)
    {
        var spheres = areas.Scratch.Spheres(shown.Length);
        int s = 0;
        for (int p = 0; p < partBounds.Length; p++)
        {
            if (mask is null || mask.Shows(p))
            {
                spheres[s++] = BakeAreas.Transform(partBounds[p], draw.Transform);
            }
        }

        var chunks = areas.Chunks;
        int owner = BakeAreas.OwnerOfModel(spheres, areas.Space, chunks);
        var placed = owner >= 0 ? Relative(draw.Transform, chunks[owner].Transform) : draw.Transform;
        ulong instance = Instance(areas.Scratch.Hasher, draw.Model, draw.Tint, placed, owner >= 0 ? chunks[owner].Name : null);

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
    public static ulong Model(ModelRef model, IReadOnlyList<object> items, IContentFiles files, Func<string, byte[]> shaderBytes)
    {
        var hasher = new StableHasher();
        if (model.IsBuiltIn)
        {
            return hasher.Add(0).Add(model.Index == 0 ? "cube" : Shapes.BuiltInNames[-model.Index - 1]).Finish();
        }

        switch (items[model.Index - 1])
        {
            case string name:
                hasher.Add(1).Add(files.HashOf(name));
                AddCookedReferences(hasher, name, files, shaderBytes);
                return hasher.Finish();
            case GeneratedMesh mesh:
                hasher.Add(2);
                AddMesh(hasher, mesh, items, shaderBytes);
                return hasher.Finish();
            default:
                throw new InvalidOperationException($"Item {model.Index} is not a model, so it can't be drawn static.");
        }
    }

    /// <summary>
    /// What a cooked model names rather than holds: the contents of each surface shader (its compiled bytes) and the
    /// hash of each texture those shaders read. The model's own file hash already covers the textures it points to by hash.
    /// </summary>
    private static void AddCookedReferences(StableHasher hasher, string name, IContentFiles files, Func<string, byte[]> shaderBytes)
    {
        ModelData data;
        using (var stream = files.Open(name))
        {
            // Only the names matter here, so a texture kept by hash isn't decoded.
            data = ModelFile.Read(stream, name, _ => TextureData.FromPixels(1, 1, TextureKind.Color, new byte[4]));
        }

        var shaders = data.MaterialShaders.OfType<ModelMaterialShader>().ToArray();
        hasher.Add(shaders.Length);
        foreach (var shader in shaders)
        {
            hasher.Add(shader.Name).Add(HashBytes(shaderBytes(shader.Name)));
            hasher.Add(shader.Textures.Length);
            foreach (string texture in shader.Textures)
            {
                hasher.Add(texture).Add(files.HashOf(texture));
            }
        }
    }

    private static string HashBytes(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static void AddMesh(StableHasher hasher, GeneratedMesh mesh, IReadOnlyList<object> items, Func<string, byte[]> shaderBytes)
    {
        AddGeometry(hasher, mesh);
        AddSurfaceValues(hasher, mesh.Surface, items);
        AddShaderInputs(hasher, mesh.Surface, items, shaderBytes);
        AddLaterLooks(hasher, mesh.Surface, items);
    }

    // The mesh's vertices and indices, whether it may be simplified, and the texture it owns.
    private static void AddGeometry(StableHasher hasher, GeneratedMesh mesh)
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
    }

    // The surface's own values and the textures it names directly.
    private static void AddSurfaceValues(StableHasher hasher, Surface surface, IReadOnlyList<object> items)
    {
        hasher.Add(surface.Color).Add(surface.Roughness).Add(surface.Metallic);
        AddTexture(hasher, surface.ColorTexture, items);
        AddTexture(hasher, surface.NormalTexture, items);
        AddTexture(hasher, surface.SurfaceTexture, items);
        hasher.Add(surface.TileWidth is null ? 0 : 1).Add(surface.TileWidth ?? 0f);
        hasher.Add(surface.Opacity).Add(surface.Ior);
        AddTexture(hasher, surface.Height?.Map, items);
        hasher.Add(surface.Height?.Depth ?? 0f);
        AddTexture(hasher, surface.Detail?.ColorTexture, items);
        AddTexture(hasher, surface.Detail?.NormalTexture, items);
        hasher.Add((surface.Detail ?? new DetailMap()).Scale);
        hasher.Add(surface.Detail is { Mask: true } ? 1 : 0).Add(surface.Features.HasFlag(SurfaceFeatures.WorldVariation) ? 1 : 0).Add(surface.Features.HasFlag(SurfaceFeatures.Dithered) ? 1 : 0);
        hasher.Add(surface.Features.HasFlag(SurfaceFeatures.Puddles) ? 1 : 0).Add(surface.Foliage is null ? 0 : 1);
        hasher.Add((surface.Foliage ?? new Foliage()).Translucency).Add(surface.Wind);
        hasher.Add(surface.Subsurface.Radius).Add(surface.Subsurface.Color).Add(surface.Subsurface.Transmission);
    }

    // The surface shader, by its compiled bytes, and the parameters, textures and texture arrays it reads.
    private static void AddShaderInputs(StableHasher hasher, Surface surface, IReadOnlyList<object> items, Func<string, byte[]> shaderBytes)
    {
        if (surface.Shader is { } shader)
        {
            string shaderName = ((ShaderItem)items[shader.Fragment.Index - 1]).Name;
            hasher.Add(1).Add(shaderName).Add(HashBytes(shaderBytes(shaderName)));
        }
        else
        {
            hasher.Add(0);
        }

        hasher.Add(surface.Shader?.Parameters.Length ?? 0);
        foreach (var parameter in surface.Shader?.Parameters ?? [])
        {
            hasher.Add(parameter.X).Add(parameter.Y).Add(parameter.Z).Add(parameter.W);
        }

        hasher.Add(surface.Shader?.Textures.Length ?? 0);
        foreach (var texture in surface.Shader?.Textures ?? [])
        {
            AddTexture(hasher, texture, items);
        }

        hasher.Add(surface.Shader?.Arrays.Length ?? 0);
        foreach (var array in surface.Shader?.Arrays ?? [])
        {
            var layers = ((TextureArrayItem)items[array.Index - 1]).Layers;
            hasher.Add(layers.Length);
            foreach (var layer in layers)
            {
                AddTexture(hasher, layer);
            }
        }
    }

    // What a surface has gained since bakes were first committed: occlusion, toon shading and outlines. Each adds to the
    // hash only when the surface has it, so every mesh made before it existed keeps its hash and its bake.
    private static void AddLaterLooks(StableHasher hasher, Surface surface, IReadOnlyList<object> items)
    {
        if (surface.Occlusion is { } occlusion)
        {
            AddTexture(hasher, occlusion.Map, items);
            hasher.Add(occlusion.OnUv2 ? 1 : 0);
        }

        if (surface.Toon is { } toon)
        {
            hasher.Add(toon.Steps).Add(toon.Rim);
            AddTexture(hasher, toon.Ramp, items);
        }

        if (surface.Features.HasFlag(SurfaceFeatures.Outline))
        {
            hasher.Add("outline");
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

/// <summary>What a static draw is, for its bake ids: the model's identity, the tint and where it is in the world.</summary>
internal readonly record struct BakeIdDraw(ulong Model, Vector3? Tint, Matrix4x4 Transform);

/// <summary>
/// The frame's baked space and chunks, which a static draw's ids are made relative to, and the scratch the ids are
/// worked out with.
/// </summary>
internal readonly ref struct BakeIdAreas(BakedSpace? space, ReadOnlySpan<BakedChunk> chunks, BakeIdScratch scratch)
{
    public BakedSpace? Space { get; } = space;

    public ReadOnlySpan<BakedChunk> Chunks { get; } = chunks;

    public BakeIdScratch Scratch { get; } = scratch;
}

/// <summary>What a frame reuses for every static draw, so giving bake ids makes no garbage.</summary>
internal sealed class BakeIdScratch
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
