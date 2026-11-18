using System.Numerics;
using Rendition;

namespace Super;

/// <summary>
/// An arc of a round shape's outline: its centre and radius, and the angles it runs between in
/// <see cref="Steps"/> equal steps. Angles start at the top and turn outwards.
/// </summary>
internal readonly record struct ProfileArc(Vector2 Centre, float Radius, float StartAngle, float EndAngle, int Steps);

/// <summary>The sums the shapes in <see cref="Shapes"/> share: exact turns, arcs, tiling and face tangents.</summary>
internal static class ShapeMath
{
    /// <summary>Sine and cosine, exact at quarter turns, so poles and seams meet bit for bit.</summary>
    public static (float Sin, float Cos) SinCos(float angle)
    {
        float quarters = angle / (MathF.PI * 0.5f);
        float nearest = MathF.Round(quarters);
        if (MathF.Abs(quarters - nearest) < 1e-6f)
        {
            int quarter = ((int)nearest % 4 + 4) % 4;
            return quarter switch
            {
                0 => (0f, 1f),
                1 => (1f, 0f),
                2 => (0f, -1f),
                _ => (-1f, 0f),
            };
        }

        return MathF.SinCos(angle);
    }

    /// <summary>
    /// Adds points round an arc to a profile, from step <paramref name="first"/> to step
    /// <paramref name="last"/> of the arc's steps, so normals face away from its centre.
    /// <paramref name="distance"/> is how far along the outline the first point lies.
    /// </summary>
    public static void AddArc(List<ProfilePoint> profile, ProfileArc arc, int first, int last, float distance = 0f)
    {
        for (int step = first; step <= last; step++)
        {
            var (sin, cos) = SinCos(arc.StartAngle + (arc.EndAngle - arc.StartAngle) * step / arc.Steps);
            var normal = new Vector2(sin, cos);
            float travelled = distance + arc.Radius * MathF.Abs(arc.EndAngle - arc.StartAngle) * (step - first) / arc.Steps;
            profile.Add(new ProfilePoint(arc.Centre.X + arc.Radius * sin, arc.Centre.Y + arc.Radius * cos, normal, travelled));
        }
    }

    /// <summary>How far UVs run across a piece this wide and tall: one tile per metre at the given tiling, or once across without one.</summary>
    public static Vector2 Tiling(float? uvPerMetre, float width, float height)
    {
        if (uvPerMetre is not { } perMetre)
        {
            return Vector2.One;
        }

        ShapeChecks.RequirePositive(perMetre, nameof(uvPerMetre), "UV scale");
        return new Vector2(width, height) * perMetre;
    }

    /// <summary>
    /// Which way a triangle's texture runs across, from how its UVs change over it. Bent
    /// to lie flat in the face, and with the handedness the shaders expect.
    /// </summary>
    public static Vector4 FaceTangent(in Vertex a, in Vertex b, in Vertex c, Vector3 normal)
    {
        var edge1 = b.Position - a.Position;
        var edge2 = c.Position - a.Position;
        var uv1 = b.Uv - a.Uv;
        var uv2 = c.Uv - a.Uv;
        float determinant = uv1.X * uv2.Y - uv2.X * uv1.Y;
        if (MathF.Abs(determinant) < 1e-12f)
        {
            // No usable UVs, so any direction in the face will do.
            return new Vector4(Vector3.Normalize(edge1), 1f);
        }

        var alongU = (edge1 * uv2.Y - edge2 * uv1.Y) / determinant;
        var alongV = (edge2 * uv1.X - edge1 * uv2.X) / determinant;
        var tangent = Vector3.Normalize(alongU - normal * Vector3.Dot(normal, alongU));

        // The shaders build the bitangent as cross(normal, tangent) * w, pointing to where v falls.
        float handedness = Vector3.Dot(Vector3.Cross(normal, tangent), alongV) < 0f ? 1f : -1f;
        return new Vector4(tangent, handedness);
    }
}

/// <summary>The checks <see cref="Shapes"/> makes of what it's given, with the messages a game sees.</summary>
internal static class ShapeChecks
{
    /// <summary>The tiling to use: the one given, else one tile per <see cref="Surface.TileWidth"/> metres, else none.</summary>
    public static float? PerMetre(float? uvPerMetre, Surface? surface)
    {
        if (uvPerMetre is { } given)
        {
            if (!(given > 0f) || !float.IsFinite(given))
            {
                throw new ArgumentOutOfRangeException(nameof(uvPerMetre), FormattableString.Invariant($"uvPerMetre must be a finite number above zero, not {given}."));
            }

            return given;
        }

        if (surface?.TileWidth is not { } width)
        {
            return null;
        }

        // A width so small that one over it overflows is as unusable as zero.
        if (!(width > 0f) || !float.IsFinite(width) || !float.IsFinite(1f / width))
        {
            throw new ArgumentOutOfRangeException(nameof(surface), FormattableString.Invariant($"The surface's TileWidth must be a finite number above zero, not {width}."));
        }

        return 1f / width;
    }

    public static void RequirePositive(float value, string field, string what)
    {
        if (!(value > 0f) || !float.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(field, value, $"The {what} must be above zero.");
        }
    }

    public static void RequireAtLeastZero(float value, string field)
    {
        if (!(value >= 0f) || !float.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(field, value, "The radius can't be below zero.");
        }
    }

    public static void RequireAtLeast(int value, int least, string field)
    {
        if (value < least)
        {
            throw new ArgumentOutOfRangeException(field, value, $"A shape needs at least {least} {field}.");
        }
    }
}
