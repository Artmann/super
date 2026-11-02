using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Rendition;
using Rendition.Import;
using Silk.NET.Assimp;
using Assimp = Silk.NET.Assimp.Assimp;
using File = System.IO.File;
using Skeleton = Rendition.Skeleton;

// Usage: Rendition.Import <model file> <materials.json> <output.rmodel>
// Textures are looked up by file name next to the model file, because model files
// usually hold absolute paths from the artist's machine.
if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: Rendition.Import <model file> <materials.json> <output.rmodel>");
    return 1;
}

string modelPath = Path.GetFullPath(args[0]);
string sourceDirectory = Path.GetDirectoryName(modelPath)!;
bool isGltf = Path.GetExtension(modelPath).ToLowerInvariant() is ".glb" or ".gltf";
var settings = JsonSerializer.Deserialize<ImportSettings>(
    File.ReadAllText(args[1]), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

var textures = new TextureCooker(sourceDirectory);
var vertices = new List<Vertex>();
var indices = new List<uint>();
var parts = new List<ModelPart>();
var materials = new List<ModelMaterial>();
var materialNames = new List<string>();

// Each mesh's material and the run of vertices it added.
var meshVertices = new List<(int Material, int First, int Count)>();

Skeleton? skeleton = null;
var clips = new List<AnimationClip>();
Vertex[] vertexArray;

unsafe
{
    var assimp = Assimp.GetApi();
    const PostProcessSteps steps = PostProcessSteps.Triangulate
        | PostProcessSteps.JoinIdenticalVertices
        | PostProcessSteps.GenerateSmoothNormals
        | PostProcessSteps.CalculateTangentSpace
        | PostProcessSteps.FlipUVs;

    var scene = Import(assimp, modelPath, steps);
    if (scene == null || scene->MRootNode == null)
    {
        Console.Error.WriteLine($"Could not read {modelPath}: {assimp.GetErrorStringS()}");
        return 1;
    }

    // A rigged model keeps its node tree, since the joints live there. A static one is
    // flattened into a single mesh space.
    RigImporter? rig = null;
    if (RigImporter.HasBones(scene))
    {
        rig = RigImporter.Read(scene);
        Console.WriteLine($"Rig: {rig.JointCount} joints");
    }
    else
    {
        if (settings.Node is { } keep)
        {
            int kept = KeepNodes(scene->MRootNode, keep, false);
            if (kept == 0)
            {
                Console.Error.WriteLine($"No node with meshes has \"{keep}\" in its name.");
                return 1;
            }
        }

        scene = assimp.ApplyPostProcessing(scene, (uint)PostProcessSteps.PreTransformVertices);
        if (scene == null)
        {
            Console.Error.WriteLine($"Could not flatten {modelPath}: {assimp.GetErrorStringS()}");
            return 1;
        }
    }

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
        materials.Add(ImportMaterial(assimp, scene->MMaterials[i], settings, textures, isGltf, out string materialName));
        materialNames.Add(materialName);
    }

    for (uint m = 0; m < scene->MNumMeshes; m++)
    {
        var mesh = scene->MMeshes[m];
        meshVertices.Add(((int)mesh->MMaterialIndex, vertices.Count, (int)mesh->MNumVertices));
        if (mesh->MTextureCoords[0] == null || mesh->MNormals == null || mesh->MTangents == null)
        {
            Console.WriteLine($"  note: mesh {m} ({mesh->MName.AsString}) has no UVs; using a flat tangent frame");
        }

        uint baseVertex = (uint)vertices.Count;
        for (uint v = 0; v < mesh->MNumVertices; v++)
        {
            var normal = mesh->MNormals != null ? mesh->MNormals[v] : Vector3.UnitY;
            var uv = mesh->MTextureCoords[0] != null ? mesh->MTextureCoords[0][v] : Vector3.Zero;
            var tangent = new Vector4(Vector3.UnitX, 1f);
            if (mesh->MTangents != null && mesh->MBitangents != null)
            {
                float sign = Vector3.Dot(Vector3.Cross(normal, mesh->MTangents[v]), mesh->MBitangents[v]) < 0f ? -1f : 1f;
                tangent = new Vector4(mesh->MTangents[v], sign);
            }

            vertices.Add(new Vertex(mesh->MVertices[v], normal, tangent, new Vector2(uv.X, uv.Y)));
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
                Console.Error.WriteLine($"Could not read {path}: {assimp.GetErrorStringS()}");
                return 1;
            }

            string baseName = source.Name ?? Path.GetFileNameWithoutExtension(source.File);
            for (uint a = 0; a < extra->MNumAnimations; a++)
            {
                string name = extra->MNumAnimations == 1 ? baseName : $"{baseName}{a + 1}";
                AddClip(clips, rig.ReadClip(extra->MAnimations[a], name));
            }

            assimp.FreeScene(extra);
        }
    }

    assimp.FreeScene(scene);

    // The turn, scale and centring go into the vertices, and for a rig into its skeleton
    // too, so skinning lands in the same space.
    vertexArray = vertices.ToArray();
    var normalization = Normalization(vertexArray, settings.RotateY, settings.TargetLength);
    Apply(vertexArray, normalization);
    skeleton = rig?.ToSkeleton(normalization);
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
    Materials = materials.ToArray(),
    Textures = textures.ToArray(),
    Skeleton = skeleton,
    Clips = clips.ToArray(),
};

