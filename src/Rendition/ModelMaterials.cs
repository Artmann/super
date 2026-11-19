namespace Rendition;

/// <summary>Turns a cooked model's materials into the renderer's, for <see cref="RendererAssets.CreateModel"/>.</summary>
internal static class ModelMaterials
{
    /// <summary>
    /// The material a cooked model's material makes, with its texture indices turned into <paramref name="textures"/>'
    /// handles and -1 for none. The mask and occlusion textures come separately, from <see cref="ModelData.MaskOf"/>
    /// and <see cref="ModelData.OcclusionOf"/>.
    /// </summary>
    public static Material MaterialOf(in ModelMaterial source, ReadOnlySpan<TextureHandle> textures)
    {
        static TextureHandle? Texture(int index, ReadOnlySpan<TextureHandle> textures) => index >= 0 ? textures[index] : null;

        return new Material
        {
            BaseColor = source.BaseColor,
            Metallic = source.Metallic,
            Roughness = source.Roughness,
            AlphaCutoff = source.AlphaCutoff,
            Textures = new MaterialTextures
            {
                BaseColor = Texture(source.BaseColorTexture, textures),
                Normal = Texture(source.NormalTexture, textures),
                Surface = Texture(source.SurfaceTexture, textures),
                Emissive = Texture(source.EmissiveTexture, textures),
            },
            Emissive = source.Emissive,
            Clearcoat = source.Clearcoat,
            Height = new MaterialHeight { Texture = Texture(source.HeightTexture, textures), Depth = source.HeightDepth },
            Detail = new MaterialDetail
            {
                Color = Texture(source.DetailColorTexture, textures),
                Normal = Texture(source.DetailNormalTexture, textures),
                Scale = source.DetailScale,
            },
            Flags = source.Flags,
            Glass = new MaterialGlass { Ior = source.Ior },
            Subsurface = source.Subsurface,
            Translucency = source.Translucency,
            Wind = source.Wind,
        };
    }

    /// <summary>
    /// The material <paramref name="model"/>'s material <paramref name="index"/> makes, with its weathering mask,
    /// occlusion texture and toon shading, which the cooked material holds apart.
    /// </summary>
    public static Material Finished(ModelData model, int index, ReadOnlySpan<TextureHandle> textures)
    {
        var material = MaterialOf(model.Materials[index], textures);
        if (model.MaskOf(index) is { } masks)
        {
            material.Weathering = new MaterialWeathering { Mask = textures[masks.Texture], Wear = masks.Wear, Dirt = masks.Dirt };
        }

        if (model.OcclusionOf(index) is >= 0 and int occlusion)
        {
            material.Textures.Occlusion = textures[occlusion];
        }

        if (model.ToonOf(index) is { } toon)
        {
            material.Toon = new ToonShading { Steps = toon.Steps, Ramp = toon.Ramp >= 0 ? textures[toon.Ramp] : null, Rim = toon.Rim };
        }

        return material;
    }
}
