using System.Numerics;

namespace Rendition;

/// <summary>L1 spherical harmonics for one colour channel: coefficient 0 is the constant band, 1..3 are (y, z, x).</summary>
public readonly record struct ShL1(float C0, float C1, float C2, float C3)
{
    /// <summary>The constant band's basis value, 1 / (2 sqrt(pi)).</summary>
    public const float Y0 = 0.282094792f;

    /// <summary>The linear band's basis scale, sqrt(3) / (2 sqrt(pi)): the basis is Y1 times y, z and x.</summary>
    public const float Y1 = 0.488602512f;

    /// <summary>A light of the given radiance from one direction (a delta), projected.</summary>
    public static ShL1 Direction(Vector3 direction, float radiance)
    {
        return new ShL1(
            Y0 * radiance,
            Y1 * direction.Y * radiance,
            Y1 * direction.Z * radiance,
            Y1 * direction.X * radiance);
    }

    /// <summary>A sky of the same radiance in every direction, projected: only C0, 4 pi Y0 L.</summary>
    public static ShL1 Uniform(float radiance)
    {
        return new ShL1(4f * MathF.PI * Y0 * radiance, 0f, 0f, 0f);
    }

    /// <summary>The value in a direction.</summary>
    public float Evaluate(Vector3 direction)
    {
        return C0 * Y0 + Y1 * (C1 * direction.Y + C2 * direction.Z + C3 * direction.X);
    }

    /// <summary>
    /// The same function turned by a rotation: (C1, C2, C3) turn as the vector (y, z, x) does. System.Numerics
    /// uses row vectors, so a direction d moves to <c>Vector3.TransformNormal(d, rotation)</c>, and the result's
    /// value there is this one's value at d. Only the upper 3x3 is used, and it must pass
    /// <see cref="BakeFiles.IsRigid"/>, so a scale, shear or mirror throws <see cref="ArgumentException"/>.
    /// </summary>
    public ShL1 Rotate(in Matrix4x4 rotation)
    {
        if (!BakeFiles.IsRigid(rotation))
        {
            throw new ArgumentException(
                "The transform must only turn (and move): a scale, shear or mirror can't rotate spherical harmonics.",
                nameof(rotation));
        }

        var turned = Vector3.TransformNormal(new Vector3(C3, C1, C2), rotation);
        return new ShL1(C0, turned.Y, turned.Z, turned.X);
    }

    /// <summary>The sum over coefficients of a * b, which is the integral of the two functions' product.</summary>
    public static float Dot(in ShL1 a, in ShL1 b)
    {
        return a.C0 * b.C0 + a.C1 * b.C1 + a.C2 * b.C2 + a.C3 * b.C3;
    }
}
