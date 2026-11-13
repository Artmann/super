using System.Globalization;
using System.Numerics;

namespace Rendition;

/// <summary>
/// Where bakes live in the content, and the rules a <see cref="BakedSpace"/> or <see cref="BakedChunk"/> must keep,
/// which the view and the renderer both check with these. See "Baked lighting" in docs/rendering.md.
/// </summary>
public static class BakeFiles
{
    /// <summary>The most baked chunks one frame can place.</summary>
    public const int MaxChunks = 256;

    /// <summary>The most baked probes a space's box can hold.</summary>
    public const int MaxSpaceProbes = 4096;

    /// <summary>How far a chunk transform's rows may be from unit length, or from square to each other.</summary>
    private const float RigidTolerance = 1e-4f;

    /// <summary>The bake of the space named <paramref name="name"/>: <c>lighting/&lt;name&gt;.lightbake</c>.</summary>
    public static string Space(string name)
    {
        CheckName(name);
        return $"lighting/{name}.lightbake";
    }

    /// <summary>
    /// The bake of one version of the chunk named <paramref name="name"/>, the one whose contents hash to
    /// <paramref name="inputHash"/>: <c>lighting/&lt;name&gt;/&lt;16 hex digits&gt;.lightbake</c>.
    /// </summary>
    public static string Chunk(string name, ulong inputHash)
    {
        CheckName(name);
        return string.Create(CultureInfo.InvariantCulture, $"lighting/{name}/{inputHash:x16}.lightbake");
    }

    /// <summary>Throws unless the name is one or more ASCII letters, digits, '-' or '_', since it names a file.</summary>
    public static void CheckName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        bool valid = name.Length > 0;
        foreach (char c in name)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_'))
            {
                valid = false;
                break;
            }
        }

        if (!valid)
        {
            throw new ArgumentException(
                $"The bake name \"{name}\" must be one or more letters, digits, '-' or '_', since it names a file.",
                nameof(name));
        }
    }

    /// <summary>
    /// Whether the transform only turns and moves: its upper 3x3 rows are unit length and square to each other,
    /// within 1e-4, without a mirror, and its last column is (0, 0, 0, 1).
    /// </summary>
    public static bool IsRigid(in Matrix4x4 transform)
    {
        if (!(transform.M14 == 0f && transform.M24 == 0f && transform.M34 == 0f && transform.M44 == 1f))
        {
            return false;
        }

        if (!(float.IsFinite(transform.M41) && float.IsFinite(transform.M42) && float.IsFinite(transform.M43)))
        {
            return false;
        }

        var x = new Vector3(transform.M11, transform.M12, transform.M13);
        var y = new Vector3(transform.M21, transform.M22, transform.M23);
        var z = new Vector3(transform.M31, transform.M32, transform.M33);
        bool unit = MathF.Abs(x.Length() - 1f) <= RigidTolerance
            && MathF.Abs(y.Length() - 1f) <= RigidTolerance
            && MathF.Abs(z.Length() - 1f) <= RigidTolerance;
        bool square = MathF.Abs(Vector3.Dot(x, y)) <= RigidTolerance
            && MathF.Abs(Vector3.Dot(y, z)) <= RigidTolerance
            && MathF.Abs(Vector3.Dot(z, x)) <= RigidTolerance;
        return unit && square && Vector3.Dot(x, Vector3.Cross(y, z)) > 0f;
    }

    /// <summary>Throws unless the chunk's transform <see cref="IsRigid"/>. <paramref name="index"/> is its place in the frame.</summary>
    public static void CheckChunkTransform(int index, string name, in Matrix4x4 transform, string? paramName)
    {
        if (!IsRigid(transform))
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture,
                $"Baked chunk {index} (\"{name}\") is scaled, sheared or mirrored. A chunk's transform may hold only rotation and translation, since one bake serves every place the chunk stands."),
                paramName);
        }
    }

    /// <summary>
    /// Throws unless the chunk's <see cref="BakedChunk.EdgeBlend"/> is 0 or more and at most half its box's smaller side
    /// across x and z, since the band runs in from all four sides and two that crossed would leave no bake at the middle.
    /// </summary>
    public static void CheckEdgeBlend(string name, float edgeBlend, Vector3 min, Vector3 max, string? paramName)
    {
        if (!(edgeBlend >= 0f))
        {
            throw new ArgumentOutOfRangeException(paramName, edgeBlend, string.Create(CultureInfo.InvariantCulture,
                $"The baked chunk \"{name}\"'s edge blend must be 0 metres or more, not {edgeBlend}."));
        }

        float smaller = MathF.Min(max.X - min.X, max.Z - min.Z);
        if (edgeBlend > smaller * 0.5f)
        {
            string message = string.Create(CultureInfo.InvariantCulture,
                $"The baked chunk \"{name}\"'s edge blend of {edgeBlend} metres is more than half its box's smaller side across x and z, {smaller} metres.");
            if (edgeBlend == BakedChunk.DefaultEdgeBlend)
            {
                // The likeliest way here is a small chunk that never asked for a band, so say where the value came from.
                message += string.Create(CultureInfo.InvariantCulture,
                    $" {edgeBlend} metres is the default; pass edgeBlend: 0 for a hard edge, or a smaller edge blend.");
            }

            throw new ArgumentOutOfRangeException(paramName, edgeBlend, message);
        }
    }

    /// <summary>
    /// Throws for more than <see cref="MaxChunks"/> chunks, or for one whose transform isn't rigid or whose edge blend
    /// doesn't fit its box.
    /// </summary>
    internal static void CheckChunks(ReadOnlySpan<BakedChunk> chunks)
    {
        if (chunks.Length > MaxChunks)
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture,
                $"The frame places {chunks.Length} baked chunks, but at most {MaxChunks} fit."),
                nameof(FrameView.BakedChunks));
        }

        for (int i = 0; i < chunks.Length; i++)
        {
            CheckChunkTransform(i, chunks[i].Name, chunks[i].Transform, nameof(FrameView.BakedChunks));
            CheckEdgeBlend(chunks[i].Name, chunks[i].EdgeBlend, chunks[i].Min, chunks[i].Max, nameof(FrameView.BakedChunks));
        }
    }
}
