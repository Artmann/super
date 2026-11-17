using Silk.NET.Assimp;

namespace Super.Cook.Models;

/// <summary>Which joints move each vertex of a skinned mesh, and by how much.</summary>
internal static unsafe class SkinInfluences
{
    /// <summary>
    /// Each vertex's joints and weights, or null for a vertex no joint moves, its joints found by bone name in
    /// <paramref name="jointOf"/>. Assimp gives every joint of the skin that a mesh doesn't use a zero weight on its
    /// first vertex, and a file can hold zero weights of its own; neither moves the vertex, so they're left out.
    /// </summary>
    public static List<(int Joint, float Weight)>?[] Of(Mesh* mesh, int vertexCount, IReadOnlyDictionary<string, int> jointOf)
    {
        var influences = new List<(int Joint, float Weight)>?[vertexCount];
        for (uint b = 0; b < mesh->MNumBones; b++)
        {
            var bone = mesh->MBones[b];
            int joint = jointOf[bone->MName.AsString];
            for (uint w = 0; w < bone->MNumWeights; w++)
            {
                var weight = bone->MWeights[w];
                if (weight.MWeight > 0f)
                {
                    (influences[weight.MVertexId] ??= []).Add((joint, weight.MWeight));
                }
            }
        }

        return influences;
    }
}
