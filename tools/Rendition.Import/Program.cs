using System.Numerics;
using System.Text.Json;
using Rendition;
using Rendition.Import;
using Silk.NET.Assimp;
using Assimp = Silk.NET.Assimp.Assimp;
using File = System.IO.File;

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

unsafe
{
    var assimp = Assimp.GetApi();
    const PostProcessSteps steps = PostProcessSteps.Triangulate
        | PostProcessSteps.JoinIdenticalVertices
        | PostProcessSteps.PreTransformVertices
        | PostProcessSteps.GenerateSmoothNormals
        | PostProcessSteps.CalculateTangentSpace
        | PostProcessSteps.FlipUVs;

    var scene = assimp.ImportFile(modelPath, (uint)steps);
    if (scene == null || scene->MRootNode == null)
    {
        Console.Error.WriteLine($"Could not read {modelPath}: {assimp.GetErrorStringS()}");
        return 1;
    }

    // Materials refer to images stored inside the file as "*0", "*1" and so on.
    for (uint i = 0; i < scene->MNumTextures; i++)
    {
        var embedded = scene->MTextures[i];
        if (embedded->MHeight == 0) // A height of 0 means the width is the size of an encoded image.
        {
            textures.AddEmbedded($"*{i}", new ReadOnlySpan<byte>(embedded->PcData, (int)embedded->MWidth).ToArray());
        }
    }

    for (uint i = 0; i < scene->MNumMaterials; i++)
    {
        materials.Add(ImportMaterial(assimp, scene->MMaterials[i], settings, textures, isGltf));
    }

    for (uint m = 0; m < scene->MNumMeshes; m++)
    {
        var mesh = scene->MMeshes[m];
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

    assimp.FreeScene(scene);
}

var vertexArray = vertices.ToArray();
Rotate(vertexArray, settings.RotateY);
Normalize(vertexArray, settings.TargetLength);

new ModelData
{
    Vertices = vertexArray,
    Indices = indices.ToArray(),
    Parts = parts.ToArray(),
    Materials = materials.ToArray(),
    Textures = textures.ToArray(),
}.Write(args[2]);

Console.WriteLine(
    $"Wrote {args[2]}: {vertices.Count} vertices, {indices.Count / 3} triangles, " +
    $"{parts.Count} parts, {materials.Count} materials, {textures.Count} textures, " +
    $"{new FileInfo(args[2]).Length / (1024 * 1024)} MB");
return 0;

static void Rotate(Vertex[] vertices, float degrees)
{
    var turn = Matrix4x4.CreateRotationY(degrees * MathF.PI / 180f);
    foreach (ref var vertex in vertices.AsSpan())
    {
        vertex.Position = Vector3.Transform(vertex.Position, turn);
        vertex.Normal = Vector3.TransformNormal(vertex.Normal, turn);
        var tangent = Vector3.TransformNormal(new Vector3(vertex.Tangent.X, vertex.Tangent.Y, vertex.Tangent.Z), turn);
        vertex.Tangent = new Vector4(tangent, vertex.Tangent.W);
    }
}

// Scales the model to real-world metres, centres it, and stands it on y = 0.
static void Normalize(Vertex[] vertices, float targetLength)
{
    var min = new Vector3(float.MaxValue);
    var max = new Vector3(float.MinValue);
    foreach (ref readonly var vertex in vertices.AsSpan())
    {
        min = Vector3.Min(min, vertex.Position);
        max = Vector3.Max(max, vertex.Position);
    }

    var size = max - min;
    Console.WriteLine($"Source bounds: {size}");
    float scale = targetLength > 0f ? targetLength / MathF.Max(size.X, MathF.Max(size.Y, size.Z)) : 1f;
    var center = (min + max) * 0.5f;
    foreach (ref var vertex in vertices.AsSpan())
    {
        var position = vertex.Position - center;
        position.Y += size.Y * 0.5f;
        vertex.Position = position * scale;
    }
}

static unsafe ModelMaterial ImportMaterial(
    Assimp assimp, Silk.NET.Assimp.Material* source, ImportSettings settings, TextureCooker textures, bool isGltf)
{
    AssimpString name;
    assimp.GetMaterialString(source, Assimp.MaterialNameBase, 0, 0, &name);
    string materialName = name.AsString;

    string? linkedColor = GetTexture(assimp, source, TextureType.Diffuse) ?? GetTexture(assimp, source, TextureType.BaseColor);
    string? linkedNormal = GetTexture(assimp, source, TextureType.Normals) ?? GetTexture(assimp, source, TextureType.Height);

    // Only glTF defines these values and the combined metal and roughness texture.
    string? linkedSurface = isGltf
        ? GetTexture(assimp, source, TextureType.Unknown) ?? GetTexture(assimp, source, TextureType.Metalness)
        : null;
    float[]? fileColor = isGltf ? GetFloats(assimp, source, "$clr.base", 4) : null;
    float[]? fileMetallic = isGltf ? GetFloats(assimp, source, "$mat.metallicFactor", 1) : null;
    float[]? fileRoughness = isGltf ? GetFloats(assimp, source, "$mat.roughnessFactor", 1) : null;

    var rule = settings.Materials.FirstOrDefault(r => materialName.Contains(r.Match, StringComparison.OrdinalIgnoreCase));
    Console.WriteLine($"{materialName}: color={linkedColor ?? "-"} normal={linkedNormal ?? "-"} surface={linkedSurface ?? "-"} rule={rule?.Match ?? "(none)"}");

    rule ??= new MaterialRule();
    var flags = (rule.TwoSided ? MaterialFlags.TwoSided : 0) | (rule.Transparent ? MaterialFlags.Transparent : 0);
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
        AlphaCutoff = rule.AlphaCutoff,
        BaseColorTexture = textures.Add(rule.BaseColor ?? linkedColor, TextureKind.Color),
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
