using System.Numerics;

namespace Rendition.Gpu.Tests;

/// <summary>
/// The baker's texel setup: drawing static draws into their lightmap rectangles for the position and normals under each
/// texel, and marking texels inside walls invalid. Each test runs twice: on the path the device takes, and with
/// multisampling forced, so the fallback for devices without conservative rasterization runs here too.
/// </summary>
[Collection(GpuCollection.Name)]
[Trait("Speed", "Slow")]
[Trait("Needs", "GPU")]
public sealed class BakeTexelGpuTests(GpuFixture gpu)
{
    private const int TexelsPerMetre = 20;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_quad_covers_its_rectangle_with_its_positions(bool forceMultisample)
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var quad = CreateMesh(LightmappedQuad());
        var draws = new[] { StaticDraw(quad, 6, Matrix4x4.CreateTranslation(1, 2, 3), 1) };

        using var baker = new Baker(gpu.Context, gpu.Renderer) { ForceMultisample = forceMultisample };
        var rect = SetUp(baker, draws);
        AssertPath(baker, forceMultisample);

        // A square metre at 20 texels a metre is 20 texels a side.
        var (cornerX, cornerY, side) = Texels(rect);
        Assert.Equal(TexelsPerMetre, side);

        // Texel (10, 10) of the rectangle has its centre at uv 10.5 / 20 = 0.525 on both axes. The quad's uv 0 is its
        // corner at (-0.5, -0.5), so that is (0.025, 0.025, 0) on the quad, moved by (1, 2, 3).
        var centre = Read(baker, rect.Page, cornerX + 10, cornerY + 10);
        Assert.True(centre.Covered, "The rectangle's middle texel isn't covered.");
        Assert.True(Vector3.Distance(new Vector3(1.025f, 2.025f, 3f), centre.Position) < 0.03f,
            $"The middle texel is at {centre.Position}, not near (1.025, 2.025, 3).");
        AssertNear(Vector3.UnitZ, centre.GeometricNormal, "geometric normal");
        AssertNear(Vector3.UnitZ, centre.ShadingNormal, "shading normal");
        Assert.True(centre.Valid, "A texel in open space was marked invalid.");

