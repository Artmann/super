using System.Numerics;
using Rendition;
using Silk.NET.Assimp;
using Assimp = Silk.NET.Assimp.Assimp;
using Skeleton = Rendition.Skeleton;

namespace Super.Cook.Models;

/// <summary>
/// Reads the skeleton, bone weights and clips of a rigged scene. The joints are every
/// node a mesh's bone names, and every node between one of those and the root, in
/// parent-first order, and the parent of each node that holds an unskinned mesh. Clips are
/// sampled at <see cref="FrameRate"/>.
/// </summary>
internal sealed unsafe class RigImporter
{
    public const float FrameRate = 30f;

    private readonly string[] _names;
    private readonly int[] _parents;
    private readonly JointPose[] _rest;
    private readonly Matrix4x4[] _restGlobal;
    private readonly Matrix4x4[] _inverseBind;
    private readonly Dictionary<string, int> _jointOf = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _jointByBareName = new(StringComparer.Ordinal);

    // Every node of the scene, parent first, with its global rest transform, so a mesh
    // that hangs off a node without a skin can be placed and attached.
    private readonly List<(string Name, int Parent, Matrix4x4 Global, int Joint)> _nodes = [];
    private readonly Dictionary<int, int> _meshNode = [];
    private readonly Action<string> _warn;

    private RigImporter(Scene* scene, Action<string> warn)
    {
        _warn = warn;
        var boneNames = new HashSet<string>(StringComparer.Ordinal);
        for (uint m = 0; m < scene->MNumMeshes; m++)
        {
            var mesh = scene->MMeshes[m];
            for (uint b = 0; b < mesh->MNumBones; b++)
            {
                boneNames.Add(mesh->MBones[b]->MName.AsString);
            }
        }

        Walk(scene->MRootNode, -1, Matrix4x4.Identity);

        // A node is a joint when it is a bone or has one below it. The parent of a node that
        // carries an unskinned mesh is a joint too, so the mesh follows that container's
        // animated chain. A container is no bone, so its inverse bind is its rest pose's inverse
        // in the node tree, where Skin placed the mesh's vertices.
        var isJoint = new bool[_nodes.Count];
        foreach (var (mesh, node) in _meshNode)
        {
            if (scene->MMeshes[mesh]->MNumBones == 0 && _nodes[node].Parent >= 0)
            {
                isJoint[_nodes[node].Parent] = true;
            }
        }

        for (int i = _nodes.Count - 1; i >= 0; i--)
        {
            if (boneNames.Contains(_nodes[i].Name))
            {
                isJoint[i] = true;
            }

            if (isJoint[i] && _nodes[i].Parent >= 0)
            {
                isJoint[_nodes[i].Parent] = true;
            }
        }

        var names = new List<string>();
        var parents = new List<int>();
        var rest = new List<JointPose>();
        var restGlobal = new List<Matrix4x4>();
        for (int i = 0; i < _nodes.Count; i++)
        {
            if (!isJoint[i])
            {
                continue;
            }

            var (name, parent, global, _) = _nodes[i];
            int joint = names.Count;
            _nodes[i] = (name, parent, global, joint);
            names.Add(name);
            parents.Add(parent >= 0 ? _nodes[parent].Joint : -1);
            restGlobal.Add(global);
            var local = parent >= 0 && Matrix4x4.Invert(_nodes[parent].Global, out var inverseParent)
                ? global * inverseParent
                : global;
            rest.Add(JointPose.FromMatrix(local));
            _jointOf.TryAdd(name, joint);
        }

        if (names.Count > Skeleton.MaxJoints)
        {
            throw new InvalidDataException($"The model has {names.Count} joints, more than the {Skeleton.MaxJoints} a model can have.");
        }

        _names = names.ToArray();
        _parents = parents.ToArray();
        _rest = rest.ToArray();
        _restGlobal = restGlobal.ToArray();

        // A bone's own inverse bind matrix is the truth. Other joints only carry static
        // meshes along, so theirs undoes the rest pose.
        _inverseBind = new Matrix4x4[_names.Length];
        for (int i = 0; i < _inverseBind.Length; i++)
        {
            Matrix4x4.Invert(_restGlobal[i], out _inverseBind[i]);
        }

        for (uint m = 0; m < scene->MNumMeshes; m++)
        {
            var mesh = scene->MMeshes[m];
            for (uint b = 0; b < mesh->MNumBones; b++)
            {
                var bone = mesh->MBones[b];
                _inverseBind[_jointOf[bone->MName.AsString]] = Matrix4x4.Transpose(bone->MOffsetMatrix);
            }
        }
    }

