using Rendition;

namespace Super;

/// <summary>
/// Hands what <see cref="ItemReads"/> read to the GPU, on the main thread, and frees it when the content unloads the
/// item. Generated meshes share a material for each surface; surface shaders named by cooked models are made once for
/// each name.
/// </summary>
internal sealed class ItemUploads(Renderer renderer, UploadedItems items)
{
    /// <summary>The materials of generated meshes without textures of their own, one for each surface.</summary>
    private readonly SharedMaterials _meshMaterials = new(renderer.Assets.DestroyMaterial);

    /// <summary>Surface shaders named by cooked models, made once for each name and kept for the renderer's life.</summary>
    private readonly Dictionary<string, SurfaceShaderHandle> _modelShaders = [];

    public ItemOnGpu Upload(object prepared, Content content) => prepared switch
    {
        ModelData model => new ModelOnGpu(renderer.Assets.CreateModel(model), null, null, model.Skeleton?.Count ?? 0),
        PreparedModel model => UploadModel(model),
        PreparedShader shader => new ShaderOnGpu(renderer.Assets.CreateSurfaceShader(shader.Spirv, shader.Name + ".frag")),
        TextureData texture => new TextureOnGpu(renderer.Assets.CreateTexture(texture)),
        TextureData[] layers => new TextureArrayOnGpu(renderer.Assets.CreateTextureArray(layers)),
        SkyImageData sky => new SkyOnGpu(renderer.CreateSkyImage(sky)),
        PreparedMesh(var mesh, var owned) => UploadMesh(mesh, owned, content),
        UnloadedItem => ItemLetGo.Instance,
        _ => throw new InvalidOperationException($"Content can't upload a {prepared.GetType().Name}."),
    };

    /// <summary>Frees what each item the content let go of made, then clears the content's list of them.</summary>
    public void UnloadPending(Content content)
    {
        foreach (int index in content.Store.PendingUnloads)
        {
            if (items.Take(index, content) is { } loaded)
            {
                Destroy(loaded);
            }
        }

        content.Store.ClearPendingUnloads();
    }

    /// <summary>
    /// Uploads a model whose materials use surface shaders. A shader is made once for each name and
    /// kept for the renderer's life, as shaders named through <see cref="Content.Shader"/> are. The
    /// textures are the model's own, since destroying a model frees every texture its materials name.
    /// </summary>
    private ModelOnGpu UploadModel(PreparedModel prepared)
    {
        foreach (var (name, spirv) in prepared.Shaders)
        {
            if (!_modelShaders.ContainsKey(name))
            {
                _modelShaders[name] = renderer.Assets.CreateSurfaceShader(spirv, name + ".frag");
            }
        }

        var textures = prepared.Textures.ToDictionary(pair => pair.Key, pair => renderer.Assets.CreateTexture(pair.Value));
        var model = prepared.Model;
        var parts = renderer.Assets.CreateModel(model, (index, material) =>
        {
            if (model.ShaderOf(index) is not { } shader)
            {
                return material;
            }

            material.Custom.Shader = _modelShaders[shader.Name];
            for (int i = 0; i < shader.Parameters.Length; i++)
            {
                material.Custom.Parameters[i] = shader.Parameters[i];
            }

            for (int i = 0; i < shader.Textures.Length; i++)
            {
                material.Custom.Textures[i] = textures[shader.Textures[i]];
            }

            return material;
        });
        return new ModelOnGpu(parts, null, null, model.Skeleton?.Count ?? 0);
    }

