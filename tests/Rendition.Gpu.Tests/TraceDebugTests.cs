using System.Numerics;

namespace Rendition.Gpu.Tests;

[Collection(GpuCollection.Name)]
[Trait("Speed", "Slow")]
[Trait("Needs", "GPU")]
public sealed class TraceDebugTests(GpuFixture gpu)
{
    private const int Size = 256;

    // The miss colour (0.25, 0.35, 0.55), worked through the sRGB curve by hand.
    private static readonly (int R, int G, int B) Sky = (137, 160, 196);

    [Fact]
    public void Shows_the_albedo_of_what_the_camera_sees()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var (vertices, indices) = TestMeshes.Quad();
        var mesh = gpu.Renderer.CreateMesh(vertices, indices);
        var red = gpu.Renderer.CreateMaterial(new Material { BaseColor = new Vector4(1, 0, 0, 1), Roughness = 0.8f });

        // The camera sits 3 metres back, so the unit quad covers the middle 40% of the view and the corners see sky.
        byte[] pixels = Capture([Draw(mesh, red, Matrix4x4.Identity)]);

        AssertPixel(pixels, Size / 2, Size / 2, (255, 0, 0));
        AssertPixel(pixels, 2, 2, Sky);
        gpu.Renderer.DestroyMaterial(red);
        gpu.Renderer.DestroyMesh(mesh);
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Sees_through_the_holes_a_texture_cuts()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var (vertices, indices) = TestMeshes.Quad();
        var mesh = gpu.Renderer.CreateMesh(vertices, indices);
        var red = gpu.Renderer.CreateMaterial(new Material { BaseColor = new Vector4(1, 0, 0, 1), Roughness = 0.8f });

        // White everywhere, and see-through on the left half: u below 0.5, which is x below 0 on the quad.
        const int side = 64;
        byte[] rgba = new byte[side * side * 4];
        for (int y = 0; y < side; y++)
        {
            for (int x = 0; x < side; x++)
            {
                int at = (y * side + x) * 4;
                rgba[at] = rgba[at + 1] = rgba[at + 2] = 255;
                rgba[at + 3] = x < side / 2 ? (byte)0 : (byte)255;
            }
        }

        var halfClear = gpu.Renderer.CreateTexture(TextureData.FromPixels(side, side, TextureKind.Color, rgba));
        var green = gpu.Renderer.CreateMaterial(new Material
        {
            BaseColor = new Vector4(0, 1, 0, 1),
            BaseColorTexture = halfClear,
            AlphaCutoff = 0.5f,
            Roughness = 0.8f,
        });

        // The green cut-out quad in front, and a bigger red one a metre behind it.
        byte[] pixels = Capture(
        [
            Draw(mesh, green, Matrix4x4.Identity),
            Draw(mesh, red, Matrix4x4.CreateScale(2f) * Matrix4x4.CreateTranslation(0, 0, -1)),
        ]);

        AssertPixel(pixels, Size / 2 - 25, Size / 2, (255, 0, 0));
        AssertPixel(pixels, Size / 2 + 25, Size / 2, (0, 255, 0));
        gpu.Renderer.DestroyMaterial(green);
        gpu.Renderer.DestroyMaterial(red);
        gpu.Renderer.DestroyTexture(halfClear);
        gpu.Renderer.DestroyMesh(mesh);
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Finds_the_part_and_triangle_a_ray_hits_in_a_mesh_of_many_parts()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;

        // Two side-by-side quads in one mesh. The left one is the first part (indices 0-5) and the right one the
        // second (indices 6-11, so FirstIndex 6). Their vertices run in the same order, and the right quad's u is the
        // left quad's plus 0.5, so a hit on the right that reads the left quad's indices lands on the red half.
        var tangent = new Vector4(1, 0, 0, 1);
        Vertex[] vertices =
        [
            new(new Vector3(-1f, -0.5f, 0), Vector3.UnitZ, tangent, new Vector2(0f, 0)),
            new(new Vector3(0f, -0.5f, 0), Vector3.UnitZ, tangent, new Vector2(0.5f, 0)),
            new(new Vector3(0f, 0.5f, 0), Vector3.UnitZ, tangent, new Vector2(0.5f, 1)),
            new(new Vector3(-1f, 0.5f, 0), Vector3.UnitZ, tangent, new Vector2(0f, 1)),
            new(new Vector3(0f, -0.5f, 0), Vector3.UnitZ, tangent, new Vector2(0.5f, 0)),
            new(new Vector3(1f, -0.5f, 0), Vector3.UnitZ, tangent, new Vector2(1f, 0)),
            new(new Vector3(1f, 0.5f, 0), Vector3.UnitZ, tangent, new Vector2(1f, 1)),
            new(new Vector3(0f, 0.5f, 0), Vector3.UnitZ, tangent, new Vector2(0.5f, 1)),
        ];
        uint[] indices = [0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7];
        var mesh = gpu.Renderer.CreateMesh(vertices, indices);

