using System.Numerics;

namespace Rendition.Gpu.Tests;

// A square view of a plain physical sky lit by one sun, and how it is drawn and read back.
internal static class TestSky
{
    // Looking along look from the origin through a 60 degree view, with the sun towards sun at a radiance of 5.
    public static FrameView View(int size, Vector3 look, Vector3 sun) => new()
    {
        Width = size,
        Height = size,
        View = Matrix4x4.CreateLookAt(Vector3.Zero, look, Vector3.UnitY),
        Projection = Projection.PerspectiveInfiniteReversedZ(MathF.PI / 3f, 1f, 0.1f),
        CameraPosition = Vector3.Zero,
        Environment = new EnvironmentSettings { Intensity = 1f, ShowAsBackground = true, Sky = new AnalyticSky { Turbidity = 2.5f } },
        Lights = new[] { new DirectionalLight { Direction = sun, Radiance = new Vector3(5f) } },
    };

    // Looking down -Z with every effect off, for a test of something drawn over the picture whose colour nothing
    // else should move.
    public static FrameView Bare(int size) =>
        View(size, -Vector3.UnitZ, Vector3.Normalize(new Vector3(0.3f, 0.5f, 0.8f))) with { Off = RenderEffects.All };

    // Draws frames frames first, then the one read back, and gives how many pixels across it is. The window may hold
    // more pixels than asked for, such as on a screen scaled up.
    public static byte[] Capture(GpuFixture gpu, in FrameView view, DrawItem[] draws, int frames, out int wide)
    {
        for (int frame = 0; frame < frames; frame++)
        {
            gpu.Renderer.Render(view, draws);
        }

        var pixels = gpu.Renderer.Capture(view, draws, out int width, out int height);
        Assert.Equal(width, height);
        wide = width;
        return pixels;
    }
}
