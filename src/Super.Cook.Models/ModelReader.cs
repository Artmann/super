using System.Numerics;
using Rendition;
using Silk.NET.Assimp;
using Assimp = Silk.NET.Assimp.Assimp;
using Skeleton = Rendition.Skeleton;

namespace Super.Cook.Models;

/// <summary>
/// A model as read from its file: its meshes, with their vertices turned, scaled and centred, its materials, and for a
/// rig its skeleton and clips.
/// </summary>
internal sealed record ReadModel(
    ModelMeshes Meshes, Vertex[] Vertices, ImportedMaterials Materials, Skeleton? Skeleton, List<AnimationClip> Clips);

/// <summary>
/// Reads a model's file with Assimp. A rigged model keeps its node tree, since the joints live there. A static one is
/// flattened into a single mesh space, which merges the meshes that share a material. With keepMeshes it keeps its node
/// tree instead, and each mesh is placed by its node, so every mesh stays its own part with its name.
/// </summary>
internal static unsafe class ModelReader
{
    private const PostProcessSteps Steps = PostProcessSteps.Triangulate
        | PostProcessSteps.JoinIdenticalVertices
        | PostProcessSteps.GenerateSmoothNormals
        | PostProcessSteps.CalculateTangentSpace
        | PostProcessSteps.FlipUVs;

    private static readonly Lazy<Assimp> Api = new(Assimp.GetApi);

    /// <returns>The model, or null when it failed and said why.</returns>
    public static ReadModel? Read(ModelCook cook)
    {
        // Assimp opens the animation files itself, so the pipeline can't see them.
        foreach (var source in cook.Settings.Animations)
        {
            cook.Input.Track(source.File);
        }

        var assimp = Api.Value;
        Scene* scene;
        using (cook.Input.Time("assimp import"))
        {
            scene = Import(assimp, cook.Input.FullPath!);
        }

        try
        {
            if (scene == null || scene->MRootNode == null)
            {
                cook.Error($"Could not read {cook.Input.Name}: {assimp.GetErrorStringS()}");
                return null;
            }

            RigImporter? rig = SceneNodes.HasBones(scene) ? RigImporter.Read(scene, cook.Warning) : null;
            if (rig is null && !Flatten(cook, assimp, ref scene))
            {
                return null;
            }

            return ReadScene(cook, assimp, scene, rig);
        }
        finally
        {
            if (scene != null)
            {
                assimp.FreeScene(scene);
            }
        }
    }

    /// <summary>
    /// Leaves out the meshes of a static model outside the node its settings name, and flattens it unless it keeps its
    /// meshes. False when that failed, which was reported.
    /// </summary>
    private static bool Flatten(ModelCook cook, Assimp assimp, ref Scene* scene)
    {
        if (cook.Settings.Node is { } keep && KeepNodes(scene->MRootNode, keep, false) == 0)
        {
            cook.Error($"No node with meshes has \"{keep}\" in its name.");
            return false;
        }

        if (cook.Settings.KeepMeshes)
        {
            return true;
        }

        // On failure Assimp has already freed the scene.
        using (cook.Input.Time("assimp flatten"))
        {
            scene = assimp.ApplyPostProcessing(scene, (uint)PostProcessSteps.PreTransformVertices);
        }

        if (scene == null)
        {
            cook.Error($"Could not flatten {cook.Input.Name}: {assimp.GetErrorStringS()}");
            return false;
        }

        return true;
    }

    private static ReadModel? ReadScene(ModelCook cook, Assimp assimp, Scene* scene, RigImporter? rig)
    {
        AddEmbeddedTextures(scene, cook.Textures);
        var materials = ImportedMaterials.Read(cook, assimp, scene);
        var meshes = AddMeshes(cook, scene, rig, materials);
        var clips = new List<AnimationClip>();
        if (rig is not null && !ReadClips(cook, assimp, scene, rig, clips))
        {
            return null;
        }

        // The turn, scale and centring go into the vertices, and for a rig into its skeleton
        // too, so skinning lands in the same space.
        var vertices = meshes.Vertices.ToArray();
        rig?.MoveToRest(vertices);
        var settings = cook.Settings;
        var normalization = Normalization(vertices, settings.RotateY, settings.TargetLength, settings.KeepOrigin);
        Apply(vertices, normalization);
        var skeleton = rig?.ToSkeleton(normalization).WithBounds(vertices);
        return new ReadModel(meshes, vertices, materials, skeleton, clips);
    }

    // glTF materials refer to images stored inside the file as "*0", "*1" and so on, while
    // FBX materials use the image's original file name, so each is stored under both.
    private static void AddEmbeddedTextures(Scene* scene, ModelTextures textures)
    {
        for (uint i = 0; i < scene->MNumTextures; i++)
        {
            var embedded = scene->MTextures[i];
            if (embedded->MHeight != 0) // A height of 0 means the width is the size of an encoded image.
            {
                continue;
            }

            byte[] encoded = new ReadOnlySpan<byte>(embedded->PcData, (int)embedded->MWidth).ToArray();
            textures.AddEmbedded($"*{i}", encoded);
            string original = embedded->MFilename.AsString;
            if (original.Length > 0)
            {
                textures.AddEmbedded(Path.GetFileName(original.Replace('\\', '/')), encoded);
            }
        }
    }

