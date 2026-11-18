using System.Numerics;
using System.Runtime.InteropServices;

namespace Rendition;

[StructLayout(LayoutKind.Sequential)]
public struct Vertex(Vector3 position, Vector3 normal, Vector4 tangent, Vector2 uv)
{
    public Vector3 Position = position;
    public Vector3 Normal = normal;

    /// <summary>xyz is the tangent; w is the handedness of the bitangent.</summary>
    public Vector4 Tangent = tangent;

    public Vector2 Uv = uv;

    /// <summary>Four joint indices, one per byte, for a skinned model. Zero for a model with no skin.</summary>
    public uint Joints;

    /// <summary>How much each joint moves this vertex, as four bytes from 0 to 255 that add up to 255. Zero when there is no skin.</summary>
    public uint Weights;

    /// <summary>
    /// A second set of UVs, in which no two triangles share texels, for textures baked from the mesh's own shape such
    /// as <see cref="MaterialWeathering.Mask"/> and lightmaps. Every cooked model has one, and so does every built-in shape,
    /// ribbon, tube, extrusion and terrain chunk. A bent mesh keeps the set of the mesh it bent, and any other mesh built
    /// in code has zeros unless it sets them or calls <c>WithLightmapUvs</c>.
    /// </summary>
    public Vector2 Uv2;

    /// <summary>The joints this vertex follows, most important first, and how much it follows each.</summary>
    public void SetSkin(ReadOnlySpan<(int Joint, float Weight)> influences)
    {
        Joints = 0;
        Weights = 0;
        float total = 0f;
        foreach (var (_, weight) in influences)
        {
            total += weight;
        }

        if (total <= 0f)
        {
            return;
        }

        // Rounding has to leave the bytes adding up to 255, or the vertex shrinks or grows.
        int remaining = 255;
        for (int i = 0; i < influences.Length && i < 4; i++)
        {
            var (joint, weight) = influences[i];
            if (joint is < 0 or > 255)
            {
                throw new ArgumentOutOfRangeException(nameof(influences), joint, "A joint index must be 0 to 255.");
            }

            int share = i == influences.Length - 1 || i == 3 ? remaining : Math.Min(remaining, (int)MathF.Round(weight / total * 255f));
            remaining -= share;
            Joints |= (uint)joint << (8 * i);
            Weights |= (uint)share << (8 * i);
        }
    }
}
