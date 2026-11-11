using System.Numerics;

namespace Rendition.Gpu.Tests;

/// <summary>
/// A scene for the live light probe tests: its draws, its point and spot lights and the radiance of its sky. There is
/// no sun, and the environment's intensity is 0, so the sky reaches the probes only where they see nothing.
/// </summary>
internal sealed record ProbeScene(DrawItem[] Draws, LocalLight[] Lights, Vector3 Sky);

/// <summary>
/// Scenes made of flat quads from <see cref="TestMeshes.Quad"/>, for the live light probe tests. No surface lies on a
/// plane of probes: the finest cascade's probes are 0.5 m apart from the origin, so walls sit at 1.1 m or 1.55 m and
/// floors at -0.05 m, a little off every probe.
/// </summary>
internal static class ProbeScenes
{
    /// <summary>
    /// A white floor 8 m square just below y = 0, a wall 4 m long and 3 m high at x = 1.1 facing -x, in red or white,
    /// and one light at (0, 1.5, 0) with a range of 6 m: a point light, or a 30° spot aimed straight down at the
    /// origin. The sky is black.
    /// </summary>
    public static ProbeScene RedWall(GpuFixture gpu, bool lightIsSpot, bool wallIsWhite = false)
    {
        var quad = Quad(gpu);
        var white = gpu.Renderer.CreateMaterial(new Material { BaseColor = new Vector4(0.8f, 0.8f, 0.8f, 1f), Roughness = 1f });
        var wall = wallIsWhite
            ? white
            : gpu.Renderer.CreateMaterial(new Material { BaseColor = new Vector4(0.8f, 0.02f, 0.02f, 1f), Roughness = 1f });

        DrawItem[] draws =
        [
            Face(quad, white, new Vector3(0f, -0.05f, 0f), Vector3.UnitY, -Vector3.UnitZ, 8f, 8f),
            Face(quad, wall, new Vector3(1.1f, 1.5f, 0f), -Vector3.UnitX, Vector3.UnitY, 4f, 3f),
        ];

        var light = new LocalLight
        {
            Key = 1,
            Position = new Vector3(0f, 1.5f, 0f),
            Radiance = new Vector3(5f),
            Range = 6f,
        };
        if (lightIsSpot)
        {
            light.Direction = -Vector3.UnitY;
            light.InnerAngle = 25f * MathF.PI / 180f;
            light.OuterAngle = 30f * MathF.PI / 180f;
        }

        return new ProbeScene(draws, [light], Vector3.Zero);
    }

    /// <summary>A closed white box from -1.1 to 1.1 on every axis, its six faces facing in, under a sky of radiance 4.</summary>
    public static ProbeScene ClosedBox(GpuFixture gpu) => Box(gpu, facingIn: true);

    /// <summary>The same box with its faces facing out: a solid block, whose inside no probe should use.</summary>
    public static ProbeScene SolidBox(GpuFixture gpu) => Box(gpu, facingIn: false);

    /// <summary>
    /// A white room 3.1 m wide and high, from -1.55 to 1.55 across and -0.05 to 3.05 up, with its faces facing in.
    /// The wall at +z starts 1 m up, which leaves an opening below it. One point light at (0, 2, 0); the sky is black.
    /// </summary>
    public static ProbeScene WhiteRoom(GpuFixture gpu)
    {
        var quad = Quad(gpu);
        var white = gpu.Renderer.CreateMaterial(new Material { BaseColor = new Vector4(0.9f, 0.9f, 0.9f, 1f), Roughness = 1f });

        const float half = 1.55f;
        const float bottom = -0.05f;
        const float top = 3.05f;
        const float height = top - bottom;
        const float middle = (top + bottom) * 0.5f;
        const float side = 2f * half;

        DrawItem[] draws =
        [
            Face(quad, white, new Vector3(0f, bottom, 0f), Vector3.UnitY, -Vector3.UnitZ, side, side),
            Face(quad, white, new Vector3(0f, top, 0f), -Vector3.UnitY, Vector3.UnitZ, side, side),
            Face(quad, white, new Vector3(-half, middle, 0f), Vector3.UnitX, Vector3.UnitY, side, height),
            Face(quad, white, new Vector3(half, middle, 0f), -Vector3.UnitX, Vector3.UnitY, side, height),
            Face(quad, white, new Vector3(0f, middle, -half), Vector3.UnitZ, Vector3.UnitY, side, height),
            // The +z wall runs from 1 m up to the ceiling, so light leaves through the gap under it.
            Face(quad, white, new Vector3(0f, (1f + top) * 0.5f, half), -Vector3.UnitZ, Vector3.UnitY, side, top - 1f),
        ];

        var light = new LocalLight
        {
            Key = 1,
            Position = new Vector3(0f, 2f, 0f),
            Radiance = new Vector3(5f),
            Range = 8f,
        };

        return new ProbeScene(draws, [light], Vector3.Zero);
    }

    private static ProbeScene Box(GpuFixture gpu, bool facingIn)
    {
        var quad = Quad(gpu);
        var white = gpu.Renderer.CreateMaterial(new Material { BaseColor = new Vector4(0.8f, 0.8f, 0.8f, 1f), Roughness = 1f });

        const float half = 1.1f;
        const float size = 2f * half;
        float sign = facingIn ? -1f : 1f;

        DrawItem[] draws =
        [
            Face(quad, white, new Vector3(half, 0f, 0f), sign * Vector3.UnitX, Vector3.UnitY, size, size),
            Face(quad, white, new Vector3(-half, 0f, 0f), -sign * Vector3.UnitX, Vector3.UnitY, size, size),
            Face(quad, white, new Vector3(0f, half, 0f), sign * Vector3.UnitY, Vector3.UnitZ, size, size),
            Face(quad, white, new Vector3(0f, -half, 0f), -sign * Vector3.UnitY, Vector3.UnitZ, size, size),
            Face(quad, white, new Vector3(0f, 0f, half), sign * Vector3.UnitZ, Vector3.UnitY, size, size),
            Face(quad, white, new Vector3(0f, 0f, -half), -sign * Vector3.UnitZ, Vector3.UnitY, size, size),
        ];

        return new ProbeScene(draws, [], new Vector3(4f));
    }

    private static MeshHandle Quad(GpuFixture gpu)
    {
        var (vertices, indices) = TestMeshes.Quad();
        return gpu.Renderer.CreateMesh(vertices, indices);
    }

    // The unit quad scaled to width by height, turned so its front faces along normal with its top towards up, and
    // centred on center.
    private static DrawItem Face(
        MeshHandle quad, MaterialHandle material, Vector3 center, Vector3 normal, Vector3 up, float width, float height)
    {
        var right = Vector3.Cross(up, normal);
        var transform = new Matrix4x4(
            right.X * width, right.Y * width, right.Z * width, 0f,
            up.X * height, up.Y * height, up.Z * height, 0f,
            normal.X, normal.Y, normal.Z, 0f,
            center.X, center.Y, center.Z, 1f);

        return new DrawItem
        {
            Mesh = quad,
            FirstIndex = 0,
            IndexCount = 6,
            Material = material,
            Transform = transform,
        };
    }
}