// A cutoff that can't cut renders fine but wrong, so it stops the import. Materials no
// part uses are never drawn, so they can't be wrong.
var problems = parts.Select(part => part.Material).Distinct().Order()
    .Select(i => (Name: materialNames[i], Problem: model.CutoutProblem(i)))
    .Where(entry => entry.Problem is not null)
    .ToList();
foreach (var (materialName, problem) in problems)
{
    Console.Error.WriteLine($"error: material {materialName}: {problem}");
}

if (problems.Count > 0)
{
    return 1;
}

model.Write(args[2]);

Console.WriteLine(
    $"Wrote {args[2]}: {vertices.Count} vertices, {indices.Count / 3} triangles, " +
    $"{parts.Count} parts, {materials.Count} materials, {textures.Count} textures, " +
    (skeleton is null ? "" : $"{skeleton.Count} joints, {clips.Count} clips, ") +
    $"{new FileInfo(args[2]).Length / (1024 * 1024)} MB");
return 0;

// Every mesh of one foliage material counts as one crown.
static void PointOutOfCrown(Vertex[] vertices, IEnumerable<(int Material, int First, int Count)> meshes)
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

static unsafe Scene* Import(Assimp assimp, string path, PostProcessSteps steps)
{
    // Mixamo FBX files otherwise sprout pivot nodes, which would break joint names
    // between a character and its animation files.
    var properties = assimp.CreatePropertyStore();
    assimp.SetImportPropertyInteger(properties, "IMPORT_FBX_PRESERVE_PIVOTS", 0);
    var scene = assimp.ImportFileExWithProperties(path, (uint)steps, null, properties);
    assimp.ReleasePropertyStore(properties);
    return scene;
}

static void AddClip(List<AnimationClip> clips, AnimationClip? clip)
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

// The transform that turns the model by rotateY degrees around the vertical axis, scales
// it to real-world metres, centres it, and stands it on y = 0. The bounds are taken after
// the turn, so the model fits its target length the way it will face.
// Drops the meshes of every node outside the ones named, so flattening leaves them out.
// Returns how many nodes with meshes were kept.
static unsafe int KeepNodes(Node* node, string keep, bool inside)
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

static Matrix4x4 Normalization(Vertex[] vertices, float rotateY, float targetLength)
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
    Console.WriteLine($"Source bounds: {size}");
    float scale = targetLength > 0f ? targetLength / MathF.Max(size.X, MathF.Max(size.Y, size.Z)) : 1f;
    var center = (min + max) * 0.5f;
    var shift = new Vector3(-center.X, -center.Y + size.Y * 0.5f, -center.Z);
    return turn * Matrix4x4.CreateTranslation(shift) * Matrix4x4.CreateScale(scale);
}

