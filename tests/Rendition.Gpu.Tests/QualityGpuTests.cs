using System.Numerics;

namespace Rendition.Gpu.Tests;

// The two quality settings that change what is drawn rather than which effect runs: how many of the sun's cascades
// draw shadows, and how soon models switch to their simpler versions. Each is checked by the triangles a frame draws.
[Collection(GpuCollection.Name)]
[Trait("Speed", "Slow")]
[Trait("Needs", "GPU")]
public sealed class QualityGpuTests(GpuFixture gpu)
{
    private const int Size = 256;

    [Fact]
    public void Fewer_shadow_cascades_draw_fewer_shadow_triangles()
    {
        int errors = gpu.ValidationErrors;
        var panel = Panel(new Vector3(0f, 0f, 0f));
        try
        {
            long all = ShadowTriangles(panel, maxCascades: 4);
            long one = ShadowTriangles(panel, maxCascades: 1);

            // The panel sits by the camera, inside every cascade, so each cascade draws it once more.
            Assert.True(one > 0, "One cascade still draws the panel's shadow.");
            Assert.True(all > one, $"Four cascades drew {all} shadow triangles and one drew {one}.");
        }
        finally
        {
            gpu.Renderer.MaxShadowCascades = 4;
            gpu.Renderer.DestroyMesh(panel.Mesh);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Lower_mesh_detail_draws_fewer_camera_triangles()
    {
        int errors = gpu.ValidationErrors;
        // Far enough that the full panel is more than it needs, near enough that a simpler one still shows.
        var panel = Panel(new Vector3(0f, 0f, -12f));
        try
        {
            long fine = CameraTriangles(panel, detail: 2f);
            long coarse = CameraTriangles(panel, detail: 0.5f);

            Assert.True(coarse > 0, "The panel is still drawn at the lowest detail.");
            Assert.True(fine > coarse, $"Detail 2 drew {fine} triangles and detail 0.5 drew {coarse}.");
        }
        finally
        {
            gpu.Renderer.MeshDetailScale = 1f;
            gpu.Renderer.DestroyMesh(panel.Mesh);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void A_cascade_cap_outside_one_to_four_is_refused(int cascades)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => gpu.Renderer.MaxShadowCascades = cascades);

        Assert.StartsWith("The sun can draw from 1 to 4 cascades.", error.Message);
        Assert.Equal(4, gpu.Renderer.MaxShadowCascades);
    }

    private long ShadowTriangles(DrawItem panel, int maxCascades)
    {
        gpu.Renderer.MaxShadowCascades = maxCascades;
        return Measure(panel, RenderEffects.All & ~RenderEffects.SunShadows).Shadows;
    }

    private long CameraTriangles(DrawItem panel, float detail)
    {
        gpu.Renderer.MeshDetailScale = detail;
        return Measure(panel, RenderEffects.All).Camera;
    }

    // Draws one frame of the panel under a sun that asks for four cascades, and returns the triangles it drew.
    private (long Camera, long Shadows) Measure(DrawItem panel, RenderEffects off)
    {
        DirectionalLight[] sun =
        [
            new() { Direction = Vector3.Normalize(new Vector3(0.4f, 1f, 0.6f)), Radiance = Vector3.One, CastsShadow = true },
        ];
        var view = new FrameView
        {
            Width = Size,
            Height = Size,
            View = Matrix4x4.CreateLookAt(new Vector3(0, 0, 3), Vector3.Zero, Vector3.UnitY),
            Projection = Projection.PerspectiveInfiniteReversedZ(MathF.PI / 4f, 1f, 0.1f),
            CameraPosition = new Vector3(0, 0, 3),
            Lights = sun,
            Shadows = new ShadowSettings { Center = Vector3.Zero, Radius = 4f, Cascades = 4 },
            // Culling and simpler models stay on: they're what these tests measure.
            Off = off & ~(RenderEffects.Culling | RenderEffects.SimplerModels),
        };

        gpu.Renderer.MeasureGpu = true;
        try
        {
            gpu.Renderer.Render(view, [panel]);
            return gpu.Renderer.DrawnTriangles;
        }
        finally
        {
            gpu.Renderer.MeasureGpu = false;
        }
    }

    // A two-metre square facing the camera, cut into 64 by 64 quads, so it has simpler versions to fall back to.
    private DrawItem Panel(Vector3 position)
    {
        const int quads = 64;
        var tangent = new Vector4(1f, 0f, 0f, 1f);
        var vertices = new List<Vertex>();
        for (int row = 0; row <= quads; row++)
        {
            for (int column = 0; column <= quads; column++)
            {
                var uv = new Vector2((float)column / quads, (float)row / quads);
                var corner = new Vector3(uv.X * 2f - 1f, uv.Y * 2f - 1f, 0f);
                vertices.Add(new Vertex(corner, Vector3.UnitZ, tangent, uv));
            }
        }

        var indices = new List<uint>();
        for (int row = 0; row < quads; row++)
        {
            for (int column = 0; column < quads; column++)
            {
                uint corner = (uint)(row * (quads + 1) + column);
                uint above = corner + quads + 1;
                indices.AddRange([corner, corner + 1, above + 1, corner, above + 1, above]);
            }
        }

        return gpu.Renderer.CreateDrawItem(vertices.ToArray(), indices.ToArray(), gpu.Matte) with
        {
            Transform = Matrix4x4.CreateTranslation(position),
        };
    }
}