        // Every texel of the rectangle is covered. Conservative rasterization also takes the ring just outside it, since
        // the quad's edges lie on texel edges, but nothing two texels or more outside.
        int margin = 4;
        int x0 = Math.Max(cornerX - margin, 0);
        int y0 = Math.Max(cornerY - margin, 0);
        int x1 = Math.Min(cornerX + side + margin, LightmapLayout.PageSize);
        int y1 = Math.Min(cornerY + side + margin, LightmapLayout.PageSize);
        float[] region = baker.ReadTexels(rect.Page, x0, y0, x1 - x0, y1 - y0);
        int inside = 0;
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                bool covered = Texel.At(region, (y - y0) * (x1 - x0) + (x - x0)).Covered;
                int outside = Math.Max(Math.Max(cornerX - x, x - (cornerX + side - 1)), Math.Max(cornerY - y, y - (cornerY + side - 1)));
                if (outside <= 0)
                {
                    inside++;
                    Assert.True(covered, $"Texel ({x}, {y}) is inside the rectangle but not covered.");
                }
                else if (outside >= 2)
                {
                    Assert.False(covered, $"Texel ({x}, {y}) is {outside} texels outside the rectangle but covered.");
                }
            }
        }

        Assert.Equal(side * side, inside);
        gpu.Renderer.DestroyMesh(quad);
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_texel_inside_a_wall_is_invalid(bool forceMultisample)
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var quad = CreateMesh(LightmappedQuad());
        var (boxVertices, boxIndices) = TestMeshes.Cube();
        var box = CreateMesh((boxVertices, boxIndices));

        // A closed box from x 0 to 0.6, y -0.6 to 0.6 and z -0.01 to 0.01: the quad's right half lies inside it, 1 cm
        // under its top. The box isn't static, so it gets no rectangle, but rays still hit it.
        var boxTransform = Matrix4x4.CreateScale(0.6f, 1.2f, 0.02f) * Matrix4x4.CreateTranslation(0.3f, 0f, 0f);
        var draws = new[]
        {
            StaticDraw(quad, 6, Matrix4x4.Identity, 1),
            new DrawItem { Mesh = box, IndexCount = 36, Material = gpu.Matte, Transform = boxTransform },
        };

        using var baker = new Baker(gpu.Context, gpu.Renderer) { ForceMultisample = forceMultisample };
        var rect = SetUp(baker, draws);
        AssertPath(baker, forceMultisample);
        var (cornerX, cornerY, side) = Texels(rect);

        // From inside the box, a 5 cm ray hits its top from behind unless it leaves within 10 degrees of flat: the
        // top is 9 mm up, so it needs a rise of 0.18 or more, which is 82% of a hemisphere uniform in its cosine.
        // Outside the box nothing faces the texel from behind.
        float[] region = baker.ReadTexels(rect.Page, cornerX, cornerY, side, side);
        int inWall = 0, inOpen = 0;
        for (int i = 0; i < side * side; i++)
        {
            var texel = Texel.At(region, i);
            Assert.True(texel.Covered, $"Texel {i} of the rectangle isn't covered.");
            if (texel.Position.X > 0.05f)
            {
                inWall++;
                Assert.False(texel.Valid, $"The texel at {texel.Position} is inside the box but valid.");
            }
            else if (texel.Position.X < -0.05f)
            {
                inOpen++;
                Assert.True(texel.Valid, $"The texel at {texel.Position} is outside the box but invalid.");
            }
        }

        // Each half of the 20 texel rectangle has 9 columns more than 5 cm from the box's side.
        Assert.Equal(9 * side, inWall);
        Assert.Equal(9 * side, inOpen);
        gpu.Renderer.DestroyMesh(box);
        gpu.Renderer.DestroyMesh(quad);
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_thin_triangle_still_covers_a_texel(bool forceMultisample)
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;

        // A sliver so small its rectangle is the smallest, 8 texels a side. In texel (3, 3) of the rectangle it runs
        // from (0.02, 0.02) to (0.98, 0.02) to (0.98, 0.12) across the texel, missing its centre at (0.5, 0.5) but
        // covering the eighth of eight standard sample positions, (0.9375, 0.0625). Its position in metres is its uv.
        Vector2[] corners = [new(0.02f, 0.02f), new(0.98f, 0.02f), new(0.98f, 0.12f)];
        var vertices = new Vertex[3];
        for (int i = 0; i < 3; i++)
        {
            var uv = (new Vector2(3f) + corners[i]) / LightmapLayout.MinSide;
            vertices[i] = new Vertex(new Vector3(uv, 0f), Vector3.UnitZ, new Vector4(1, 0, 0, 1), uv) { Uv2 = uv };
        }

        var sliver = CreateMesh((vertices, [0, 1, 2]));
        var draws = new[] { StaticDraw(sliver, 3, Matrix4x4.Identity, 1) };

        using var baker = new Baker(gpu.Context, gpu.Renderer) { ForceMultisample = forceMultisample };
        var rect = SetUp(baker, draws);
        AssertPath(baker, forceMultisample);
        var (cornerX, cornerY, side) = Texels(rect);
        Assert.Equal(LightmapLayout.MinSide, side);

        var texel = Read(baker, rect.Page, cornerX + 3, cornerY + 3);
        Assert.True(texel.Covered, "The sliver's texel isn't covered.");
        AssertNear(Vector3.UnitZ, texel.ShadingNormal, "shading normal");
        AssertNear(Vector3.UnitZ, texel.GeometricNormal, "geometric normal");

        // Within a texel (12.5 cm here) of the sliver's middle, (3.66, 3.0533) / 8 metres.
        var middle = new Vector3(3.66f / 8f, 3.0533f / 8f, 0f);
        Assert.True(Vector3.Distance(middle, texel.Position) < 1f / LightmapLayout.MinSide,
            $"The sliver's texel is at {texel.Position}, not near {middle}.");

        gpu.Renderer.DestroyMesh(sliver);
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    /// <summary>The test quad, with its second UVs the same as its first so it fills its rectangle.</summary>
    private static (Vertex[] Vertices, uint[] Indices) LightmappedQuad()
    {
        var (vertices, indices) = TestMeshes.Quad();
        for (int i = 0; i < vertices.Length; i++)
        {
            vertices[i].Uv2 = vertices[i].Uv;
        }

        return (vertices, indices);
    }

    private MeshHandle CreateMesh((Vertex[] Vertices, uint[] Indices) mesh)
    {
        var handle = gpu.Renderer.CreateMesh(mesh.Vertices, mesh.Indices);
        gpu.RenderOneFrame(); // Uploads the mesh.
        return handle;
    }

    private DrawItem StaticDraw(MeshHandle mesh, uint indexCount, Matrix4x4 transform, ulong instance) => new()
    {
        Mesh = mesh,
        IndexCount = indexCount,
        Material = gpu.Matte,
        Transform = transform,
        BakeId = BakeIds.Of(instance, 0),
    };

    /// <summary>Lays out the static draws, sets up their texels and returns the first one's rectangle.</summary>
    private LightmapRect SetUp(Baker baker, DrawItem[] draws)
    {
        var layout = LightmapLayout.Build(draws, gpu.Renderer.AreaOf, new BakeSettings(TexelsPerMetre), _ => true);
        gpu.Context.SubmitAndWait(cmd => baker.SetUpTexels(cmd, layout, draws));
        Assert.True(layout.TryGet(draws[0].BakeId, out var rect), "The static draw has no rectangle.");
        return rect;
    }

    private void AssertPath(Baker baker, bool forceMultisample)
    {
        Assert.Equal(!forceMultisample && gpu.Context.ConservativeRasterization, baker.Conservative);    }

    private static (int X, int Y, int Side) Texels(LightmapRect rect) => (
        (int)MathF.Round(rect.Offset.X * LightmapLayout.PageSize),
        (int)MathF.Round(rect.Offset.Y * LightmapLayout.PageSize),
        (int)MathF.Round(rect.Scale.X * LightmapLayout.PageSize));

    private static Texel Read(Baker baker, int page, int x, int y) => Texel.At(baker.ReadTexels(page, x, y, 1, 1), 0);

    private static void AssertNear(Vector3 expected, Vector3 actual, string what)
    {
        Assert.True(Vector3.Distance(expected, actual) < 0.01f, $"The {what} is {actual}, not {expected}.");
    }

    private readonly record struct Texel(Vector3 Position, bool Covered, Vector3 GeometricNormal, Vector3 ShadingNormal, bool Valid)
    {
        public static Texel At(float[] texels, int index)
        {
            var t = texels.AsSpan(index * Baker.TexelFloats, Baker.TexelFloats);
            return new Texel(
                new Vector3(t[0], t[1], t[2]),
                t[3] > 0.5f,
                new Vector3(t[4], t[5], t[6]),
                new Vector3(t[7], t[8], t[9]),
                t[10] > 0.5f);
        }
    }
}
