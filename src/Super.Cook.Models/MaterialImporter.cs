using System.Numerics;
using Rendition;
using Super.Cook.Materials;
using Assimp = Silk.NET.Assimp.Assimp;
using AssimpMaterial = Silk.NET.Assimp.Material;

namespace Super.Cook.Models;

/// <summary>
/// Turns one of a model's materials into a <see cref="ModelMaterial"/>, by the first rule that matches its name. A rule's
/// graph makes its textures when it has one, and otherwise they're the files the rule names or the model file links.
/// </summary>
internal sealed unsafe class MaterialImporter
{
    // How many times detail textures repeat for each repeat of the others, unless a rule or a detail graph says.
    private const float DefaultDetailScale = 8f;

    // The flags a rule's switches set.
    private static readonly (Func<MaterialRule, bool> Asks, MaterialFlags Flag)[] RuleFlags =
    [
        (rule => rule.TwoSided || rule.Foliage is not null, MaterialFlags.TwoSided),
        (rule => rule.Transparent, MaterialFlags.Transparent),
        (rule => rule.Foliage is not null, MaterialFlags.Foliage),
        (rule => rule.DetailMask, MaterialFlags.DetailMask),
        (rule => rule.WorldVariation, MaterialFlags.WorldVariation),
        (rule => rule.Dithered, MaterialFlags.Dithered),
        (rule => rule.Puddles, MaterialFlags.Puddles),
        (rule => rule.Outline, MaterialFlags.Outline),
        (rule => rule.NoDecals, MaterialFlags.NoDecals),
    ];

    private readonly ModelCook _cook;
    private readonly MaterialRule _rule;
    private readonly string _name;

    // A texture the rule names must be there; one only the model file links to may not be. This is the material when a
    // rule matched it, and null otherwise.
    private readonly string? _named;

    private MaterialImporter(ModelCook cook, string name)
    {
        var rule = cook.RuleFor(name);
        _cook = cook;
        _rule = rule ?? new MaterialRule();
        _name = name;
        _named = rule is null ? null : name;
    }

    /// <summary>
    /// Imports one material. <paramref name="graphWidth"/> is the width in metres of its graph, or 0 when it has none.
    /// What's wrong with it is reported.
    /// </summary>
    public static ModelMaterial Import(
        Assimp assimp, AssimpMaterial* source, ModelCook cook, out string materialName, out float graphWidth)
    {
        materialName = AssimpMaterials.NameOf(assimp, source);
        var importer = new MaterialImporter(cook, materialName);
        var graph = GraphRules.RunGraph(importer._rule, materialName, cook);
        graphWidth = graph?.Width ?? 0f;
        if (graph is not null)
        {
            return importer.FromGraph(graph);
        }

        if (importer._rule.Graph is null)
        {
            // Reports that a detail graph needs a main graph.
            GraphRules.RunDetailGraph(importer._rule, materialName, cook);
        }

        return importer.FromFile(AssimpMaterials.Read(assimp, source, cook.IsGltf));
    }

    /// <summary>What a material takes from its rule alone, whether a graph or files make its textures.</summary>
    private ModelMaterial FromRule()
    {
        var flags = RuleFlags.Where(entry => entry.Asks(_rule)).Aggregate(default(MaterialFlags), (all, entry) => all | entry.Flag);
        var emissive = _rule.EmissiveColor is { Length: 3 } e
            ? new Vector3(e[0], e[1], e[2])
            : _rule.Emissive != null ? Vector3.One : Vector3.Zero;
        return new ModelMaterial
        {
            Emissive = emissive,
            Clearcoat = _rule.Clearcoat,
            Flags = flags,
            Ior = _rule.Ior,
            Subsurface = Subsurface(_rule),
            Translucency = _rule.Foliage?.Translucency ?? 0f,
            Wind = _rule.Wind?.Sway ?? 0f,
        };
    }

