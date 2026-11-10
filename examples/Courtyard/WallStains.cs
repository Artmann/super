using System.Numerics;

namespace Courtyard;

/// <summary>Which grime a <see cref="WallStains"/> box is laid with.</summary>
public enum Stain
{
    /// <summary>Rising damp along the foot of the wall.</summary>
    Damp,

    /// <summary>Dirty water run off the window sill.</summary>
    Drip,

    /// <summary>Rust washed down from an anchor plate.</summary>
    Rust,
}

/// <summary>Where the Courtyard projects its grime onto the brick wall, as decal boxes.</summary>
public static class WallStains
{
    // From art/courtyard.py: the wall's face, its pier and the window sill, in Blender's metres.
    private const float WallX0 = -2.8f;
    private const float WallX1 = 11.0f;
    private const float PierX0 = 6.3f;
    private const float PierX1 = 7.0f;
    private const float PierDepth = 0.14f;
    private const float SillZ = 1.15f;
    private const float WindowX = 2.4f;
    private const float WindowWidth = 2.0f;

    /// <summary>Every stain's box and the grime it is laid with.</summary>
    public static List<(Matrix4x4 Box, Stain Kind)> Boxes()
    {
        var stains = new List<(Matrix4x4 Box, Stain Kind)>();

        // Rising damp along the foot of the wall, solid just under the top of the plinth and faded
        // out below the sill. The grime repeats every metre along the wall, so the band is laid as
        // boxes about a metre wide. The pier stands out of the wall and gets its own.
        float bottom = 0.42f;
        float top = SillZ - 0.05f;
        Band(stains, WallX0, PierX0, bottom, top, 0f);
        Band(stains, PierX1, WallX1, bottom, top, 0f);
        Band(stains, PierX0, PierX1, bottom, top - 0.1f, PierDepth);

        // Dirty water run off the sill, a little wider than the window. A streak fades out at both its
        // sides as well, across the width of its box.
        float half = WindowWidth / 2f + 0.12f;
        stains.Add((StainBox(WindowX - half, WindowX + half, SillZ - 0.2f, SillZ - 0.95f, 0f, 0.12f), Stain.Drip));

        // Rust washed down from each anchor plate, in a shallow box, so it stains the brick round
        // the plate and only the plate's rim.
        foreach (float x in new[] { -1.2f, 10.1f })
        {
            stains.Add((StainBox(x - 0.16f, x + 0.16f, 5.38f, 4.3f, 0f, 0.05f), Stain.Rust));
        }

        return stains;
    }

    // A band of damp from x0 to x1 that is solid at zSolid and has faded by zFaded, on the wall
    // or on a face standing `outOfWall` in front of it, in boxes about a metre wide.
    private static void Band(List<(Matrix4x4 Box, Stain Kind)> stains, float x0, float x1, float zSolid, float zFaded, float outOfWall)
    {
        int count = Math.Max(1, (int)MathF.Round(x1 - x0));
        float width = (x1 - x0) / count;
        for (int i = 0; i < count; i++)
        {
            float left = x0 + i * width;
            stains.Add((StainBox(left, left + width, zSolid, zFaded, outOfWall, 0.12f), Stain.Damp));
        }
    }

    // A decal box on the wall, from Blender's x and z, projecting into the wall. Its depth runs
    // `depth` either side of a point 2 cm out, where the faces of the bricks are. The grime is
    // solid at the image's V of 0 and fades towards 1, and the box's local +Z is where V is 0,
    // so +Z points to zSolid.
    private static Matrix4x4 StainBox(float x0, float x1, float zSolid, float zFaded, float outOfWall, float depth)
    {
        float width = x1 - x0;
        float height = MathF.Abs(zFaded - zSolid);
        // Blender's (x, y, z) is (x, z, -y) here, so the wall faces +z and its face is at -WallY.
        var centre = new Vector3((x0 + x1) / 2f, (zSolid + zFaded) / 2f, -GravelSpills.WallY + outOfWall + 0.02f);
        // The box's +Y looks out of the wall. Its +Z points to the solid edge, and its +X is turned
        // with it so the box isn't mirrored.
        bool fadesUp = zFaded > zSolid;
        var across = fadesUp ? new Vector3(width, 0f, 0f) : new Vector3(-width, 0f, 0f);
        var solid = fadesUp ? new Vector3(0f, -height, 0f) : new Vector3(0f, height, 0f);
        var outward = new Vector3(0f, 0f, 2f * depth);
        return new Matrix4x4(
            across.X, across.Y, across.Z, 0f,
            outward.X, outward.Y, outward.Z, 0f,
            solid.X, solid.Y, solid.Z, 0f,
            centre.X, centre.Y, centre.Z, 1f);
    }
}