static void Apply(Vertex[] vertices, in Matrix4x4 transform)
{
    foreach (ref var vertex in vertices.AsSpan())
    {
        vertex.Position = Vector3.Transform(vertex.Position, transform);
        vertex.Normal = Vector3.Normalize(Vector3.TransformNormal(vertex.Normal, transform));
        var tangent = Vector3.TransformNormal(new Vector3(vertex.Tangent.X, vertex.Tangent.Y, vertex.Tangent.Z), transform);
        vertex.Tangent = new Vector4(Vector3.Normalize(tangent), vertex.Tangent.W);
    }
}

static unsafe ModelMaterial ImportMaterial(
    Assimp assimp, Silk.NET.Assimp.Material* source, ImportSettings settings, TextureCooker textures, bool isGltf,
    out string materialName)
{
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

    // Use a local copy, since lambdas can't capture an out parameter.
    string matchName = materialName;
    var rule = settings.Materials.FirstOrDefault(r => matchName.Contains(r.Match, StringComparison.OrdinalIgnoreCase));
    Console.WriteLine($"{materialName}: color={linkedColor ?? "-"} normal={linkedNormal ?? "-"} surface={linkedSurface ?? "-"} rule={rule?.Match ?? "(none)"}");

    rule ??= new MaterialRule();
    var flags = (rule.TwoSided || rule.Foliage ? MaterialFlags.TwoSided : 0)
        | (rule.Transparent ? MaterialFlags.Transparent : 0)
        | (rule.Foliage ? MaterialFlags.Foliage : 0);
    var emissive = rule.EmissiveColor is { Length: 3 } e
        ? new Vector3(e[0], e[1], e[2])
        : rule.Emissive != null ? Vector3.One : Vector3.Zero;
    return new ModelMaterial
    {
        BaseColor = rule.Color is { Length: 3 } c
            ? new Vector4(c[0], c[1], c[2], rule.Opacity)
            : fileColor is { } f ? new Vector4(f[0], f[1], f[2], f[3]) : new Vector4(1f, 1f, 1f, rule.Opacity),
        Metallic = rule.Metallic ?? fileMetallic?[0] ?? 0f,
        Roughness = rule.Roughness ?? fileRoughness?[0] ?? 0.6f,
        AlphaCutoff = rule.AlphaCutoff ?? fileCutoff ?? 0f,
        BaseColorTexture = textures.Add(rule.BaseColor ?? linkedColor, TextureKind.Color, alphaFile: rule.Alpha),
        NormalTexture = textures.Add(rule.NoNormalMap ? null : rule.Normal ?? linkedNormal, TextureKind.Linear, isNormalMap: true),
        SurfaceTexture = rule.Surface is not null
            ? textures.Add(rule.Surface, TextureKind.Linear)
            : textures.Add(linkedSurface, TextureKind.Linear, isGltfSurface: true),
        Emissive = emissive,
        EmissiveTexture = textures.Add(rule.Emissive, TextureKind.Color),
        Clearcoat = rule.Clearcoat,
        Flags = flags,
    };
}

// glTF's MASK mode cuts out by alpha, at 0.5 unless the file says otherwise.
static unsafe float? GetGltfCutoff(Assimp assimp, Silk.NET.Assimp.Material* material)
{
    AssimpString mode;
    if (assimp.GetMaterialString(material, "$mat.gltf.alphaMode", 0, 0, &mode) != Return.Success
        || mode.AsString != "MASK")
    {
        return null;
    }

    return GetFloats(assimp, material, "$mat.gltf.alphaCutoff", 1)?[0] ?? 0.5f;
}

static unsafe float[]? GetFloats(Assimp assimp, Silk.NET.Assimp.Material* material, string key, uint count)
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

static unsafe string? GetTexture(Assimp assimp, Silk.NET.Assimp.Material* material, TextureType type)
{
    AssimpString path;
    var result = assimp.GetMaterialTexture(material, type, 0, &path, null, null, null, null, null, null);
    return result == Return.Success ? Path.GetFileName(path.AsString.Replace('\\', '/')) : null;
}