    /// <summary>
    /// A material whose graph made its textures. They hold the colour, metalness and roughness, which the material's
    /// values multiply, so those are 1 unless the rule says otherwise. The model file's own values were for its own textures.
    /// </summary>
    private ModelMaterial FromGraph(GraphRun graph)
    {
        var textures = _cook.Textures;
        int size = graph.Size;
        var (colorTexture, surfaceTexture) = GraphColorAndSurface(graph);
        var (heightTexture, heightDepth) = GraphHeight(graph);
        var (detailColor, detailNormal, detailScale) = DetailFromGraph(graph);
        return FromRule() with
        {
            BaseColor = _rule.Color is { Length: 3 } gc ? new Vector4(gc[0], gc[1], gc[2], _rule.Opacity) : new Vector4(1f, 1f, 1f, _rule.Opacity),
            Metallic = _rule.Metallic ?? 1f,
            Roughness = _rule.Roughness ?? 1f,
            AlphaCutoff = _rule.AlphaCutoff ?? 0f,
            BaseColorTexture = colorTexture,
            NormalTexture = _rule.NoNormalMap
                ? -1
                : textures.AddPixels(graph.Key + "#normal", size, size, TextureKind.Linear, graph.Pixels.Normal, isNormalMap: true),
            SurfaceTexture = surfaceTexture,
            EmissiveTexture = textures.Add(_rule.Emissive, TextureKind.Color, namedBy: _named),
            HeightTexture = heightTexture,
            HeightDepth = heightDepth,
            DetailColorTexture = detailColor,
            DetailNormalTexture = detailNormal,
            DetailScale = detailScale,
        };
    }

    /// <summary>
    /// The graph's colour and surface textures. A cutoff reads the graph's opacity from the colour's alpha, so it's moved
    /// there from the surface's, which is left whole so the opacity isn't counted twice.
    /// </summary>
    private (int Color, int Surface) GraphColorAndSurface(GraphRun graph)
    {
        var textures = _cook.Textures;
        int size = graph.Size;
        if (_rule.AlphaCutoff is { } cutoff)
        {
            var (color, surface) = OpacityInColor(graph.Pixels);
            return (
                textures.AddPixels(graph.Key + "#color-opacity", size, size, TextureKind.Color, color,
                    alphaCutoff: CoverageClaims.TextureCutoff(cutoff, _rule.Opacity)),
                textures.AddPixels(graph.Key + "#surface-opaque", size, size, TextureKind.Linear, surface));
        }

        return (
            textures.AddPixels(graph.Key + "#color", size, size, TextureKind.Color, graph.Pixels.Color),
            textures.AddPixels(graph.Key + "#surface", size, size, TextureKind.Linear, graph.Pixels.Surface));
    }

    /// <summary>The graph's own height map sinks its grooves in with parallax, unless the rule gives a height map.</summary>
    private (int Texture, float Depth) GraphHeight(GraphRun graph)
    {
        float depth = _rule.HeightDepth ?? (_rule.Height is null ? graph.HeightDepth : 0f);
        if (_rule.Height is not null)
        {
            return (_cook.Textures.Add(_rule.Height, TextureKind.Linear, namedBy: _named), depth);
        }

        return (depth > 0f ? _cook.Textures.AddPixels(graph.Key + "#height", graph.Size, graph.Size, TextureKind.Linear, graph.Pixels.Height) : -1, depth);
    }

    /// <summary>
    /// A graph material's detail textures: the detail graph's, or else the rule's. A detail graph tiles at its own width,
    /// which sets how many times it repeats across the main graph.
    /// </summary>
    private (int Color, int Normal, float Scale) DetailFromGraph(GraphRun graph)
    {
        var textures = _cook.Textures;
        if (GraphRules.RunDetailGraph(_rule, _name, _cook) is not { } detail)
        {
            return (
                textures.Add(_rule.DetailColor, TextureKind.Linear, namedBy: _named),
                textures.Add(_rule.DetailNormal, TextureKind.Linear, isNormalMap: true, namedBy: _named),
                _rule.DetailScale ?? DefaultDetailScale);
        }

        return (
            textures.AddPixels(detail.Key + "#detail", detail.Size, detail.Size, TextureKind.Linear, DetailColorPixels(detail.Pixels.Color)),
            textures.AddPixels(detail.Key + "#normal", detail.Size, detail.Size, TextureKind.Linear, detail.Pixels.Normal, isNormalMap: true),
            _rule.DetailScale ?? graph.Width / detail.Width);
    }

    /// <summary>A material whose textures are files, the ones the rule names or else the ones the model file links.</summary>
    private ModelMaterial FromFile(FileMaterial file)
    {
        var baseColor = _rule.Color is { Length: 3 } c
            ? new Vector4(c[0], c[1], c[2], _rule.Opacity)
            : file.BaseColor is { } f ? new Vector4(f[0], f[1], f[2], f[3]) : new Vector4(1f, 1f, 1f, _rule.Opacity);
        float alphaCutoff = _rule.AlphaCutoff ?? file.Cutoff ?? 0f;
        var material = FromRule() with
        {
            BaseColor = baseColor,
            Metallic = _rule.Metallic ?? file.Metallic ?? 0f,
            Roughness = _rule.Roughness ?? file.Roughness ?? 0.6f,
            AlphaCutoff = alphaCutoff,
        };
        return WithFileTextures(material, file, CoverageClaims.TextureCutoff(alphaCutoff, baseColor.W));
    }

