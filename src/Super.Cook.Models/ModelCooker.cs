using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Rendition;
using Super.Cook.Materials;
using Silk.NET.Assimp;
using Assimp = Silk.NET.Assimp.Assimp;
using Skeleton = Rendition.Skeleton;

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
    // How many times detail textures repeat for each repeat of the others, unless a rule or a detail graph says.
    private const float DefaultDetailScale = 8f;

    private static readonly JsonSerializerOptions SettingsOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly Lazy<Assimp> Api = new(Assimp.GetApi);

    public string Id => "model";

    // 2: "has no UVs" warns only when the material uses a texture. 3: subsurface. 4: height and detail textures.
    // 5: materials gained Ior. 6: parts keep the name of their mesh's node, keepMeshes and keepOrigin, and unskinned
    // meshes follow their container node. 7: textures are objects of their own that models point to by hash.
    // 8: material graphs. 9: graph materials have no height or detail textures unless their rule names them, and an
    // ior of 1. 10: Kaiser mips, roughness widened by the normal map, graph heights as parallax, detail graphs, and
    // block-compressed textures. 11: material shaders, and a graph's opacity in the colour's alpha under a cutoff. The
    // graphs' own version is folded in, so a change to Super.Materials cooks every model again, since one may use a
    // graph.
    public int Version { get; } = unchecked(MaterialGraphs.Version * 31 + 11);

    public IReadOnlyList<string> Extensions { get; } = [".glb", ".gltf", ".fbx", ".obj"];

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
            JsonNode? gltf;
            try
            {
                gltf = JsonNode.Parse(input.Bytes);
            }
            catch (JsonException)
            {
                return; // Assimp says what's wrong with it.
            }

            if (gltf?["buffers"] is JsonArray buffers)
            {
                foreach (var buffer in buffers)
                {
                    if (buffer?["uri"] is JsonValue value && value.TryGetValue(out string? uri)
                        && !uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    {
                        input.Track(Uri.UnescapeDataString(uri));
                    }
                }
            }
        }
        else if (extension == ".obj")
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
    private static unsafe byte[]? CookOne(CookInput input, CookOutput output, ImportSettings settings, string prefix, GraphRuns graphs)
    {
        string modelPath = input.FullPath!;
        string sourceDirectory = Path.GetDirectoryName(modelPath)!;
        bool isGltf = Path.GetExtension(modelPath).ToLowerInvariant() is ".glb" or ".gltf";
        foreach (var source in settings.Animations)
        {
            input.Track(source.File);
        }

        var textures = new ModelTextures(input, output, prefix);
        var vertices = new List<Vertex>();
        var indices = new List<uint>();
        var parts = new List<ModelPart>();
        var meshNames = new List<string>();
        var materials = new List<ModelMaterial>();
        var materialNames = new List<string>();
        var shaders = new List<ModelMaterialShader?>();

        // The width in metres of each material's graph, or 0 for a material without one.
        var graphWidths = new List<float>();

        // Each mesh's material and the run of vertices it added.
        var meshVertices = new List<(int Material, int First, int Count)>();

        Skeleton? skeleton = null;
        var clips = new List<AnimationClip>();
        Vertex[] vertexArray;

        var assimp = Api.Value;
        const PostProcessSteps steps = PostProcessSteps.Triangulate
            | PostProcessSteps.JoinIdenticalVertices
            | PostProcessSteps.GenerateSmoothNormals
            | PostProcessSteps.CalculateTangentSpace
            | PostProcessSteps.FlipUVs;

        Scene* scene;
        using (input.Time("assimp import"))
        {
            scene = Import(assimp, modelPath, steps);
        }

        try
        {
            if (scene == null || scene->MRootNode == null)
            {
                output.Error($"{prefix}Could not read {input.Name}: {assimp.GetErrorStringS()}");
                return null;
            }

            // A rigged model keeps its node tree, since the joints live there. A static one is
            // flattened into a single mesh space, which merges the meshes that share a material. With
            // keepMeshes it keeps its node tree instead, and each mesh is placed by its node, so every
            // mesh stays its own part with its name.
            RigImporter? rig = null;
            if (RigImporter.HasBones(scene))
            {
                rig = RigImporter.Read(scene, message => output.Warning(prefix + message));
            }
            else
            {
                if (settings.Node is { } keep)
                {
                    int kept = KeepNodes(scene->MRootNode, keep, false);
                    if (kept == 0)
                    {
                        output.Error($"{prefix}No node with meshes has \"{keep}\" in its name.");
                        return null;
                    }
                }

                if (!settings.KeepMeshes)
                {
                    // On failure Assimp has already freed the scene.
                    using (input.Time("assimp flatten"))
                    {
                        scene = assimp.ApplyPostProcessing(scene, (uint)PostProcessSteps.PreTransformVertices);
                    }

                    if (scene == null)
                    {
                        output.Error($"{prefix}Could not flatten {input.Name}: {assimp.GetErrorStringS()}");
                        return null;
                    }
                }
            }

            // Only a placed mesh keeps its name. A merged part has no single mesh to name it after.
            bool named = rig is not null || settings.KeepMeshes;

            // glTF materials refer to images stored inside the file as "*0", "*1" and so on, while
            // FBX materials use the image's original file name, so each is stored under both.
            for (uint i = 0; i < scene->MNumTextures; i++)
            {
                var embedded = scene->MTextures[i];
                if (embedded->MHeight == 0) // A height of 0 means the width is the size of an encoded image.
                {
                    byte[] encoded = new ReadOnlySpan<byte>(embedded->PcData, (int)embedded->MWidth).ToArray();
                    textures.AddEmbedded($"*{i}", encoded);
                    string original = embedded->MFilename.AsString;
                    if (original.Length > 0)
                    {
                        textures.AddEmbedded(Path.GetFileName(original.Replace('\\', '/')), encoded);
                    }
                }
            }

            for (uint i = 0; i < scene->MNumMaterials; i++)
            {
                var context = new MaterialContext(settings, textures, graphs, output, prefix, isGltf);
                var material = ImportMaterial(assimp, scene->MMaterials[i], context, out string materialName, out float graphWidth);
                textures.WidenRoughness(material.SurfaceTexture, material.NormalTexture);
                materials.Add(material);
                materialNames.Add(materialName);
                graphWidths.Add(graphWidth);
                shaders.Add(RuleFor(settings, materialName) is { } rule ? ImportShader(rule, materialName, input, output, prefix) : null);
            }

            foreach (var (m, world, node) in Placements(scene, rig is null && settings.KeepMeshes))
            {
                var mesh = scene->MMeshes[m];
                meshVertices.Add(((int)mesh->MMaterialIndex, vertices.Count, (int)mesh->MNumVertices));
                // Without UVs every texel read lands on one corner. A material with no textures doesn't care.
                if (mesh->MTextureCoords[0] == null && SamplesTexture(materials[(int)mesh->MMaterialIndex]))
                {
                    output.Warning($"{prefix}mesh {m} ({mesh->MName.AsString}) has no UVs; using a flat tangent frame");
                }

                // A mirroring placement turns the tangent frame over, so its sign flips.
                var normals = NormalMatrix(world);
                bool mirrored = world.GetDeterminant() < 0f;
                uint baseVertex = (uint)vertices.Count;
                float graphWidth = graphWidths[(int)mesh->MMaterialIndex];
                for (uint v = 0; v < mesh->MNumVertices; v++)
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

                    var direction = Vector3.TransformNormal(new Vector3(tangent.X, tangent.Y, tangent.Z), world);
                    vertices.Add(new Vertex(
                        Vector3.Transform(mesh->MVertices[v], world),
                        Vector3.TransformNormal(normal, normals),
                        new Vector4(direction, mirrored ? -tangent.W : tangent.W),
                        new Vector2(uv.X, uv.Y)));
                }

                rig?.Skin(mesh, (int)m, CollectionsMarshal.AsSpan(vertices).Slice((int)baseVertex, (int)mesh->MNumVertices));

                uint firstIndex = (uint)indices.Count;
                for (uint f = 0; f < mesh->MNumFaces; f++)
                {
                    var face = mesh->MFaces[f];
                    if (face.MNumIndices != 3)
                    {
                        continue; // Stray points and lines survive triangulation.
                    }

                    indices.Add(baseVertex + face.MIndices[0]);
                    indices.Add(baseVertex + face.MIndices[1]);
                    indices.Add(baseVertex + face.MIndices[2]);
                }

                parts.Add(new ModelPart
                {
                    FirstIndex = firstIndex,
                    IndexCount = (uint)indices.Count - firstIndex,
                    Material = (int)mesh->MMaterialIndex,
                });
                if (named)
                {
                    meshNames.Add(MeshName(rig?.NodeOfMeshName((int)m) ?? node, mesh));
                }
            }

            if (rig is not null)
            {
                for (uint a = 0; a < scene->MNumAnimations; a++)
                {
                    var animation = scene->MAnimations[a];
                    string name = animation->MName.AsString is { Length: > 0 } own ? own : $"clip{a}";
                    AddClip(clips, rig.ReadClip(animation, name));
                }

                foreach (var source in settings.Animations)
                {
                    string path = Path.Combine(sourceDirectory, source.File);
                    var extra = Import(assimp, path, steps);
                    if (extra == null)
                    {
                        output.Error($"{prefix}Could not read {source.File}: {assimp.GetErrorStringS()}");
                        return null;
                    }

                    try
                    {
                        string baseName = source.Name ?? Path.GetFileNameWithoutExtension(source.File);
                        for (uint a = 0; a < extra->MNumAnimations; a++)
                        {
                            string name = extra->MNumAnimations == 1 ? baseName : $"{baseName}{a + 1}";
                            AddClip(clips, rig.ReadClip(extra->MAnimations[a], name));
                        }
                    }
                    finally
                    {
                        assimp.FreeScene(extra);
                    }
                }
            }

            // The turn, scale and centring go into the vertices, and for a rig into its skeleton
            // too, so skinning lands in the same space.
            vertexArray = vertices.ToArray();
            var normalization = Normalization(vertexArray, settings.RotateY, settings.TargetLength, settings.KeepOrigin);
            Apply(vertexArray, normalization);
            skeleton = rig?.ToSkeleton(normalization);
        }
        finally
        {
            if (scene != null)
            {
                assimp.FreeScene(scene);
            }
        }

        for (int i = 0; i < materials.Count; i++)
        {
            if ((materials[i].Flags & MaterialFlags.Foliage) != 0)
            {
                PointOutOfCrown(vertexArray, meshVertices.Where(mesh => mesh.Material == i));
            }
        }

        var model = new ModelData
        {
            Vertices = vertexArray,
            Indices = indices.ToArray(),
            Parts = parts.ToArray(),
            MeshNames = meshNames.ToArray(),
            Materials = materials.ToArray(),
            MaterialShaders = shaders.Any(shader => shader is not null) ? shaders.ToArray() : [],
            Textures = textures.ToArray(),
            Skeleton = skeleton,
            Clips = clips.ToArray(),
        };

        // A cutoff that can't cut renders fine but wrong, and the renderer refuses a
        // subsurface it can't store, so both stop the cook. Materials no part uses are
        // never drawn, so they can't be wrong.
        var problems = parts.Select(part => part.Material).Distinct().Order()
            .Select(i => (Name: materialNames[i], Problem: model.CutoutProblem(i) ?? SubsurfaceProblem(model.Materials[i])))
            .Where(entry => entry.Problem is not null)
            .ToList();
        foreach (var (materialName, problem) in problems)
        {
            output.Error($"{prefix}material {materialName}: {problem}");
        }

        if (problems.Count > 0)
        {
            return null;
        }

        // Last, since the checks above read the textures' pixels.
        if (settings.CompressTextures)
        {
            using (input.Time("compress textures"))
            {
                TextureCompression.Compress(model.Textures, model.Materials);
            }
        }

        using (input.Time("write model"))
        {
            // Textures go out as objects of their own, so models that share an image share one copy of it.
            return model.ToBytes(texture => ModelTextures.Share(texture, output));
        }
    }

    private static string? SubsurfaceProblem(in ModelMaterial material) =>
        material.Subsurface.Problem(material.Clearcoat, material.Flags);

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

    private static unsafe Scene* Import(Assimp assimp, string path, PostProcessSteps steps)
    {
        // Mixamo FBX files otherwise sprout pivot nodes, which would break joint names
        // between a character and its animation files.
        var properties = assimp.CreatePropertyStore();
        assimp.SetImportPropertyInteger(properties, "IMPORT_FBX_PRESERVE_PIVOTS", 0);
        var scene = assimp.ImportFileExWithProperties(path, (uint)steps, null, properties);
        assimp.ReleasePropertyStore(properties);
        return scene;
    }

    private static bool SamplesTexture(ModelMaterial material)
    {
        return material.BaseColorTexture >= 0 || material.NormalTexture >= 0
            || material.SurfaceTexture >= 0 || material.EmissiveTexture >= 0
            || material.HeightTexture >= 0 || material.DetailColorTexture >= 0 || material.DetailNormalTexture >= 0;
    }

    private static void AddClip(List<AnimationClip> clips, AnimationClip? clip)
    {
        if (clip is null)
        {
            return;
        }

        if (clips.Any(existing => existing.Name == clip.Name))
        {
            throw new InvalidDataException($"Two clips are called {clip.Name}. Give one a name in the settings file.");
        }

        clips.Add(clip);
    }

    // The meshes to cook, each with the transform that puts it in the model's space. With byNode, they
    // come in the order the nodes hold them, and each is placed by its node. Otherwise every mesh is
    // already in the model's space: a rig's meshes stay in their own space, since the rig importer
    // places them and the joints move them, and a merged static scene was flattened by Assimp.
    // Node is the name of the node holding the mesh when placed by node, and null otherwise.
    private static unsafe List<(uint Mesh, Matrix4x4 World, string? Node)> Placements(Scene* scene, bool byNode)
    {
        var placed = new List<(uint Mesh, Matrix4x4 World, string? Node)>();
        if (byNode)
        {
            Place(scene->MRootNode, Matrix4x4.Identity, placed);
        }
        else
        {
            for (uint m = 0; m < scene->MNumMeshes; m++)
            {
                placed.Add((m, Matrix4x4.Identity, null));
            }
        }

        return placed;
    }

    private static unsafe void Place(Node* node, in Matrix4x4 parent, List<(uint Mesh, Matrix4x4 World, string? Node)> placed)
    {
        // Assimp stores matrices for column vectors; this engine multiplies row vectors.
        var world = Matrix4x4.Transpose(node->MTransformation) * parent;
        string name = node->MName.AsString;
        for (uint i = 0; i < node->MNumMeshes; i++)
        {
            placed.Add((node->MMeshes[i], world, name));
        }

        for (uint c = 0; c < node->MNumChildren; c++)
        {
            Place(node->MChildren[c], world, placed);
        }
    }

    // A part is named by the node that holds its mesh: that's the name an artist sees in the outliner,
    // and it stays whole where Assimp splits a mesh with several materials into several meshes or
    // suffixes their names. A node with no name falls back to the mesh's own name.
    private static unsafe string MeshName(string? node, Mesh* mesh) =>
        string.IsNullOrEmpty(node) ? mesh->MName.AsString : node;

    // Normals keep pointing off the surface when a placement stretches it, so they use the inverse transpose.
    private static Matrix4x4 NormalMatrix(in Matrix4x4 world) =>
        Matrix4x4.Invert(world, out var inverse) ? Matrix4x4.Transpose(inverse) : Matrix4x4.Identity;

    // Drops the meshes of every node outside the ones named, so flattening leaves them out.
    // Returns how many nodes with meshes were kept.
    private static unsafe int KeepNodes(Node* node, string keep, bool inside)
    {
        inside |= node->MName.AsString.Contains(keep, StringComparison.OrdinalIgnoreCase);
        int kept = 0;
        if (!inside)
        {
            node->MNumMeshes = 0;
        }
        else if (node->MNumMeshes > 0)
        {
            kept++;
        }

        for (uint i = 0; i < node->MNumChildren; i++)
        {
            kept += KeepNodes(node->MChildren[i], keep, inside);
        }

        return kept;
    }

    // The transform that turns the model by rotateY degrees around the vertical axis, scales
    // it to real-world metres, centres it, and stands it on y = 0. The bounds are taken after
    // the turn, so the model fits its target length the way it will face. With keepOrigin the
    // turn and scale are about the file's own origin, and nothing is moved.
    private static Matrix4x4 Normalization(Vertex[] vertices, float rotateY, float targetLength, bool keepOrigin)
    {
        var turn = Matrix4x4.CreateRotationY(rotateY * MathF.PI / 180f);
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (ref readonly var vertex in vertices.AsSpan())
        {
            var turned = Vector3.Transform(vertex.Position, turn);
            min = Vector3.Min(min, turned);
            max = Vector3.Max(max, turned);
        }

        var size = max - min;
        float scale = targetLength > 0f ? targetLength / MathF.Max(size.X, MathF.Max(size.Y, size.Z)) : 1f;
        if (keepOrigin)
        {
            return turn * Matrix4x4.CreateScale(scale);
        }

        var center = (min + max) * 0.5f;
        var shift = new Vector3(-center.X, -center.Y + size.Y * 0.5f, -center.Z);
        return turn * Matrix4x4.CreateTranslation(shift) * Matrix4x4.CreateScale(scale);
    }

    private static void Apply(Vertex[] vertices, in Matrix4x4 transform)
    {
        foreach (ref var vertex in vertices.AsSpan())
        {
            vertex.Position = Vector3.Transform(vertex.Position, transform);
            vertex.Normal = Vector3.Normalize(Vector3.TransformNormal(vertex.Normal, transform));
            var tangent = Vector3.TransformNormal(new Vector3(vertex.Tangent.X, vertex.Tangent.Y, vertex.Tangent.Z), transform);
            vertex.Tangent = new Vector4(Vector3.Normalize(tangent), vertex.Tangent.W);
        }
    }

    // A graph's UVs are in metres, so they're divided by its width. FlipUVs has turned v upside down, so v is turned back
    // first and again after. That keeps v running the way it did, and lines a tile's edge up with the file's own UV origin,
    // so a brick wall laid out from its foot in Blender starts a row of bricks at its foot.
    private static Vector3 GraphUv(Vector3 uv, float width)
    {
        return new Vector3(uv.X / width, 1f - (1f - uv.Y) / width, uv.Z);
    }

    private sealed record MaterialContext(
        ImportSettings Settings, ModelTextures Textures, GraphRuns Graphs, CookOutput Output, string Prefix, bool IsGltf);

    /// <summary>One graph's images, packed into the pixels of its textures.</summary>
    private sealed record GraphRun(string Key, int Size, float Width, float HeightDepth, MaterialPixels Pixels);

    /// <summary>The graphs a model's materials name, each run once per (path, size) however many materials or parts use it.</summary>
    private sealed class GraphRuns(CookInput input)
    {
        private readonly Dictionary<string, GraphRun?> _runs = new(StringComparer.Ordinal);

        /// <returns>The run, or null after reporting why there is none to <paramref name="error"/>.</returns>
        public GraphRun? Run(string path, int size, Action<string> error, Action<string> warning)
        {
            path = path.Replace('\\', '/');
            string key = $"{path}@{size}";
            if (_runs.TryGetValue(key, out var known))
            {
                return known;
            }

            GraphRun? run = null;
            if (MaterialGraphs.Run(input, path, size, error) is { } images)
            {
                var pixels = MaterialPixels.Pack(images);
                foreach (string part in pixels.NotNumbers)
                {
                    warning($"{path}: {part} has pixels that aren't numbers. They were written as 0.");
                }

                run = new GraphRun(key, images.Size, images.Width, images.HeightDepth, pixels);
            }

            _runs[key] = run;
            return run;
        }
    }

    /// <returns>The detail graph's run, or null when the rule has none or it failed, which was reported.</returns>
    private static GraphRun? RunDetailGraph(MaterialRule rule, string materialName, MaterialContext context)
    {
        if (rule.DetailGraph is not { } path)
        {
            return null;
        }

        void Error(string message) => context.Output.Error($"{context.Prefix}material {materialName}: {message}");

        if (rule.Graph is null)
        {
            Error("detailGraph tiles at a size the main graph's width sets, so it needs a graph.");
            return null;
        }

        if (rule.DetailColor is not null || rule.DetailNormal is not null)
        {
            Error("detailGraph makes the detail textures, so it can't be used with detailColor or detailNormal.");
            return null;
        }

        int size = rule.DetailGraphSize ?? MaterialGraphs.DefaultSize;
        if (MaterialGraphs.SizeProblem("detailGraphSize", size) is { } problem)
        {
            Error(problem);
            return null;
        }

        return context.Graphs.Run(path, size, Error,
            message => context.Output.Warning($"{context.Prefix}material {materialName}: {message}"));
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

    /// <returns>
    /// The surface shader a rule names, with its textures as their names in the cooked content, or null when it names
    /// none or something about it is wrong, which was reported.
    /// </returns>
    private static ModelMaterialShader? ImportShader(MaterialRule rule, string materialName, CookInput input, CookOutput output, string prefix)
    {
        void Error(string message) => output.Error($"{prefix}material {materialName}: {message}");

        float[][] parameters = rule.ShaderParameters ?? [];
        string[] paths = rule.ShaderTextures ?? [];
        if (rule.Shader is not { } name)
        {
            if (rule.ShaderParameters is not null || rule.ShaderTextures is not null)
            {
                Error("shaderParameters and shaderTextures are read by a shader, so they need one.");
            }

            return null;
        }

        bool wrong = false;
        if (rule.Transparent)
        {
            Error("transparent materials can't use a surface shader yet.");
            wrong = true;
        }

        if (parameters.Length > ShaderParameters.Length)
        {
            Error($"shaderParameters has {parameters.Length} entries, but a surface shader takes at most {ShaderParameters.Length}.");
            wrong = true;
        }

        var vectors = new Vector4[Math.Min(parameters.Length, ShaderParameters.Length)];
        for (int i = 0; i < vectors.Length; i++)
        {
            float[] numbers = parameters[i] ?? [];
            if (numbers.Length > 4)
            {
                Error($"shaderParameters[{i}] has {numbers.Length} numbers, but a parameter holds at most 4.");
                wrong = true;
                continue;
            }

            float At(int component) => component < numbers.Length ? numbers[component] : 0f;
            vectors[i] = new Vector4(At(0), At(1), At(2), At(3));
        }

        if (paths.Length > ShaderTextures.Length)
        {
            Error($"shaderTextures has {paths.Length} entries, but a surface shader takes at most {ShaderTextures.Length}.");
            wrong = true;
        }

        var names = new string[Math.Min(paths.Length, ShaderTextures.Length)];
        for (int i = 0; i < names.Length; i++)
        {
            if (paths[i] is { } path && input.NameOf(path) is { } found)
            {
                names[i] = found;
            }
            else
            {
                Error($"shaderTextures[{i}] {paths[i]} isn't in the source root.");
                wrong = true;
            }
        }

        return wrong ? null : new ModelMaterialShader(name, vectors, names);
    }

    /// <summary>The first rule whose <c>match</c> is in the material's name, or null when there is none.</summary>
    private static MaterialRule? RuleFor(ImportSettings settings, string materialName) =>
        settings.Materials.FirstOrDefault(rule => materialName.Contains(rule.Match, StringComparison.OrdinalIgnoreCase));

    /// <returns>The graph's run, or null when the rule has no graph or it failed, which was reported.</returns>
    private static GraphRun? RunGraph(MaterialRule rule, string materialName, MaterialContext context)
    {
        if (rule.Graph is not { } path)
        {
            return null;
        }

        void Error(string message) => context.Output.Error($"{context.Prefix}material {materialName}: {message}");

        if (rule.BaseColor is not null || rule.Normal is not null || rule.Surface is not null || rule.Alpha is not null)
        {
            Error("graph makes the colour, normal and surface textures, so it can't be used with baseColor, normal, surface or alpha.");
            return null;
        }

        int size = rule.GraphSize ?? MaterialGraphs.DefaultSize;
        if (MaterialGraphs.SizeProblem("graphSize", size) is { } problem)
        {
            Error(problem);
            return null;
        }

        return context.Graphs.Run(path, size, Error,
            message => context.Output.Warning($"{context.Prefix}material {materialName}: {message}"));
    }

    private static unsafe ModelMaterial ImportMaterial(
        Assimp assimp, Silk.NET.Assimp.Material* source, MaterialContext context, out string materialName, out float graphWidth)
    {
        var (settings, textures, isGltf) = (context.Settings, context.Textures, context.IsGltf);
        AssimpString name;
        assimp.GetMaterialString(source, Assimp.MaterialNameBase, 0, 0, &name);
        materialName = name.AsString;

        string? linkedColor = GetTexture(assimp, source, TextureType.Diffuse) ?? GetTexture(assimp, source, TextureType.BaseColor);
        string? linkedNormal = GetTexture(assimp, source, TextureType.Normals) ?? GetTexture(assimp, source, TextureType.Height);

        // Only glTF defines these values and the combined metal and roughness texture.
        string? linkedSurface = isGltf
            ? GetTexture(assimp, source, TextureType.Unknown) ?? GetTexture(assimp, source, TextureType.Metalness)
            : null;
        float[]? fileColor = isGltf ? GetFloats(assimp, source, "$clr.base", 4) : null;
        float[]? fileMetallic = isGltf ? GetFloats(assimp, source, "$mat.metallicFactor", 1) : null;
        float[]? fileRoughness = isGltf ? GetFloats(assimp, source, "$mat.roughnessFactor", 1) : null;
        float? fileCutoff = isGltf ? GetGltfCutoff(assimp, source) : null;

        var rule = RuleFor(settings, materialName);

        // A texture the rule names must be there; one only the model file links to may not be.
        string? named = rule is null ? null : materialName;
        rule ??= new MaterialRule();
        var flags = (rule.TwoSided || rule.Foliage ? MaterialFlags.TwoSided : 0)
            | (rule.Transparent ? MaterialFlags.Transparent : 0)
            | (rule.Foliage ? MaterialFlags.Foliage : 0)
            | (rule.DetailMask ? MaterialFlags.DetailMask : 0)
            | (rule.WorldVariation ? MaterialFlags.WorldVariation : 0)
            | (rule.Dithered ? MaterialFlags.Dithered : 0);
        var emissive = rule.EmissiveColor is { Length: 3 } e
            ? new Vector3(e[0], e[1], e[2])
            : rule.Emissive != null ? Vector3.One : Vector3.Zero;

        var graph = RunGraph(rule, materialName, context);
        graphWidth = graph?.Width ?? 0f;
        if (graph is not null)
        {
            // The graph's textures hold the colour, metalness and roughness, which these values multiply, so they're 1
            // unless the rule says otherwise. The model file's own values were for its own textures.
            int size = graph.Size;

            // A cutoff reads the graph's opacity from the colour's alpha, so it's moved there from the surface's, which is
            // left whole so the opacity isn't counted twice.
            int colorTexture, surfaceTexture;
            if (rule.AlphaCutoff is not null)
            {
                var (color, surface) = OpacityInColor(graph.Pixels);
                colorTexture = textures.AddPixels(graph.Key + "#color-opacity", size, size, TextureKind.Color, color);
                surfaceTexture = textures.AddPixels(graph.Key + "#surface-opaque", size, size, TextureKind.Linear, surface);
            }
            else
            {
                colorTexture = textures.AddPixels(graph.Key + "#color", size, size, TextureKind.Color, graph.Pixels.Color);
                surfaceTexture = textures.AddPixels(graph.Key + "#surface", size, size, TextureKind.Linear, graph.Pixels.Surface);
            }

            // The graph's own height map sinks its grooves in with parallax, unless the rule gives a height map.
            float heightDepth = rule.HeightDepth ?? (rule.Height is null ? graph.HeightDepth : 0f);
            int heightTexture = rule.Height is not null
                ? textures.Add(rule.Height, TextureKind.Linear, namedBy: named)
                : heightDepth > 0f
                    ? textures.AddPixels(graph.Key + "#height", size, size, TextureKind.Linear, graph.Pixels.Height)
                    : -1;

            // A detail graph tiles at its own width, which sets how many times it repeats across the main graph.
            var detail = RunDetailGraph(rule, materialName, context);
            int detailColor = detail is null
                ? textures.Add(rule.DetailColor, TextureKind.Linear, namedBy: named)
                : textures.AddPixels(detail.Key + "#detail", detail.Size, detail.Size, TextureKind.Linear,
                    DetailColorPixels(detail.Pixels.Color));
            int detailNormal = detail is null
                ? textures.Add(rule.DetailNormal, TextureKind.Linear, isNormalMap: true, namedBy: named)
                : textures.AddPixels(detail.Key + "#normal", detail.Size, detail.Size, TextureKind.Linear,
                    detail.Pixels.Normal, isNormalMap: true);
            float detailScale = rule.DetailScale ?? (detail is null ? DefaultDetailScale : graph.Width / detail.Width);

            return new ModelMaterial
            {
                BaseColor = rule.Color is { Length: 3 } gc ? new Vector4(gc[0], gc[1], gc[2], rule.Opacity) : new Vector4(1f, 1f, 1f, rule.Opacity),
                Metallic = rule.Metallic ?? 1f,
                Roughness = rule.Roughness ?? 1f,
                AlphaCutoff = rule.AlphaCutoff ?? 0f,
                BaseColorTexture = colorTexture,
                NormalTexture = rule.NoNormalMap
                    ? -1
                    : textures.AddPixels(graph.Key + "#normal", size, size, TextureKind.Linear, graph.Pixels.Normal, isNormalMap: true),
                SurfaceTexture = surfaceTexture,
                Emissive = emissive,
                EmissiveTexture = textures.Add(rule.Emissive, TextureKind.Color, namedBy: named),
                Clearcoat = rule.Clearcoat,
                HeightTexture = heightTexture,
                HeightDepth = heightDepth,
                DetailColorTexture = detailColor,
                DetailNormalTexture = detailNormal,
                DetailScale = detailScale,
                Flags = flags,
                Ior = rule.Ior,
                Subsurface = Subsurface(rule),
            };
        }

        if (rule.Graph is null)
        {
            // Reports that a detail graph needs a main graph.
            RunDetailGraph(rule, materialName, context);
        }

        return new ModelMaterial
        {
            BaseColor = rule.Color is { Length: 3 } c
                ? new Vector4(c[0], c[1], c[2], rule.Opacity)
                : fileColor is { } f ? new Vector4(f[0], f[1], f[2], f[3]) : new Vector4(1f, 1f, 1f, rule.Opacity),
            Metallic = rule.Metallic ?? fileMetallic?[0] ?? 0f,
            Roughness = rule.Roughness ?? fileRoughness?[0] ?? 0.6f,
            AlphaCutoff = rule.AlphaCutoff ?? fileCutoff ?? 0f,
            BaseColorTexture = textures.Add(rule.BaseColor ?? linkedColor, TextureKind.Color, alphaFile: rule.Alpha,
                namedBy: rule.BaseColor is null ? null : named),
            NormalTexture = textures.Add(rule.NoNormalMap ? null : rule.Normal ?? linkedNormal, TextureKind.Linear, isNormalMap: true,
                namedBy: rule.Normal is null ? null : named),
            SurfaceTexture = rule.Surface is not null
                ? textures.Add(rule.Surface, TextureKind.Linear, namedBy: named)
                : textures.Add(linkedSurface, TextureKind.Linear, isGltfSurface: true),
            Emissive = emissive,
            EmissiveTexture = textures.Add(rule.Emissive, TextureKind.Color, namedBy: named),
            Clearcoat = rule.Clearcoat,
            HeightTexture = textures.Add(rule.Height, TextureKind.Linear, namedBy: named),
            HeightDepth = rule.HeightDepth ?? 0f,
            DetailColorTexture = textures.Add(rule.DetailColor, TextureKind.Linear, namedBy: named),
            DetailNormalTexture = textures.Add(rule.DetailNormal, TextureKind.Linear, isNormalMap: true, namedBy: named),
            DetailScale = rule.DetailScale ?? DefaultDetailScale,
            Flags = flags,
            Ior = rule.Ior,
            Subsurface = Subsurface(rule),
        };
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

    // glTF's MASK mode cuts out by alpha, at 0.5 unless the file says otherwise.
    private static unsafe float? GetGltfCutoff(Assimp assimp, Silk.NET.Assimp.Material* material)
    {
        AssimpString mode;
        if (assimp.GetMaterialString(material, "$mat.gltf.alphaMode", 0, 0, &mode) != Return.Success
            || mode.AsString != "MASK")
        {
            return null;
        }

        return GetFloats(assimp, material, "$mat.gltf.alphaCutoff", 1)?[0] ?? 0.5f;
    }

    private static unsafe float[]? GetFloats(Assimp assimp, Silk.NET.Assimp.Material* material, string key, uint count)
    {
        var values = new float[count];
        uint read = count;
        fixed (float* pointer = values)
        {
            return assimp.GetMaterialFloatArray(material, key, 0, 0, pointer, &read) == Return.Success && read == count
                ? values
                : null;
        }
    }

    private static unsafe string? GetTexture(Assimp assimp, Silk.NET.Assimp.Material* material, TextureType type)
    {
        AssimpString path;
        var result = assimp.GetMaterialTexture(material, type, 0, &path, null, null, null, null, null, null);
        return result == Return.Success ? Path.GetFileName(path.AsString.Replace('\\', '/')) : null;
    }
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
