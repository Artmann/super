using Rendition;
using Super.Ui;

namespace Super;

/// <summary>
/// The part of loading a content item that needs no GPU: reading and decoding its files. It runs on a background
/// thread, and <see cref="ItemUploads"/> hands the result to the GPU on the main thread.
/// </summary>
internal static class ItemReads
{
    /// <summary>Reads what <paramref name="item"/> names, with cooked models read from <paramref name="files"/>.</summary>
    public static object Prepare(object item, IContentFiles files) => item switch
    {
        string name => PrepareModel(name, files),
        ShaderItem shader => new PreparedShader(shader.Name, LoadShader(shader.Name)),
        GameImage image => LoadImage(image),
        GeneratedMesh mesh => PrepareMesh(mesh),
        UnloadedItem unloaded => unloaded, // Let go before the window saw it.
        _ => ReadPicture(item),
    };

    /// <summary>Reads a texture, a texture array, a sky's picture or a colour table.</summary>
    private static object ReadPicture(object item) => item switch
    {
        TextureItem texture => LoadTexture(texture),
        TextureArrayItem array => Array.ConvertAll(array.Layers, LoadTexture),
        SkyItem sky => LoadSky(sky),
        LutItem lut => LoadLut(lut),
        _ => throw new InvalidOperationException($"Content can't load a {item.GetType().Name}."),
    };

    /// <summary>
    /// Reads a named texture and builds its mips, or takes the ones it was cooked with when it's
    /// compressed. A cooked texture's kind, normal-map flag and format come from its header, as its
    /// cook settings set them.
    /// </summary>
    internal static TextureData LoadTexture(TextureItem item)
    {
        if (item.Generated is { } generated)
        {
            return TextureData.FromPixels(generated.Size, generated.Size, item.Kind, generated.Pixels, item.NormalMap);
        }

        return CookedTexture.Read(item.Files.Read(item.Name!), item.Name!).ToTextureData();
    }

    /// <summary>
    /// Reads an interface image and builds its mips. A compressed one is decoded first, because an
    /// image's mips weigh each pixel by its alpha, so a clear pixel lends no colour to its neighbours,
    /// which the cooked mips don't.
    /// </summary>
    internal static TextureData LoadImage(GameImage image)
    {
        if (image.Pixels is { } pixels)
        {
            return new TextureData
            {
                Width = image.Width,
                Height = image.Height,
                Kind = TextureKind.Color,
                Mips = ImageMips.Build(image.Width, image.Height, pixels),
            };
        }

        var cooked = CookedTexture.Read(image.Cooked!, image.Path);
        return new TextureData
        {
            Width = cooked.Width,
            Height = cooked.Height,
            Kind = TextureKind.Color,
            Mips = ImageMips.Build(cooked.Width, cooked.Height, cooked.DecodePixels()),
        };
    }

    /// <summary>Reads a cooked model as a stream, so it's never held whole beside what it builds.</summary>
    internal static ModelData LoadModel(string name, IContentFiles files)
    {
        using var stream = files.Open(name);
        return ModelFile.Read(stream, name, hash =>
        {
            using var texture = files.OpenObject(hash);
            return TextureData.Read(texture, $"{name}: texture {hash}");
        });
    }

    /// <summary>
    /// Reads a cooked model and, when its materials name surface shaders, those shaders and the
    /// cooked textures they read, by name. <paramref name="readShader"/> reads a shader's SPIR-V by
    /// name, from the <c>shaders</c> folder beside the program unless a test gives another.
    /// </summary>
    internal static object PrepareModel(string name, IContentFiles files, Func<string, byte[]>? readShader = null)
    {
        var model = LoadModel(name, files);
        var shaders = model.MaterialShaders.OfType<ModelMaterialShader>().ToArray();
        if (shaders.Length == 0)
        {
            return model;
        }

        var spirv = new Dictionary<string, byte[]>();
        var textures = new Dictionary<string, TextureData>();
        foreach (var shader in shaders)
        {
            if (!spirv.ContainsKey(shader.Name))
            {
                spirv[shader.Name] = (readShader ?? LoadShader)(shader.Name);
            }

            foreach (string texture in shader.Textures)
            {
                if (!textures.ContainsKey(texture))
                {
                    ContentNames.Require(files, texture, ContentKind.Texture);
                    textures[texture] = LoadTexture(new TextureItem(texture, null, files, TextureKind.Color, false));
                }
            }
        }

        return new PreparedModel(model, spirv, textures);
    }

    /// <summary>Reads a surface shader's SPIR-V by name, from the <c>shaders</c> folder beside the program.</summary>
    internal static byte[] LoadShader(string name)
    {
        // Shaders.targets keeps the source extension, so Shaders/terrain.frag becomes shaders/terrain.frag.spv.
        string path = Path.Combine(AppContext.BaseDirectory, "shaders", name + ".frag.spv");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Could not find the shader {name}: expected {path}. The build compiles Shaders/{name}.frag to it, when the game uses the Super package or its project imports src/Shaders.targets.",
                path);
        }

        return File.ReadAllBytes(path);
    }

    /// <summary>Reads a photographed sky's faces.</summary>
    private static SkyImageData LoadSky(SkyItem item)
    {
        using var stream = item.Files.Open(item.Name);
        return SkyImageData.Read(stream, item.Name);
    }

    /// <summary>Reads an artist's colour table.</summary>
    private static ColorLutData LoadLut(LutItem item)
    {
        using var stream = item.Files.Open(item.Name);
        return ColorLutData.Read(stream, item.Name);
    }

    /// <summary>A generated mesh, with the pixels of the texture it owns made into one.</summary>
    private static PreparedMesh PrepareMesh(GeneratedMesh mesh) => new(
        mesh,
        mesh.OwnedTexture is { } owned ? TextureData.FromPixels(owned.Size, owned.Size, TextureKind.Color, owned.Pixels) : null);
}

/// <summary>A generated mesh, and the texture it owns when it has one.</summary>
internal sealed record PreparedMesh(GeneratedMesh Mesh, TextureData? OwnedTexture);

/// <summary>A surface shader's compiled code, and its name so live shader reload can read the file again.</summary>
internal sealed record PreparedShader(string Name, byte[] Spirv);

/// <summary>A model whose materials name surface shaders, with the shaders and textures they read.</summary>
internal sealed record PreparedModel(ModelData Model, Dictionary<string, byte[]> Shaders, Dictionary<string, TextureData> Textures);
