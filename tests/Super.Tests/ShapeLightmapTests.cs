using System.Numerics;
using Rendition;

namespace Super.Tests;

public class ShapeLightmapTests
{
    public static TheoryData<string> AllShapes =>
    [
        "box", "sphere", "hemisphere", "capsule", "cylinder", "tapered", "cone", "plane", "long plane", "quad", "torus",
        "prism", "hex", "flat sphere", "flat box", "ribbon", "embankment", "tube", "ring tube",
    ];

    private static GeneratedMesh ShapeByName(string name) => name switch
    {
        "box" => Shapes.Box(new Vector3(2f, 3f, 0.7f)),
        "sphere" => Shapes.Sphere(),
        "hemisphere" => Shapes.Hemisphere(),
        "capsule" => Shapes.Capsule(),
        "cylinder" => Shapes.Cylinder(),
        "tapered" => Shapes.Cylinder(0.1f, 2f, 0.5f),
        "cone" => Shapes.Cone(),
        "plane" => Shapes.Plane(new Vector2(4f, 2f), subdivisions: 8),
        "long plane" => Shapes.Plane(new Vector2(1000f, 1f)),
        "quad" => Shapes.Quad(new Vector2(2f, 1f)),
        "torus" => Shapes.Torus(),
        "prism" => Shapes.Prism(5),
        "hex" => Shapes.Hex(),
        "flat sphere" => Shapes.Flat(Shapes.Sphere(1f, segments: 8, rings: 4)),
        "flat box" => Shapes.Flat(Shapes.Box(Vector3.One)),
        "ribbon" => Shapes.Ribbon(Curve(), 2f, step: 0.25f),
        "embankment" => Shapes.Extrude(Curve(), [new Vector2(1.5f, -0.5f), new Vector2(1f, 0f), new Vector2(-1f, 0f), new Vector2(-1.5f, -0.5f)], step: 0.25f),
        "tube" => Shapes.Tube(Curve(), 0.2f, sides: 6, step: 0.25f),
        "ring tube" => Shapes.Tube(Spline.Through([new Vector3(5f, 0f, 0f), new Vector3(0f, 0f, 5f), new Vector3(-5f, 0f, 0f), new Vector3(0f, 0f, -5f)], closed: true), 0.3f),
        _ => throw new ArgumentException(name),
    };

    /// <summary>A curve that turns left, climbs and turns back.</summary>
    private static Spline Curve() => Spline.Through([Vector3.Zero, new Vector3(0f, 0f, 4f), new Vector3(3f, 1f, 7f), new Vector3(7f, 1.5f, 8f)]);

    private static string? Problem(GeneratedMesh mesh) =>
        LightmapUnwrap.Problem(mesh.Vertices.Select(v => v.Position).ToArray(), mesh.Vertices.Select(v => v.Uv2).ToArray(), mesh.Indices);

    [Theory]
    [MemberData(nameof(AllShapes))]
    public void Has_a_valid_lightmap_set(string name)
    {
        Assert.Null(Problem(ShapeByName(name)));
    }

    [Fact]
    public void A_curved_wall_40_metres_long_has_a_valid_lightmap_set()
    {
        var path = Spline.Through([Vector3.Zero, new Vector3(10.6f, 0f, 7.1f), new Vector3(17.7f, 0f, 19.5f), new Vector3(30.2f, 0f, 21.3f)]);
        Assert.InRange(path.Length, 39f, 41f);
        Vector2[] outline = [new Vector2(0.15f, 0f), new Vector2(0.15f, 3f), new Vector2(-0.15f, 3f), new Vector2(-0.15f, 0f)];

        var wall = Shapes.Extrude(path, outline, closedOutline: true, step: 0.5f);

        Assert.Null(Problem(wall));
    }

    [Fact]
    public void Charts_of_a_box_get_texels_by_their_size()
    {
        // A face 4 m by 1 m and one 1 m by 1 m: the first spans four times the second's UV area.
        var box = Shapes.Box(new Vector3(4f, 1f, 1f));

        float Area(Vector3 normal)
        {
            var uvs = box.Vertices.Where(v => v.Normal == normal).Select(v => v.Uv2).ToArray();
            Assert.Equal(4, uvs.Length);
            var low = uvs.Aggregate(Vector2.Min);
            var high = uvs.Aggregate(Vector2.Max);
            return (high.X - low.X) * (high.Y - low.Y);
        }

        Assert.Equal(4f, Area(Vector3.UnitZ) / Area(Vector3.UnitX), 0.01f);
    }

    [Fact]
    public void A_mesh_that_already_has_one_is_kept()
    {
        var box = Shapes.Box(Vector3.One);
        Assert.Same(box, box.WithLightmapUvs());
    }

    [Fact]
    public void A_mesh_without_one_gets_one()
    {
        var bare = Shapes.Box(Vector3.One) with { Vertices = [.. Shapes.Box(Vector3.One).Vertices.Select(v => v with { Uv2 = Vector2.Zero })] };
        Assert.NotNull(Problem(bare));

        var made = bare.WithLightmapUvs();

        Assert.Null(Problem(made));
        Assert.Equal(bare.Indices.Length, made.Indices.Length);
        Assert.Same(bare.Surface, made.Surface);
    }
}