        // Red where u is below 0.5 and green above.
        const int side = 64;
        byte[] rgba = new byte[side * side * 4];
        for (int y = 0; y < side; y++)
        {
            for (int x = 0; x < side; x++)
            {
                int at = (y * side + x) * 4;
                rgba[at] = x < side / 2 ? (byte)255 : (byte)0;
                rgba[at + 1] = x < side / 2 ? (byte)0 : (byte)255;
                rgba[at + 3] = 255;
            }
        }

        var redGreen = gpu.Renderer.CreateTexture(TextureData.FromPixels(side, side, TextureKind.Color, rgba));
        var blue = gpu.Renderer.CreateMaterial(new Material { BaseColor = new Vector4(0, 0, 1, 1), Roughness = 0.8f });
        var textured = gpu.Renderer.CreateMaterial(new Material
        {
            BaseColor = Vector4.One,
            BaseColorTexture = redGreen,
            Roughness = 0.8f,
        });

        // One mesh at one place, so both draws become one instance with two parts.
        byte[] pixels = Capture(
        [
            new DrawItem { Mesh = mesh, FirstIndex = 0, IndexCount = 6, Material = blue, Transform = Matrix4x4.Identity },
            new DrawItem { Mesh = mesh, FirstIndex = 6, IndexCount = 6, Material = textured, Transform = Matrix4x4.Identity },
        ]);

        // A unit is about 103 pixels here, so these sit in the middle of each quad.
        AssertPixel(pixels, Size / 2 - 50, Size / 2, (0, 0, 255));
        AssertPixel(pixels, Size / 2 + 50, Size / 2, (0, 255, 0));
        gpu.Renderer.DestroyMaterial(textured);
        gpu.Renderer.DestroyMaterial(blue);
        gpu.Renderer.DestroyTexture(redGreen);
        gpu.Renderer.DestroyMesh(mesh);
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Asking_for_it_without_ray_tracing_says_why_it_cannot()
    {
        using var fresh = new GpuFixture(disableRayTracing: true);

        var thrown = Assert.Throws<InvalidOperationException>(() => fresh.Renderer.Render(View(), []));

        Assert.Equal(
            "The trace debug view needs a GPU that can trace rays, and this one can't (turned off with --no-trace).",
            thrown.Message);
        Assert.Equal(0, fresh.ValidationErrors);
    }

    private byte[] Capture(DrawItem[] draws)
    {
        byte[] pixels = gpu.Renderer.Capture(View(), draws, out int width, out int height);
        Assert.Equal((Size, Size), (width, height));
        return pixels;
    }

    private static FrameView View() => new()
    {
        Width = Size,
        Height = Size,
        View = Matrix4x4.CreateLookAt(new Vector3(0, 0, 3), Vector3.Zero, Vector3.UnitY),
        Projection = Projection.PerspectiveInfiniteReversedZ(MathF.PI / 4f, 1f, 0.1f),
        CameraPosition = new Vector3(0, 0, 3),
        DebugView = DebugView.Trace,
    };

    private static DrawItem Draw(MeshHandle mesh, MaterialHandle material, Matrix4x4 transform) =>
        new() { Mesh = mesh, IndexCount = 6, Material = material, Transform = transform };

    private static void AssertPixel(byte[] pixels, int x, int y, (int R, int G, int B) expected)
    {
        int at = (y * Size + x) * 4;
        var actual = (R: (int)pixels[at], G: (int)pixels[at + 1], B: (int)pixels[at + 2]);
        bool close = Math.Abs(actual.R - expected.R) <= 2
            && Math.Abs(actual.G - expected.G) <= 2
            && Math.Abs(actual.B - expected.B) <= 2;
        Assert.True(close, $"Pixel ({x}, {y}) is {actual}, expected {expected} within 2.");
    }
}