    private static ModelMeshes AddMeshes(ModelCook cook, Scene* scene, RigImporter? rig, ImportedMaterials materials)
    {
        // Only a placed mesh keeps its name. A merged part has no single mesh to name it after.
        bool named = rig is not null || cook.Settings.KeepMeshes;
        var meshes = new ModelMeshes();
        foreach (var (m, world, node) in Placements(scene, rig is null && cook.Settings.KeepMeshes))
        {
            var mesh = scene->MMeshes[m];
            int material = (int)mesh->MMaterialIndex;

            // Without UVs every texel read lands on one corner. A material with no textures doesn't care.
            if (mesh->MTextureCoords[0] == null && SamplesTexture(materials.Materials[material]))
            {
                cook.Warning($"mesh {m} ({mesh->MName.AsString}) has no UVs; using a flat tangent frame");
            }

            uint baseVertex = (uint)meshes.Vertices.Count;
            var added = meshes.AddVertices(mesh, world, materials.GraphWidths[material]);
            rig?.Skin(mesh, (int)m, added);
            meshes.AddTriangles(mesh, baseVertex);
            if (named)
            {
                meshes.MeshNames.Add(MeshName(rig?.NodeOfMeshName((int)m) ?? node, mesh));
            }
        }

        return meshes;
    }

    /// <summary>Reads a rig's clips, its own and then its settings' animation files'. False when a file can't be read, which was reported.</summary>
    private static bool ReadClips(ModelCook cook, Assimp assimp, Scene* scene, RigImporter rig, List<AnimationClip> clips)
    {
        for (uint a = 0; a < scene->MNumAnimations; a++)
        {
            var animation = scene->MAnimations[a];
            string name = animation->MName.AsString is { Length: > 0 } own ? own : $"clip{a}";
            AddClip(clips, rig.ReadClip(animation, name));
        }

        foreach (var source in cook.Settings.Animations)
        {
            if (!ReadClipFile(cook, assimp, source, rig, clips))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ReadClipFile(ModelCook cook, Assimp assimp, AnimationSource source, RigImporter rig, List<AnimationClip> clips)
    {
        string path = Path.Combine(Path.GetDirectoryName(cook.Input.FullPath!)!, source.File);
        var extra = Import(assimp, path);
        if (extra == null)
        {
            cook.Error($"Could not read {source.File}: {assimp.GetErrorStringS()}");
            return false;
        }

        try
        {
            string baseName = source.Name ?? Path.GetFileNameWithoutExtension(source.File);
            for (uint a = 0; a < extra->MNumAnimations; a++)
            {
                string name = extra->MNumAnimations == 1 ? baseName : $"{baseName}{a + 1}";
                AddClip(clips, rig.ReadClip(extra->MAnimations[a], name, source.InPlace));
            }
        }
        finally
        {
            assimp.FreeScene(extra);
        }

        return true;
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

    private static Scene* Import(Assimp assimp, string path)
    {
        // Mixamo FBX files otherwise sprout pivot nodes, which would break joint names
        // between a character and its animation files.
        var properties = assimp.CreatePropertyStore();
        assimp.SetImportPropertyInteger(properties, "IMPORT_FBX_PRESERVE_PIVOTS", 0);
        var scene = assimp.ImportFileExWithProperties(path, (uint)Steps, null, properties);
        assimp.ReleasePropertyStore(properties);
        return scene;
    }

    private static bool SamplesTexture(ModelMaterial material)
    {
        return material.BaseColorTexture >= 0 || material.NormalTexture >= 0
            || material.SurfaceTexture >= 0 || material.EmissiveTexture >= 0
            || material.HeightTexture >= 0 || material.DetailColorTexture >= 0 || material.DetailNormalTexture >= 0;
    }

    // The meshes to cook, each with the transform that puts it in the model's space. With byNode, they
    // come in the order the nodes hold them, and each is placed by its node. Otherwise every mesh is
    // already in the model's space: a rig's meshes stay in their own space, since the rig importer
    // places them and the joints move them, and a merged static scene was flattened by Assimp.
    // Node is the name of the node holding the mesh when placed by node, and null otherwise.
    private static List<(uint Mesh, Matrix4x4 World, string? Node)> Placements(Scene* scene, bool byNode)
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

    private static void Place(Node* node, in Matrix4x4 parent, List<(uint Mesh, Matrix4x4 World, string? Node)> placed)
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
    private static string MeshName(string? node, Mesh* mesh) =>
        string.IsNullOrEmpty(node) ? mesh->MName.AsString : node;

    // Drops the meshes of every node outside the ones named, so flattening leaves them out.
    // Returns how many nodes with meshes were kept.
    private static int KeepNodes(Node* node, string keep, bool inside)
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
}
