using System.Numerics;
using Rendition;
using Silk.NET.Assimp;
using Skeleton = Rendition.Skeleton;

namespace Super.Cook.Models;

using Scene = Silk.NET.Assimp.Scene;

/// <summary>
/// Reads the skeleton, bone weights and clips of a rigged scene. The joints are every
/// node a mesh's bone names, and every node between one of those and the root, in
/// parent-first order, and the parent of each node that holds an unskinned mesh. Clips are
/// sampled at <see cref="FrameRate"/>.
/// </summary>
internal sealed unsafe class RigImporter
{
    public const float FrameRate = ClipSampler.FrameRate;

    private readonly JointNames _joints;
    private readonly int[] _parents;
    private readonly JointPose[] _rest;
    private readonly Matrix4x4[] _restGlobal;
    private readonly Matrix4x4[] _inverseBind;
    private readonly SceneNodes _nodes;
    private readonly Action<string> _warn;

    private RigImporter(Scene* scene, Action<string> warn)
    {
        _warn = warn;
        _nodes = new SceneNodes(scene);

        // A container that isn't a bone gets its rest pose's inverse as its inverse bind. A container that is a bone
        // keeps the bone's, and Skin moves the mesh's vertices to match it. Every container and its ancestors count
        // towards Skeleton.MaxJoints.
        (string[] names, _parents, _rest, _restGlobal) = ListJoints(_nodes.Joints(scene));
        if (names.Length > Skeleton.MaxJoints)
        {
            throw new InvalidDataException($"The model has {names.Length} joints, more than the {Skeleton.MaxJoints} a model can have.");
        }

        _joints = new JointNames(names);
        _inverseBind = InverseBinds(scene);
    }

    public int JointCount => _joints.Names.Length;

    /// <summary>
    /// Moves every vertex from the bind space into where the file shows the model at rest, and the
    /// inverse binds with them, so skinning lands each vertex where it did before and a draw with no
    /// pose, such as a swimming fish, stands as the file shows it. A file's bind is often a turn away
    /// from its rest, such as an armature turned up from z-up, and without this an unposed draw lies on
    /// its side or its nose. One move fits every joint whose bind matches its rest; it's taken from the
    /// joint that carries the most weight over the skinned vertices. A move that mirrors the model is left out, since it would
    /// turn the triangles inside out.
    /// </summary>
    public void MoveToRest(Span<Vertex> vertices)
    {
        var carried = new long[JointCount];
        foreach (var vertex in vertices)
        {
            for (int i = 0; i < 4; i++)
            {
                carried[(vertex.Joints >> (i * 8)) & 0xFF] += (vertex.Weights >> (i * 8)) & 0xFF;
            }
        }

        int heaviest = Array.IndexOf(carried, carried.Max());
        var toRest = _inverseBind[heaviest] * _restGlobal[heaviest];
        if (toRest.GetDeterminant() <= 0f || !Matrix4x4.Invert(toRest, out var fromRest) || toRest.IsIdentity)
        {
            return;
        }

        // Normals keep pointing off the surface when the move stretches it, so they use the inverse transpose.
        var normals = Matrix4x4.Transpose(fromRest);
        foreach (ref var vertex in vertices)
        {
            vertex.Position = Vector3.Transform(vertex.Position, toRest);
            vertex.Normal = Vector3.Normalize(Vector3.TransformNormal(vertex.Normal, normals));
            var tangent = Vector3.TransformNormal(new Vector3(vertex.Tangent.X, vertex.Tangent.Y, vertex.Tangent.Z), toRest);
            vertex.Tangent = new Vector4(Vector3.Normalize(tangent), vertex.Tangent.W);
        }

        for (int i = 0; i < _inverseBind.Length; i++)
        {
            _inverseBind[i] = fromRest * _inverseBind[i];
        }
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

        return new Skeleton(_joints.Names, _parents, _rest, inverseBind, normalize);
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
            SkinToNode(meshIndex, vertices);
            return;
        }