    /// <summary>
    /// A generated mesh's material, built from what its surface named. Meshes with the same surface share one; a mesh
    /// with its own texture gets its own.
    /// </summary>
    private ModelOnGpu UploadMesh(GeneratedMesh mesh, TextureData? owned, Content content)
    {
        var surface = mesh.Surface;
        TextureHandle? ownedTexture = owned is null ? null : renderer.Assets.CreateTexture(owned);
        if (ownedTexture is not null || !_meshMaterials.TryShare(surface, out var handle))
        {
            handle = renderer.Assets.CreateMaterial(MeshMaterial(surface, ownedTexture, content));
            if (ownedTexture is null)
            {
                _meshMaterials.Add(surface, handle);
            }
        }

        try
        {
            var part = renderer.Assets.CreateDrawItem(mesh.Vertices, mesh.Indices, handle, mesh.Simplify);
            return new ModelOnGpu([part], handle, ownedTexture);
        }
        catch when (ownedTexture is null)
        {
            _meshMaterials.Release(handle);
            throw;
        }
    }

    /// <summary>The material a generated mesh draws with: its surface's values, textures and shader.</summary>
    private Material MeshMaterial(Surface surface, TextureHandle? ownedTexture, Content content)
    {
        var material = SurfaceMaterials.MaterialOf(surface) with
        {
            Textures = TexturesOf(surface, ownedTexture, content),
            Height = new MaterialHeight { Texture = items.TextureFor(surface.Height?.Map, content), Depth = surface.Height?.Depth ?? 0f },
            Detail = new MaterialDetail
            {
                Color = items.TextureFor(surface.Detail?.ColorTexture, content),
                Normal = items.TextureFor(surface.Detail?.NormalTexture, content),
                Scale = (surface.Detail ?? new DetailMap()).Scale,
            },
            Toon = surface.Toon is { } toon
                ? new ToonShading { Steps = toon.Steps, Ramp = items.TextureFor(toon.Ramp, content), Rim = toon.Rim }
                : null,
            Subsurface = surface.Subsurface,
            Custom = new CustomSurface
            {
                Shader = surface.Shader is { } shader ? items.Require<ShaderOnGpu>(shader.Fragment.Index, content).Handle : null,
            },
        };
        var custom = surface.Shader ?? new SurfaceShader(default);
        custom.Parameters.CopyTo(material.Custom.Parameters);
        for (int i = 0; i < custom.Textures.Length; i++)
        {
            material.Custom.Textures[i] = items.TextureFor(custom.Textures[i], content);
        }

        for (int i = 0; i < custom.Arrays.Length; i++)
        {
            material.Custom.Arrays[i] = items.Require<TextureArrayOnGpu>(custom.Arrays[i].Index, content).Handle;
        }

        return material;
    }

    /// <summary>A generated mesh's texture maps: its own colour texture when it has one, or its surface's.</summary>
    private MaterialTextures TexturesOf(Surface surface, TextureHandle? ownedTexture, Content content) => new()
    {
        BaseColor = ownedTexture ?? items.TextureFor(surface.ColorTexture, content),
        Normal = items.TextureFor(surface.NormalTexture, content),
        Surface = items.TextureFor(surface.SurfaceTexture, content),
        Occlusion = items.TextureFor(surface.Occlusion?.Map, content),
    };

    /// <summary>Frees what an item made. A model from a file owns its materials and textures; a generated mesh owns one of each.</summary>
    private void Destroy(ItemOnGpu loaded)
    {
        switch (loaded)
        {
            case ModelOnGpu { Material: { } material } generated:
                renderer.Assets.DestroyMesh(generated.Parts[0].Mesh);
                if (generated.OwnedTexture is { } owned)
                {
                    renderer.Assets.DestroyMaterial(material);
                    renderer.Assets.DestroyTexture(owned);
                }
                else
                {
                    _meshMaterials.Release(material);
                }

                break;
            case ModelOnGpu model:
                renderer.Assets.DestroyModel(model.Parts);
                break;
            case TextureOnGpu texture:
                renderer.Assets.DestroyTexture(texture.Handle);
                break;
            case ItemLetGo:
                break;
            default:
                throw new InvalidOperationException($"Content can't unload a {loaded.GetType().Name}.");
        }
    }
}