    public int JointCount => _names.Length;

    public static bool HasBones(Scene* scene)
    {
        for (uint m = 0; m < scene->MNumMeshes; m++)
        {
            if (scene->MMeshes[m]->MNumBones > 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <param name="warn">Told about weights and clips that had to be dropped.</param>
    public static RigImporter Read(Scene* scene, Action<string> warn) => new(scene, warn);

    /// <summary>
    /// The skeleton, with <paramref name="normalize"/> (the importer's scale, centring and
    /// turn, already applied to the vertices) folded in so skinning lands in the same
    /// space as the vertices.
    /// </summary>
    public Skeleton ToSkeleton(in Matrix4x4 normalize)
    {
        if (!Matrix4x4.Invert(normalize, out var undo))
        {
            throw new InvalidOperationException("The model's normalization can't be undone, so it can't be skinned.");
        }

        var inverseBind = new Matrix4x4[_inverseBind.Length];
        for (int i = 0; i < inverseBind.Length; i++)
        {
            inverseBind[i] = undo * _inverseBind[i];
        }

        return new Skeleton(_names, _parents, _rest, inverseBind, normalize);
    }

    /// <summary>
    /// Fills the joints and weights of a mesh's vertices. A mesh without bones is moved
    /// to where its node sits and follows the nearest joint above it, so a sword in a hand
    /// node swings with the hand.
    /// </summary>
    public void Skin(Mesh* mesh, int meshIndex, Span<Vertex> vertices)
    {
        if (mesh->MNumBones == 0)
        {
            int node = NodeOfMesh(meshIndex);
            var global = _nodes[node].Global;
            int joint = NearestJoint(node);
            foreach (ref var vertex in vertices)
            {
                vertex.Position = Vector3.Transform(vertex.Position, global);
                vertex.Normal = Vector3.Normalize(Vector3.TransformNormal(vertex.Normal, global));
                var tangent = Vector3.TransformNormal(new Vector3(vertex.Tangent.X, vertex.Tangent.Y, vertex.Tangent.Z), global);
                vertex.Tangent = new Vector4(Vector3.Normalize(tangent), vertex.Tangent.W);
                vertex.SetSkin([(joint, 1f)]);
            }

            return;
        }

        var influences = new List<(int Joint, float Weight)>[vertices.Length];
        for (uint b = 0; b < mesh->MNumBones; b++)
        {
            var bone = mesh->MBones[b];
            int joint = _jointOf[bone->MName.AsString];
            for (uint w = 0; w < bone->MNumWeights; w++)
            {
                var weight = bone->MWeights[w];
                (influences[weight.MVertexId] ??= []).Add((joint, weight.MWeight));
            }
        }

        int dropped = 0;
        for (int v = 0; v < vertices.Length; v++)
        {
            var list = influences[v];
            if (list is null)
            {
                vertices[v].SetSkin([(NearestJoint(NodeOfMesh(meshIndex)), 1f)]);
                continue;
            }

            list.Sort((a, b) => b.Weight.CompareTo(a.Weight));
            if (list.Count > 4)
            {
                dropped++;
                list.RemoveRange(4, list.Count - 4);
            }

            vertices[v].SetSkin(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list));
        }

        if (dropped > 0)
        {
            _warn($"{dropped} vertices of mesh {meshIndex} follow more than four joints; the weakest were dropped");
        }
    }

    /// <summary>
    /// Samples a clip into frames at <see cref="FrameRate"/>. Joints the animation doesn't
    /// move hold their rest pose. Channels are matched to joints by node name, so a clip
    /// from another file of the same rig fits.
    /// </summary>
    public AnimationClip? ReadClip(Animation* animation, string name)
    {
        double ticksPerSecond = animation->MTicksPerSecond > 0 ? animation->MTicksPerSecond : 25.0;
        float seconds = (float)(animation->MDuration / ticksPerSecond);
        int frameCount = Math.Max(1, (int)MathF.Round(seconds * FrameRate) + 1);

        var channels = new NodeAnim*[JointCount];
        int matched = 0;
        for (uint c = 0; c < animation->MNumChannels; c++)
        {
            var channel = animation->MChannels[c];
            if (JointOf(channel->MNodeName.AsString) is { } joint)
            {
                channels[joint] = channel;
                matched++;
            }
        }

        if (matched == 0)
        {
            _warn($"clip {name} moves no joint of this rig, so it's left out");
            return null;
        }

        var frames = new JointPose[frameCount * JointCount];
        for (int frame = 0; frame < frameCount; frame++)
        {
            double ticks = Math.Min(frame / (double)FrameRate * ticksPerSecond, animation->MDuration);
            for (int joint = 0; joint < JointCount; joint++)
            {
                var channel = channels[joint];
                frames[frame * JointCount + joint] = channel == null
                    ? _rest[joint]
                    : new JointPose(
                        SampleVector(channel->MPositionKeys, channel->MNumPositionKeys, ticks, _rest[joint].Position),
                        SampleRotation(channel->MRotationKeys, channel->MNumRotationKeys, ticks, _rest[joint].Rotation),
                        SampleVector(channel->MScalingKeys, channel->MNumScalingKeys, ticks, _rest[joint].Scale));
            }
        }

        return new AnimationClip(name, FrameRate, JointCount, frames);
    }

    /// <summary>
    /// The joint a clip's channel drives. Mixamo names joints "mixamorig:Hips", but adds a
    /// number to the prefix in some downloads ("mixamorig12:Hips"), so when the full name
    /// isn't a joint, the part after the colon is matched on its own.
    /// </summary>
    private int? JointOf(string channel)
    {
        if (_jointOf.TryGetValue(channel, out int joint))
        {
            return joint;
        }

        if (_jointByBareName.Count == 0)
        {
            for (int i = 0; i < _names.Length; i++)
            {
                _jointByBareName.TryAdd(BareName(_names[i]), i);
            }
        }

        return _jointByBareName.TryGetValue(BareName(channel), out joint) ? joint : null;
    }

    private static string BareName(string name) => name[(name.LastIndexOf(':') + 1)..];

    private void Walk(Node* node, int parent, in Matrix4x4 parentGlobal)
    {
        // Assimp stores matrices for column vectors; this engine multiplies row vectors.
        var local = Matrix4x4.Transpose(node->MTransformation);
        var global = local * parentGlobal;
        int index = _nodes.Count;
        _nodes.Add((node->MName.AsString, parent, global, -1));
        for (uint i = 0; i < node->MNumMeshes; i++)
        {
            _meshNode[(int)node->MMeshes[i]] = index;
        }

        for (uint i = 0; i < node->MNumChildren; i++)
        {
            Walk(node->MChildren[i], index, global);
        }
    }

    private int NodeOfMesh(int mesh) => _meshNode.GetValueOrDefault(mesh, 0);

    private int NearestJoint(int node)
    {
        while (node >= 0 && _nodes[node].Joint < 0)
        {
            node = _nodes[node].Parent;
        }

        return node >= 0 ? _nodes[node].Joint : 0;
    }

    private static Vector3 SampleVector(VectorKey* keys, uint count, double ticks, Vector3 rest)
    {
        if (count == 0)
        {
            return rest;
        }

        int next = FindNext(keys, count, ticks);
        if (next == 0)
        {
            return keys[0].MValue;
        }

        if (next >= count)
        {
            return keys[count - 1].MValue;
        }

        var before = keys[next - 1];
        var after = keys[next];
        return Vector3.Lerp(before.MValue, after.MValue, Share(before.MTime, after.MTime, ticks));
    }

    private static Quaternion SampleRotation(QuatKey* keys, uint count, double ticks, Quaternion rest)
    {
        if (count == 0)
        {
            return rest;
        }

        int next = FindNext(keys, count, ticks);
        if (next == 0)
        {
            return ToQuaternion(keys[0].MValue);
        }

        if (next >= count)
        {
            return ToQuaternion(keys[count - 1].MValue);
        }

        var before = keys[next - 1];
        var after = keys[next];
        return Quaternion.Slerp(ToQuaternion(before.MValue), ToQuaternion(after.MValue), Share(before.MTime, after.MTime, ticks));
    }

    private static int FindNext(VectorKey* keys, uint count, double ticks)
    {
        int next = 0;
        while (next < count && keys[next].MTime <= ticks)
        {
            next++;
        }

        return next;
    }

    private static int FindNext(QuatKey* keys, uint count, double ticks)
    {
        int next = 0;
        while (next < count && keys[next].MTime <= ticks)
        {
            next++;
        }

        return next;
    }

    private static float Share(double from, double to, double at) =>
        to > from ? (float)Math.Clamp((at - from) / (to - from), 0.0, 1.0) : 0f;

    private static Quaternion ToQuaternion(AssimpQuaternion q) => Quaternion.Normalize(new Quaternion(q.X, q.Y, q.Z, q.W));
}
