namespace Rendition;

/// <summary>
/// What the renderer draws with: meshes, models, textures, materials, surface shaders and the overlay's atlas. Made
/// without waiting for the GPU, and let go of two frames after they're destroyed, when no frame in flight can read
/// them. Reached through <see cref="Renderer.Assets"/>.
/// </summary>
public sealed class RendererAssets
{
    private readonly MeshAssets _meshes;
    private readonly TextureAssets _textures;
    private readonly MaterialAssets _materials;

    internal RendererAssets(MeshAssets meshes, TextureAssets textures, MaterialAssets materials)
    {
        _meshes = meshes;
        _textures = textures;
        _materials = materials;
    }

    /// <summary>
    /// Hands a mesh's bytes to the next frame's command buffer rather than waiting for
    /// the GPU, so it can be drawn in that same frame.
    /// </summary>
    public MeshHandle CreateMesh(ReadOnlySpan<Vertex> vertices, ReadOnlySpan<uint> indices) => _meshes.Create(vertices, indices);

    /// <summary>
    /// Lets go of a mesh and its detail entries. Returns at once; the memory comes back
    /// two frames later, when no frame in flight can read it. Drawing with the handle
    /// after this is a caller mistake that isn't checked.
    /// </summary>
    public void DestroyMesh(MeshHandle mesh) => _meshes.Destroy(mesh);

    /// <summary>
    /// Uploads a mesh as one draw that the renderer can skip when it's out of view, and
    /// simplify when it's small on screen. Pass <paramref name="simplify"/> false for
    /// meshes whose every vertex matters, like terrain chunks that must meet their
    /// neighbours exactly.
    /// </summary>
    public DrawItem CreateDrawItem(
        ReadOnlySpan<Vertex> vertices, ReadOnlySpan<uint> indices, MaterialHandle material, bool simplify = true)
    {
        ModelPart[] parts = [new ModelPart { IndexCount = (uint)indices.Length }];
        return _meshes.CreateDetailed(vertices, indices, parts, [material], simplify)[0];
    }

    /// <summary>
    /// Uploads a cooked model and returns one draw per part. <paramref name="finish"/>, given each
    /// material's index and what the model holds, can change it before it's made, such as to give it
    /// the surface shader <see cref="ModelData.ShaderOf"/> names. <see cref="DestroyModel"/> frees
    /// every texture a material names, its shader textures too, so give each model textures of its own.
    /// </summary>
    public DrawItem[] CreateModel(ModelData model, Func<int, Material, Material>? finish = null)
    {
        var textures = new TextureHandle[model.Textures.Length];
        for (int i = 0; i < textures.Length; i++)
        {
            textures[i] = _textures.Create(model.Textures[i], model.EdgesOf(i));
        }

        var materials = new MaterialHandle[model.Materials.Length];
        for (int i = 0; i < materials.Length; i++)
        {
            var material = ModelMaterials.Finished(model, i, textures);
            materials[i] = _materials.Create(finish is null ? material : finish(i, material));
        }

        // A posed skeleton can reach well outside the rest pose, so a skinned part's
        // culling sphere is doubled rather than culled too early at the screen's edge.
        float radiusScale = model.Skeleton is null ? 1f : 2f;
        return _meshes.CreateDetailed(model.Vertices, model.Indices, model.Parts, materials, radiusScale: radiusScale);
    }

    /// <summary>
    /// Destroys everything <see cref="CreateModel"/> or <see cref="CreateDrawItem"/> made
    /// for these parts: their mesh, their materials and the textures those name. Parts
    /// that share a material, or a material already destroyed, are passed over.
    /// </summary>
    public void DestroyModel(ReadOnlySpan<DrawItem> parts)
    {
        if (parts.IsEmpty)
        {
            return;
        }

        _materials.DestroyOfParts(parts);
        _meshes.Destroy(parts[0].Mesh);
    }