    /// <summary>
    /// <paramref name="material"/> with its textures from files. When the colour and the surface both carry opacity, the
    /// shader cuts by their product, so the colour keeps the product's coverage and the surface is left whole.
    /// </summary>
    private ModelMaterial WithFileTextures(ModelMaterial material, FileMaterial file, float textureCutoff)
    {
        var textures = _cook.Textures;
        var partner = _rule.Surface is not null && textureCutoff > 0f
            ? textures.CutoutPartner(_rule.BaseColor ?? file.Color, _rule.Alpha, _rule.Surface)
            : null;
        return material with
        {
            BaseColorTexture = textures.Add(_rule.BaseColor ?? file.Color, TextureKind.Color, alphaFile: _rule.Alpha,
                namedBy: _rule.BaseColor is null ? null : _named, alphaCutoff: textureCutoff, partner: partner),
            NormalTexture = textures.Add(_rule.NoNormalMap ? null : _rule.Normal ?? file.Normal, TextureKind.Linear, isNormalMap: true,
                namedBy: _rule.Normal is null ? null : _named),
            // A glTF surface map's alpha is always whole, so only a surface texture the rule names can carry opacity.
            SurfaceTexture = _rule.Surface is not null
                ? textures.Add(_rule.Surface, TextureKind.Linear, namedBy: _named, alphaCutoff: partner is null ? textureCutoff : 0f)
                : textures.Add(file.Surface, TextureKind.Linear, isGltfSurface: true),
            EmissiveTexture = textures.Add(_rule.Emissive, TextureKind.Color, namedBy: _named),
            HeightTexture = textures.Add(_rule.Height, TextureKind.Linear, namedBy: _named),
            HeightDepth = _rule.HeightDepth ?? 0f,
            DetailColorTexture = textures.Add(_rule.DetailColor, TextureKind.Linear, namedBy: _named),
            DetailNormalTexture = textures.Add(_rule.DetailNormal, TextureKind.Linear, isNormalMap: true, namedBy: _named),
            DetailScale = _rule.DetailScale ?? DefaultDetailScale,
        };
    }

    /// <summary>
    /// A detail graph's colour as a detail colour texture: grey, its brightness divided by the
    /// average, so 128 is the average and the main graph's colour is kept on the whole.
    /// </summary>
    private static byte[] DetailColorPixels(byte[] color)
    {
        int count = color.Length / 4;
        var brightness = new float[count];
        double total = 0;
        for (int i = 0; i < count; i++)
        {
            brightness[i] = 0.2126f * ToLinear(color[i * 4]) + 0.7152f * ToLinear(color[i * 4 + 1]) + 0.0722f * ToLinear(color[i * 4 + 2]);
            total += brightness[i];
        }

        float average = (float)Math.Max(total / count, 1e-4);
        var pixels = new byte[color.Length];
        for (int i = 0; i < count; i++)
        {
            byte value = (byte)Math.Clamp((int)MathF.Round(brightness[i] / average * 128f), 0, 255);
            pixels[i * 4] = value;
            pixels[i * 4 + 1] = value;
            pixels[i * 4 + 2] = value;
            pixels[i * 4 + 3] = 255;
        }

        return pixels;

        static float ToLinear(byte value)
        {
            float c = value / 255f;
            return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        }
    }

    /// <summary>A graph's colour with its opacity, from the surface's alpha, as alpha, and its surface with an alpha of 1.</summary>
    private static (byte[] Color, byte[] Surface) OpacityInColor(MaterialPixels pixels)
    {
        byte[] color = (byte[])pixels.Color.Clone();
        byte[] surface = (byte[])pixels.Surface.Clone();
        for (int alpha = 3; alpha < color.Length; alpha += 4)
        {
            color[alpha] = surface[alpha];
            surface[alpha] = 255;
        }

        return (color, surface);
    }

    private static Subsurface Subsurface(MaterialRule rule) =>
        rule.Subsurface is { } subsurface
            ? new Subsurface
            {
                Radius = subsurface.Radius,
                Color = subsurface.Color is { Length: 3 } spread ? new Vector3(spread[0], spread[1], spread[2]) : Vector3.Zero,
                Transmission = subsurface.Transmission,
            }
            : default;
}
