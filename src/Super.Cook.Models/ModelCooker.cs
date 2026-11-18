using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Rendition;
using Super.Cook.Materials;

namespace Super.Cook.Models;

/// <summary>
/// Cooks glTF, FBX and OBJ files into <c>.rmodel</c> data with Assimp. Assimp reads the
/// model from disk, so the files it opens on its own (glTF buffers, OBJ material libraries
/// and extra animation files) are tracked by hand, and textures are looked up through
/// <see cref="CookInput"/>. Textures are found by file name beside the model or under it,
/// because model files usually hold absolute paths from the artist's machine.
/// </summary>
public sealed class ModelCooker : ICooker
{
    private static readonly JsonSerializerOptions SettingsOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = true,
    };

    public string Id => "model";

    // 2: "has no UVs" warns only when the material uses a texture. 3: subsurface. 4: height and detail textures.
    // 5: materials gained Ior. 6: parts keep the name of their mesh's node, keepMeshes and keepOrigin, and unskinned
    // meshes follow their container node. 7: textures are objects of their own that models point to by hash.
    // 8: material graphs. 9: graph materials have no height or detail textures unless their rule names them, and an
    // ior of 1. 10: Kaiser mips, roughness widened by the normal map, graph heights as parallax, detail graphs, and
    // block-compressed textures. 11: material shaders, and a graph's opacity in the colour's alpha under a cutoff.
    // 12: vertices keep a second UV set, and materials bake masks for wear and dirt. 13: masks measure height per mesh,
    // carry the wear breakup in alpha and read as clean off their islands. 14: foliage sets its translucency, and
    // materials may sway in the wind. 15: BC7 from the cook's own encoder, and BC4 and BC5 at BCnEncoder's Fast. 16: a
    // cut-out's textures keep their alpha coverage at every mip level. 17: when a cut-out's colour and surface both carry
    // alpha, the colour keeps the coverage of their product and the surface is left whole. 18: BC7 compression keeps a
    // cut-out's coverage too. 19: compression keeps the product's coverage on the colour, and leaves such a surface
    // whole. 20: every model has a second UV set fit for a lightmap, its own when it's usable and an unwrapped one when
    // it isn't. 21: a rig's vertices are in its rest pose. 22: skeletons keep the bounds of what each joint moves.
    // 23: materials take an occlusion texture, the glTF file's or a rule's, on either UV set. 24: a rule's toon and
    // outline. 25: clips carry their events. The
    // graphs' own version is folded in, so a change to Super.Materials cooks every model again, since one may use a graph.
    public int Version { get; } = unchecked(MaterialGraphs.Version * 31 + 26);

    public IReadOnlyList<string> Extensions { get; } = [".glb", ".gltf", ".fbx", ".obj"];

    /// <summary>Models start before the rest, since one with graphs and masks can take longer than all the rest together.</summary>
    public int StartRank => 1;

    public void Cook(CookInput input, CookOutput output)
    {
        if (input.FullPath is null)
        {
            output.Error($"{input.Name}: models can only be cooked from files on disk");
            return;
        }

        var settings = input.SettingsAs<ModelSettings>();
        TrackOwnFiles(input);
        var graphs = new GraphRuns(input);

        if (settings.Parts is null)
        {
            if (CookOne(input, output, settings, "", graphs) is { } bytes)
            {
                output.Add(ContentKind.Model, bytes);
            }

            return;
        }

        if (settings.Parts.Count == 0)
        {
            output.Error($"{input.Name}: parts is empty, so nothing would be cooked.");
            return;
        }

        foreach (string part in settings.Parts.Keys.Order(StringComparer.Ordinal))
        {
            var partSettings = PartSettings(input.Settings, part);
            if (CookOne(input, output, partSettings, $"part {part}: ", graphs) is { } bytes)
            {
                output.AddPart(part, ContentKind.Model, bytes);
            }
        }
    }

    // The files Assimp opens beside the model by itself, which the pipeline can't see.
    private static void TrackOwnFiles(CookInput input)
    {
        string extension = Path.GetExtension(input.Name).ToLowerInvariant();
        if (extension == ".gltf")
        {
            TrackGltfBuffers(input);
        }
        else if (extension == ".obj")
        {
            TrackObjLibraries(input);
        }
    }

    // A glTF file's buffers that are files of their own, not data inside it.
    private static void TrackGltfBuffers(CookInput input)
    {
        JsonNode? gltf;
        try
        {
            gltf = JsonNode.Parse(input.Bytes);
        }
        catch (JsonException)
        {
            return; // Assimp says what's wrong with it.
        }

        if (gltf?["buffers"] is not JsonArray buffers)
        {
            return;
        }

        foreach (var buffer in buffers)
        {
            if (buffer?["uri"] is JsonValue value && value.TryGetValue(out string? uri)
                && !uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                input.Track(Uri.UnescapeDataString(uri));
            }
        }
    }

    // An OBJ file's material libraries.
    private static void TrackObjLibraries(CookInput input)
    {
        foreach (string line in Encoding.UTF8.GetString(input.Bytes).Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("mtllib ", StringComparison.Ordinal) || trimmed.StartsWith("mtllib\t", StringComparison.Ordinal))
            {
                input.Track(trimmed["mtllib".Length..].Trim());
            }
        }
    }

    // The settings beside parts, with the part's own laid over them key by key.
    private static ImportSettings PartSettings(JsonObject settings, string part)
    {
        var merged = new JsonObject();
        JsonObject? partObject = null;
        foreach (var (key, value) in settings)
        {
            if (key.Equals("parts", StringComparison.OrdinalIgnoreCase))
            {
                partObject = value?[part] as JsonObject;
                continue;
            }

            merged[key] = value?.DeepClone();
        }

        foreach (var (key, value) in partObject ?? [])
        {
            foreach (string same in merged.Select(pair => pair.Key).Where(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                merged.Remove(same);
            }

            merged[key] = value?.DeepClone();
        }

        return merged.Deserialize<ImportSettings>(SettingsOptions) ?? new ImportSettings();
    }

    /// <returns>The cooked bytes, or null when it failed and said why through <paramref name="output"/>.</returns>
    private static byte[]? CookOne(CookInput input, CookOutput output, ImportSettings settings, string prefix, GraphRuns graphs)
    {
        bool isGltf = Path.GetExtension(input.FullPath!).ToLowerInvariant() is ".glb" or ".gltf";
        var cook = new ModelCook(input, output, prefix, settings, new ModelTextures(input, output, prefix), graphs, isGltf);
        if (ModelReader.Read(cook) is not { } read)
        {
            return null;
        }

        var eventProblems = ClipEventSettings.Attach(settings.Events, read.Clips, read.Skeleton);
        PointOutOfCrowns(cook, read);

        // Lighting and the masks both read the second UV set, so it's made usable before either. Each material is
        // checked and unwrapped on its own, the way its masks are baked.
        if (LightmapUvs.Make(cook, read.Vertices, read.Meshes, read.Materials.Names) is not { } lightmapped)
        {
            return null;
        }

        var materials = read.Materials;
        int[] occlusion = MaterialExtras.AddOcclusion(cook, materials.Materials, materials.Names, materials.Occlusions, lightmapped.Made);
        if (ModelMasks.Bake(cook, lightmapped.Vertices, read.Meshes, read.Materials.Names) is not { } masks)
        {
            return null;
        }

        var model = ToModel(read, lightmapped.Vertices, masks, occlusion, cook.Textures);
        return Check(cook, model, read.Materials.Names, eventProblems) ? Write(cook, model, masks, occlusion) : null;
    }

    // Foliage normals point out of their crown, unless the rule says crown: false.
    private static void PointOutOfCrowns(ModelCook cook, ReadModel read)
    {
        var (materials, names) = (read.Materials.Materials, read.Materials.Names);
        for (int i = 0; i < materials.Count; i++)
        {
            if ((materials[i].Flags & MaterialFlags.Foliage) != 0 && cook.RuleFor(names[i])?.Foliage?.Crown != false)
            {
                PointOutOfCrown(read.Vertices, read.Meshes.Runs.Where(mesh => mesh.Material == i));
            }
        }
    }

    // Every mesh of one foliage material counts as one crown.
    private static void PointOutOfCrown(Vertex[] vertices, IEnumerable<(int Material, int First, int Count)> meshes)
    {
        var runs = meshes.ToList();
        var crown = runs.SelectMany(run => vertices.Skip(run.First).Take(run.Count)).ToArray();
        FoliageNormals.PointOutOfCrown(crown);

        int next = 0;
        foreach (var run in runs)
        {
            crown.AsSpan(next, run.Count).CopyTo(vertices.AsSpan(run.First, run.Count));
            next += run.Count;
        }
    }

    private static ModelData ToModel(
        ReadModel read, Vertex[] vertices, ModelMaterialMasks?[] masks, int[] occlusion, ModelTextures textures)
    {
        var (meshes, materials) = (read.Meshes, read.Materials);
        return new ModelData
        {
            Vertices = vertices,
            Indices = meshes.Indices.ToArray(),
            Parts = meshes.Parts.ToArray(),
            MeshNames = meshes.MeshNames.ToArray(),
            Materials = materials.Materials.ToArray(),
            MaterialShaders = materials.Shaders.Any(shader => shader is not null) ? materials.Shaders.ToArray() : [],
            MaterialMasks = masks.Any(mask => mask is not null) ? masks : [],
            MaterialOcclusion = occlusion,
            MaterialToon = materials.Toons.Any(toon => toon is not null) ? materials.Toons.ToArray() : [],
            Textures = textures.ToArray(),
            Skeleton = read.Skeleton,
            Clips = read.Clips.ToArray(),
        };
    }

    /// <summary>
    /// Reports what's wrong with the model, and says whether it's fine. A cutoff that can't cut renders fine but wrong,
    /// and the renderer refuses a subsurface it can't store, so both stop the cook. Materials no part uses are never
    /// drawn, so they can't be wrong.
    /// </summary>
    private static bool Check(ModelCook cook, ModelData model, List<string> materialNames, IReadOnlyList<string> eventProblems)
    {
        var problems = model.Parts.Select(part => part.Material).Distinct().Order()
            .Select(i => (Name: materialNames[i], Problem: model.CutoutProblem(i) ?? SubsurfaceProblem(model.Materials[i]) ?? FoliageProblem(model.Materials[i])))
            .Where(entry => entry.Problem is not null)
            .Select(entry => $"material {entry.Name}: {entry.Problem}").Concat(eventProblems).ToList();
        foreach (string problem in problems)
        {
            cook.Error(problem);
        }

        return problems.Count == 0;
    }

    /// <summary>
    /// The model's bytes, with its textures compressed when the settings ask. That comes last, since the checks read the
    /// textures' pixels.
    /// </summary>
    private static byte[] Write(ModelCook cook, ModelData model, ModelMaterialMasks?[] masks, int[] occlusion)
    {
        if (cook.Settings.CompressTextures)
        {
            // It times its own phases: taking textures from the step cache, compressing the rest, and keeping them.
            var extra = masks.OfType<ModelMaterialMasks>().Select(mask => (mask.Texture, TextureFormat.Bc7))
                .Concat(occlusion.Where(texture => texture >= 0).Select(texture => (texture, TextureFormat.Bc4)));
            TextureCompression.Compress(model.Textures, model.Materials, extra, cook.Input, cook.Warning);
        }

        using (cook.Input.Time("write model"))
        {
            // Textures go out as objects of their own, so models that share an image share one copy of it.
            return model.ToBytes(texture => ModelTextures.Share(texture, cook.Output));
        }
    }

    private static string? SubsurfaceProblem(in ModelMaterial material) =>
        material.Subsurface.Problem(material.Clearcoat, material.Flags);

    private static string? FoliageProblem(in ModelMaterial material) =>
        ((material.Flags & MaterialFlags.Foliage) != 0 ? Rendition.Material.TranslucencyProblem(material.Translucency) : null)
        ?? Rendition.Material.WindProblem(material.Wind);
}

/// <summary>Every cooker that ships with Super, the model cooker included.</summary>
public static class ModelCookers
{
    public static IReadOnlyList<ICooker> All { get; } = [.. BuiltInCookers.All, new MaterialCooker(), new ModelCooker()];

    /// <summary>
    /// A cooked model with every texture inside it, so it can be read on its own, away from the store its
    /// shared textures are in. <paramref name="name"/> is only for messages.
    /// </summary>
    public static byte[] StandAlone(ICookedStore store, string hash, string name)
    {
        using var stream = new MemoryStream(store.Get(hash), writable: false);
        var model = ModelData.Read(stream, name, texture =>
        {
            using var bytes = new MemoryStream(store.Get(texture), writable: false);
            return TextureData.Read(bytes, $"{name}: texture {texture}");
        });
        return model.ToBytes();
    }
}