    /// <summary>
    /// The sphere around a part, in the model's units, before its draw's transform. A part without detail is the
    /// point at its origin.
    /// </summary>
    public BakeSphere BoundsOf(DetailHandle detail) => _meshes.BoundsOf(detail);

    /// <summary>
    /// Hands a texture's bytes to the next frame's command buffer rather than waiting for
    /// the GPU, so it can be sampled in that same frame. <paramref name="edges"/> says what is
    /// read past the texture's edge, for every material that uses it: an atlas such as a
    /// <see cref="MaterialWeathering.Mask"/> wants <see cref="TextureEdges.Clamp"/>.
    /// </summary>
    public TextureHandle CreateTexture(TextureData data, TextureEdges edges = TextureEdges.Repeat) => _textures.Create(data, edges);

    /// <summary>
    /// Stacks same-sized images into one texture that shaders read by layer, uploaded the
    /// same way as <see cref="CreateTexture"/>. Every layer must have the first layer's
    /// size, kind, format and mip count.
    /// </summary>
    public TextureArrayHandle CreateTextureArray(ReadOnlySpan<TextureData> layers) => _textures.CreateArray(layers);

    /// <summary>
    /// Lets go of a texture. Its slot reads as white once it's released, for whatever takes
    /// the slot next. A material that still names it must not be drawn after this.
    /// </summary>
    public void DestroyTexture(TextureHandle texture) => _textures.Destroy(texture);

    /// <summary>Lets go of a texture array, with the same rules as <see cref="DestroyTexture"/>.</summary>
    public void DestroyTextureArray(TextureArrayHandle array) => _textures.DestroyArray(array);

    /// <summary>
    /// Replaces the image that <see cref="FrameExtras.Overlay"/> quads read from. Only its
    /// alpha is used. Waits for the GPU, so it isn't for every frame.
    /// </summary>
    public void SetOverlayAtlas(TextureData data) => _textures.SetOverlayAtlas(data);

    /// <summary>Makes a material in one of the limited material slots, after checking what it names exists.</summary>
    public MaterialHandle CreateMaterial(in Material material) => _materials.Create(material);

    /// <summary>
    /// Changes an existing material, for things that change colour during play. Unlike
    /// <see cref="CreateMaterial"/>, this doesn't use up one of the limited material slots.
    /// </summary>
    public void UpdateMaterial(MaterialHandle handle, in Material material) => _materials.Update(handle, material);

    /// <summary>Lets go of a material slot. Draws that still use it are a caller mistake.</summary>
    public void DestroyMaterial(MaterialHandle material) => _materials.Destroy(material);

    /// <summary>
    /// Adds a surface shader from compiled SPIR-V. It decides how a material's surface
    /// looks and is lit like any other. See docs/design.md for how to write one. This is
    /// slow, so call it while loading.
    /// </summary>
    /// <param name="spirv">The compiled code.</param>
    /// <param name="name">
    /// The compiled file the code came from, without <c>.spv</c>, such as <c>terrain.frag</c>, when it is one in the
    /// <c>shaders</c> folder next to the program. <see cref="ReloadShaders"/> then reads it again when it changes.
    /// </param>
    public SurfaceShaderHandle CreateSurfaceShader(byte[] spirv, string? name = null) => _materials.CreateShader(spirv, name);

    /// <summary>
    /// Builds every pipeline that uses one of these shaders again, from the compiled files in the <c>shaders</c>
    /// folder as they are now, and returns how many it built. Names are the files' without <c>.spv</c>, such as
    /// <c>lighting.frag</c>. Needs <see cref="RendererOptions.LiveShaders"/>, and does nothing without it. It waits
    /// for the GPU, so call it between frames. A shader that changed what it reads or writes, not only how, needs
    /// a restart.
    /// </summary>
    public int ReloadShaders(IReadOnlySet<string> names) => _materials.ReloadShaders(names);
}
