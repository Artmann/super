using System.Numerics;
using Silk.NET.Assimp;

namespace Super.Cook.Models;

/// <summary>
/// Every node of a scene, parent first, with its global rest transform and, once the joints are chosen, its joint, so
/// a mesh that hangs off a node without a skin can be placed and attached. Also which node holds each mesh.
/// </summary>
internal sealed unsafe class SceneNodes
{
    private readonly List<(string Name, int Parent, Matrix4x4 Global, int Joint)> _nodes = [];
    private readonly Dictionary<int, int> _meshNode = [];

    public SceneNodes(Scene* scene)
    {
        Walk(scene->MRootNode, -1, Matrix4x4.Identity);
    }

    public int Count => _nodes.Count;

    public (string Name, int Parent, Matrix4x4 Global, int Joint) this[int index]
    {
        get => _nodes[index];
        set => _nodes[index] = value;
    }

    /// <summary>Whether any mesh of the scene has bones.</summary>
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

    /// <summary>
    /// Which nodes are joints. A node is a joint when it is a bone or has one below it. The parent of a node that
    /// carries an unskinned mesh is a joint too, so the mesh follows that container's animated chain.
    /// </summary>
    public bool[] Joints(Scene* scene)
    {
        var boneNames = BoneNames(scene);
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

        return isJoint;
    }

    /// <summary>The node that holds a mesh, or the root when none does.</summary>
    public int OfMesh(int mesh) => _meshNode.GetValueOrDefault(mesh, 0);

    /// <summary>The joint of the node, or of the nearest node above it that is one, or 0 when none is.</summary>
    public int NearestJoint(int node)
    {
        while (node >= 0 && _nodes[node].Joint < 0)
        {
            node = _nodes[node].Parent;
        }

        return node >= 0 ? _nodes[node].Joint : 0;
    }

    private static HashSet<string> BoneNames(Scene* scene)
    {
        var boneNames = new HashSet<string>(StringComparer.Ordinal);
        for (uint m = 0; m < scene->MNumMeshes; m++)
        {
            var mesh = scene->MMeshes[m];
            for (uint b = 0; b < mesh->MNumBones; b++)
            {
                boneNames.Add(mesh->MBones[b]->MName.AsString);
            }
        }

        return boneNames;
    }

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
}