        var influences = SkinInfluences.Of(mesh, vertices.Length, _joints.ByName);
        int dropped = 0;
        for (int v = 0; v < vertices.Length; v++)
        {
            var list = influences[v];
            if (list is null)
            {
                vertices[v].SetSkin([(_nodes.NearestJoint(_nodes.OfMesh(meshIndex)), 1f)]);
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
    public AnimationClip? ReadClip(Animation* animation, string name, bool inPlace = false)
    {
        if (ClipSampler.Channels(animation, _joints) is not { } channels)
        {
            _warn($"clip {name} moves no joint of this rig, so it's left out");
            return null;
        }

        var frames = ClipSampler.Frames(animation, channels, _rest);
        return new AnimationClip(name, FrameRate, JointCount, inPlace ? ClipInPlace.Held(frames, _parents, _restGlobal) : frames);
    }

    /// <summary>The name of the node that holds a mesh, which names the mesh's part.</summary>
    public string NodeOfMeshName(int mesh) => _nodes[_nodes.OfMesh(mesh)].Name;

    /// <summary>
    /// Moves an unskinned mesh to where its node sits and binds it all to the nearest joint above it.
    /// </summary>
    private void SkinToNode(int meshIndex, Span<Vertex> vertices)
    {
        int node = _nodes.OfMesh(meshIndex);
        int joint = _nodes.NearestJoint(node);

        // Skinning multiplies by the joint's inverse bind. For a bone that's the skinned mesh's
        // bind, which needn't match the node tree's rest pose, so a mesh placed in the node tree
        // would land off by the difference. Taking the vertices to the bind's space first makes
        // the mesh sit where the file puts it at rest. For a joint that isn't a bone the inverse
        // bind is the rest pose's inverse, and this leaves the vertices alone.
        var global = _nodes[node].Global;
        if (Matrix4x4.Invert(_restGlobal[joint], out var fromRest) && Matrix4x4.Invert(_inverseBind[joint], out var bind))
        {
            global = global * fromRest * bind;
        }

        foreach (ref var vertex in vertices)
        {
            vertex.Position = Vector3.Transform(vertex.Position, global);
            vertex.Normal = Vector3.Normalize(Vector3.TransformNormal(vertex.Normal, global));
            var tangent = Vector3.TransformNormal(new Vector3(vertex.Tangent.X, vertex.Tangent.Y, vertex.Tangent.Z), global);
            vertex.Tangent = new Vector4(Vector3.Normalize(tangent), vertex.Tangent.W);
            vertex.SetSkin([(joint, 1f)]);
        }
    }

    /// <summary>
    /// The joints' names, parents, local rest poses and global rest transforms, in node order, numbering each joint
    /// node as it goes.
    /// </summary>
    private (string[] Names, int[] Parents, JointPose[] Poses, Matrix4x4[] RestGlobal) ListJoints(bool[] isJoint)
    {
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
        }

        return (names.ToArray(), parents.ToArray(), rest.ToArray(), restGlobal.ToArray());
    }

    /// <summary>
    /// Each joint's inverse bind. A bone's own inverse bind matrix is the truth. Other joints only carry static
    /// meshes along, so theirs undoes the rest pose.
    /// </summary>
    private Matrix4x4[] InverseBinds(Scene* scene)
    {
        var inverseBind = new Matrix4x4[JointCount];
        for (int i = 0; i < inverseBind.Length; i++)
        {
            Matrix4x4.Invert(_restGlobal[i], out inverseBind[i]);
        }

        for (uint m = 0; m < scene->MNumMeshes; m++)
        {
            var mesh = scene->MMeshes[m];
            for (uint b = 0; b < mesh->MNumBones; b++)
            {
                var bone = mesh->MBones[b];
                inverseBind[_joints.ByName[bone->MName.AsString]] = Matrix4x4.Transpose(bone->MOffsetMatrix);
            }
        }

        return inverseBind;
    }
}
