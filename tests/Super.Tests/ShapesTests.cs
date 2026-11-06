using System.Numerics;
using Rendition;
using Super.Testing;

namespace Super.Tests;

public class ShapesTests
{
    private const float Tolerance = 1e-4f;

    /// <summary>Each closed shape with a point inside it, from which every normal must point away.</summary>
    public static TheoryData<string> ClosedShapes => ["box", "sphere", "hemisphere", "capsule", "short capsule", "cylinder", "tapered", "cone", "torus", "prism", "hex", "flat sphere"];

    private static GeneratedMesh Make(string name) => name switch
    {
        "box" => Shapes.Box(new Vector3(2f, 3f, 0.7f)),
        "sphere" => Shapes.Sphere(1.5f, segments: 8, rings: 4),
        "hemisphere" => Shapes.Hemisphere(1f, segments: 8, rings: 3),
        "capsule" => Shapes.Capsule(0.5f, 3f, segments: 8, rings: 3),
        "short capsule" => Shapes.Capsule(0.5f, 1f, segments: 8, rings: 3),
        "cylinder" => Shapes.Cylinder(1f, 1f, 2f, segments: 8),
        "tapered" => Shapes.Cylinder(0.3f, 1f, 2f, segments: 8),
        "cone" => Shapes.Cone(1f, 2f, segments: 8),
        "torus" => Shapes.Torus(1f, 0.25f, segments: 8, sides: 4),
        "prism" => Shapes.Prism(5, 1f, 2f),
        "hex" => Shapes.Hex(1f, 0.5f),
        "flat sphere" => Shapes.Flat(Shapes.Sphere(1f, segments: 8, rings: 4)),
        "plane" => Shapes.Plane(new Vector2(4f, 2f), subdivisions: 3),
        "quad" => Shapes.Quad(new Vector2(2f, 1f)),
        "ribbon" => Shapes.Ribbon(Bend(), 2f, step: 0.25f),
        "embankment" => Shapes.Extrude(Bend(), [new Vector2(1.5f, -0.5f), new Vector2(1f, 0f), new Vector2(-1f, 0f), new Vector2(-1.5f, -0.5f)], step: 0.25f),
        "tube" => Shapes.Tube(Bend(), 0.2f, sides: 6, step: 0.25f),
        "bent plane" => Shapes.Bend(Shapes.Plane(new Vector2(0.5f, 8f), subdivisions: 24), Bend()),
        _ => throw new ArgumentException(name),
    };

    /// <summary>A curve that turns left, climbs and turns back, for shapes made along a spline.</summary>
    private static Spline Bend() => Spline.Through([Vector3.Zero, new Vector3(0f, 0f, 4f), new Vector3(3f, 1f, 7f), new Vector3(7f, 1.5f, 8f)]);

    /// <summary>Where the outward direction at a point starts: the middle for most, the tube's core for a torus.</summary>
    private static Vector3 Inside(string name, Vector3 position) => name switch
    {
        "hemisphere" => new Vector3(0f, 0.1f, 0f),
        "torus" => position with { Y = 0f } == Vector3.Zero ? Vector3.Zero : Vector3.Normalize(position with { Y = 0f }),
        _ => Vector3.Zero,
    };

    [Theory]
    [InlineData("box", 24, 12)]
    // 9 columns (the seam is doubled) by 5 rows; 2 × 8 × 4 triangles, less 8 at each pole.
    [InlineData("sphere", 45, 48)]
    // A dome of 9 × 4 with 8 lost at the top, and a base of a centre and 8 round it.
    [InlineData("hemisphere", 45, 48)]
    // 8 rows: two ends of 4 rows each. 7 bands of 16, less 8 at each pole.
    [InlineData("capsule", 72, 96)]
    // The same, but the middle band has no height, so its 16 triangles go too.
    [InlineData("short capsule", 72, 80)]
    // A side of 9 × 2 with 16 triangles, and two ends of 9 vertices and 8 triangles.
    [InlineData("cylinder", 36, 32)]
    [InlineData("tapered", 36, 32)]
    // A side whose top row meets at the tip, so half its 16 triangles go, and one end.
    [InlineData("cone", 27, 16)]
    // 9 × 5 vertices; 8 × 4 squares of two triangles.
    [InlineData("torus", 45, 64)]
    // 10 side triangles and 5 on each end, each with three corners of its own.
    [InlineData("prism", 60, 20)]
    [InlineData("hex", 72, 24)]
    [InlineData("flat sphere", 144, 48)]
    // 4 × 4 vertices; 3 × 3 squares of two triangles.
    [InlineData("plane", 16, 18)]
    [InlineData("quad", 4, 2)]
    public void Shapes_have_the_hand_counted_vertices_and_triangles(string name, int vertices, int triangles)
    {
        var mesh = Make(name);

        Assert.Equal(vertices, mesh.Vertices.Length);
        Assert.Equal(triangles, mesh.Indices.Length / 3);
        Assert.Equal(0, mesh.Indices.Length % 3);
    }

    [Theory]
    [MemberData(nameof(ClosedShapes))]
    public void Closed_shapes_have_unit_normals_pointing_outwards(string name)
    {
        var mesh = Make(name);

        foreach (var vertex in mesh.Vertices)
        {
            Assert.Equal(1f, vertex.Normal.Length(), Tolerance);
            Assert.True(
                Vector3.Dot(vertex.Normal, vertex.Position - Inside(name, vertex.Position)) > 0f,
                $"The normal {vertex.Normal} at {vertex.Position} points inwards.");
        }
    }

    [Theory]
    [MemberData(nameof(ClosedShapes))]
    [InlineData("plane")]
    [InlineData("quad")]
    [InlineData("ribbon")]
    [InlineData("embankment")]
    [InlineData("tube")]
    [InlineData("bent plane")]
    public void Every_triangle_winds_to_face_the_way_its_normals_point(string name)
    {
        var mesh = Make(name);

        for (int i = 0; i < mesh.Indices.Length; i += 3)
        {
            var (a, b, c) = Corners(mesh, i);
            var face = Vector3.Cross(b.Position - a.Position, c.Position - a.Position);
            Assert.True(face.Length() > 0f, $"Triangle {i / 3} has no area.");
            foreach (var corner in (ReadOnlySpan<Vertex>)[a, b, c])
            {
                Assert.True(Vector3.Dot(face, corner.Normal) > 0f, $"Triangle {i / 3} faces away from its normal at {corner.Position}.");
            }
        }
    }

    [Theory]
    [MemberData(nameof(ClosedShapes))]
    public void Closed_shapes_are_watertight(string name)
    {
        var mesh = Make(name);

        // Seams double vertices for their UVs, so corners are matched by where they are.
        var edges = new Dictionary<(Vector3, Vector3), int>();
        for (int i = 0; i < mesh.Indices.Length; i += 3)
        {
            var (a, b, c) = Corners(mesh, i);
            foreach (var edge in (ReadOnlySpan<(Vector3, Vector3)>)[(Snap(a), Snap(b)), (Snap(b), Snap(c)), (Snap(c), Snap(a))])
            {
                edges[edge] = edges.GetValueOrDefault(edge) + 1;
            }
        }

        Assert.NotEmpty(edges);
        foreach (var ((from, to), count) in edges)
        {
            Assert.True(count == 1, $"The edge {from} to {to} is used {count} times one way.");
            Assert.True(edges.ContainsKey((to, from)), $"The edge {from} to {to} has nothing on the other side.");
        }
    }

    [Fact]
    public void An_open_plane_is_not_watertight()
    {
        // Proves the watertight check can fail: a plane's rim has nothing on the other side.
        var mesh = Make("plane");
        var edges = new HashSet<(Vector3, Vector3)>();
        for (int i = 0; i < mesh.Indices.Length; i += 3)
        {
            var (a, b, c) = Corners(mesh, i);
            edges.Add((Snap(a), Snap(b)));
            edges.Add((Snap(b), Snap(c)));
            edges.Add((Snap(c), Snap(a)));
        }

        Assert.Contains(edges, edge => !edges.Contains((edge.Item2, edge.Item1)));
    }

    [Theory]
    [MemberData(nameof(ClosedShapes))]
    [InlineData("plane")]
    [InlineData("quad")]
    [InlineData("ribbon")]
    [InlineData("embankment")]
    [InlineData("tube")]
    [InlineData("bent plane")]
    public void Tangents_run_along_u_with_the_handedness_the_shaders_expect(string name)
    {
        var mesh = Make(name);

        for (int i = 0; i < mesh.Indices.Length; i += 3)
        {
            var (a, b, c) = Corners(mesh, i);
            var (alongU, alongV) = UvDirections(a, b, c);
            foreach (var corner in (ReadOnlySpan<Vertex>)[a, b, c])
            {
                var tangent = new Vector3(corner.Tangent.X, corner.Tangent.Y, corner.Tangent.Z);
                Assert.Equal(1f, tangent.Length(), Tolerance);
                Assert.Equal(0f, Vector3.Dot(tangent, corner.Normal), Tolerance);
                Assert.True(Vector3.Dot(tangent, alongU) > 0f, $"The tangent at {corner.Position} runs against u.");

                // surface.glsl builds the bitangent this way, and it must point where v falls.
                var bitangent = Vector3.Cross(corner.Normal, tangent) * corner.Tangent.W;
                Assert.True(Vector3.Dot(bitangent, alongV) < 0f, $"The bitangent at {corner.Position} points where v rises.");
            }
        }
    }

    [Fact]
    public void Sphere_vertices_lie_on_its_radius()
    {
        var mesh = Shapes.Sphere(1.5f, segments: 8, rings: 4);

        foreach (var vertex in mesh.Vertices)
        {
            Assert.Equal(1.5f, vertex.Position.Length(), Tolerance);
        }
    }

    [Fact]
    public void Torus_vertices_lie_on_its_tube()
    {
        var mesh = Shapes.Torus(1f, 0.25f, segments: 8, sides: 4);

        foreach (var vertex in mesh.Vertices)
        {
            var core = Vector3.Normalize(vertex.Position with { Y = 0f });
            Assert.Equal(0.25f, Vector3.Distance(vertex.Position, core), Tolerance);
        }
    }

    [Fact]
    public void Cone_side_normals_tip_up_by_the_slope()
    {
        // A cone 1 wide and 2 tall leans so its normal is (2, 1) along the ground and up, over √5.
        var mesh = Shapes.Cone(1f, 2f, segments: 8);

        var side = mesh.Vertices.First(vertex => vertex.Position == new Vector3(1f, -1f, 0f) && vertex.Normal.Y > 0f);
        Assert.Equal(2f / MathF.Sqrt(5f), side.Normal.X, Tolerance);
        Assert.Equal(1f / MathF.Sqrt(5f), side.Normal.Y, Tolerance);
    }

    [Fact]
    public void Flat_gives_each_triangle_one_normal_across_its_face()
    {
        var mesh = Shapes.Flat(Shapes.Sphere(1f, segments: 8, rings: 4));

        for (int i = 0; i < mesh.Indices.Length; i += 3)
        {
            var (a, b, c) = Corners(mesh, i);
            var face = Vector3.Normalize(Vector3.Cross(b.Position - a.Position, c.Position - a.Position));
            Assert.Equal(face, a.Normal);
            Assert.Equal(face, b.Normal);
            Assert.Equal(face, c.Normal);
        }
    }

    [Fact]
    public void Uvs_stretch_once_across_a_plane_unless_asked_to_tile()
    {
        var stretched = Shapes.Plane(new Vector2(4f, 2f));
        var tiled = Shapes.Plane(new Vector2(4f, 2f), uvPerMetre: 0.5f);

        Assert.Equal(new Vector2(1f, 1f), MaxUv(stretched));

        // 4 m by 2 m at half a tile per metre.
        Assert.Equal(new Vector2(2f, 1f), MaxUv(tiled));
    }

    [Fact]
    public void A_plane_tiles_at_its_surfaces_width()
    {
        var plane = Shapes.Plane(new Vector2(6f, 4f), surface: new Surface { TileWidth = 2f });

        // One tile every 2 m: 6 m is 3 tiles and 4 m is 2.
        Assert.Equal(new Vector2(3f, 2f), MaxUv(plane));
    }

    [Theory]
    [InlineData(0f, "0")]
    [InlineData(float.NaN, "NaN")]
    [InlineData(float.PositiveInfinity, "Infinity")]
    [InlineData(1e-40f, "1E-40")]
    public void A_surface_width_that_is_not_a_finite_number_above_zero_is_refused(float width, string shown)
    {
        var problem = Assert.Throws<ArgumentOutOfRangeException>(() => Shapes.Sphere(surface: new Surface { TileWidth = width }));

        Assert.Equal($"The surface's TileWidth must be a finite number above zero, not {shown}. (Parameter 'surface')", problem.Message);
    }

    [Fact]
    public void A_negative_uv_per_metre_is_refused_with_a_plain_minus_under_a_swedish_culture()
    {
        var before = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("sv-SE");

            var problem = Assert.Throws<ArgumentOutOfRangeException>(() => Shapes.Box(Vector3.One, uvPerMetre: -0.5f));

            Assert.Equal("uvPerMetre must be a finite number above zero, not -0.5. (Parameter 'uvPerMetre')", problem.Message);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void A_given_uv_per_metre_beats_the_surfaces_width()
    {
        var plane = Shapes.Plane(new Vector2(6f, 4f), surface: new Surface { TileWidth = 2f }, uvPerMetre: 1f);

        Assert.Equal(new Vector2(6f, 4f), MaxUv(plane));
    }

    [Fact]
    public void Without_a_width_a_plane_stretches_its_texture_once()
    {
        var plane = Shapes.Plane(new Vector2(6f, 4f), surface: new Surface());

        Assert.Equal(new Vector2(1f, 1f), MaxUv(plane));
    }

    [Fact]
    public void A_cylinder_tiles_at_its_surfaces_width()
    {
        var cylinder = Shapes.Cylinder(1f, 1f, 2f, segments: 8, surface: new Surface { TileWidth = 2f });

        // Round the side is 2π m and down it is 2 m, at one tile per 2 m.
        var side = cylinder.Vertices.Where(vertex => MathF.Abs(vertex.Normal.Y) < 0.5f);
        var most = MaxUv(side);
        Assert.Equal(MathF.PI, most.X, Tolerance);
        Assert.Equal(1f, most.Y, Tolerance);
    }

    [Fact]
    public void A_sphere_tiles_at_its_surfaces_width()
    {
        var sphere = Shapes.Sphere(1f, segments: 8, rings: 4, surface: new Surface { TileWidth = 2f });

        // Round the middle is 2π m and pole to pole is π m, at one tile per 2 m.
        var most = MaxUv(sphere);
        Assert.Equal(MathF.PI, most.X, Tolerance);
        Assert.Equal(MathF.PI * 0.5f, most.Y, Tolerance);
    }

    [Fact]
    public void A_ribbon_tiles_at_its_surfaces_width()
    {
        var straight = Spline.Through([Vector3.Zero, new Vector3(0f, 0f, 10f)]);

        var mesh = Shapes.Ribbon(straight, 4f, surface: new Surface { TileWidth = 2f });

        // 4 m across and 10 m along, one tile per 2 m.
        Assert.Equal(new Vector2(2f, 5f), MaxUv(mesh));
    }

    [Fact]
    public void Box_faces_tile_by_their_own_size()
    {
        var box = Shapes.Box(new Vector3(3f, 1f, 2f), uvPerMetre: 1f);

        // The top is 3 m across x and 2 m along z; the front is 3 m wide and 1 m high.
        var top = box.Vertices.Where(vertex => vertex.Normal == Vector3.UnitY).ToArray();
        var front = box.Vertices.Where(vertex => vertex.Normal == Vector3.UnitZ).ToArray();
        Assert.Equal(new Vector2(3f, 2f), MaxUv(top));
        Assert.Equal(new Vector2(3f, 1f), MaxUv(front));
    }

    [Fact]
    public void Sphere_uvs_tile_round_its_middle_and_from_pole_to_pole()
    {
        var sphere = Shapes.Sphere(1f, segments: 8, rings: 4, uvPerMetre: 1f);

        // Round the middle is 2π m and pole to pole is π m.
        var most = MaxUv(sphere.Vertices);
        Assert.Equal(2f * MathF.PI, most.X, Tolerance);
        Assert.Equal(MathF.PI, most.Y, Tolerance);
    }

    [Fact]
    public void Capsule_uvs_run_down_its_outline()
    {
        var capsule = Shapes.Capsule(0.5f, 3f, segments: 8, rings: 3, uvPerMetre: 1f);

        // Two quarter circles of radius 0.5 and a 2 m middle: π/2 + 2.
        Assert.Equal(MathF.PI * 0.5f + 2f, MaxUv(capsule.Vertices).Y, Tolerance);
    }

    [Fact]
    public void Built_in_shapes_are_one_unit_wide_and_centred_on_zero()
    {
        var meshes = Shapes.BuiltIn();
        GeneratedMesh Of(ModelRef model) => meshes[-model.Index - 1];

        Assert.Equal((new Vector3(-0.5f), new Vector3(0.5f)), Bounds(Of(ModelRef.Sphere)));
        Assert.Equal((new Vector3(-0.5f, -1f, -0.5f), new Vector3(0.5f, 1f, 0.5f)), Bounds(Of(ModelRef.Capsule)));
        Assert.Equal((new Vector3(-0.5f), new Vector3(0.5f)), Bounds(Of(ModelRef.Cylinder)));
        Assert.Equal((new Vector3(-0.5f), new Vector3(0.5f)), Bounds(Of(ModelRef.Cone)));

        // The cylinder and cone have the same bounds, but only the cone comes to a point.
        Assert.All(Of(ModelRef.Cone).Vertices.Where(vertex => vertex.Position.Y == 0.5f), vertex => Assert.Equal(0f, vertex.Position.X));
        Assert.Contains(Of(ModelRef.Cylinder).Vertices, vertex => vertex.Position == new Vector3(0.5f, 0.5f, 0f));
        Assert.Equal((new Vector3(-0.5f, 0f, -0.5f), new Vector3(0.5f, 0f, 0.5f)), Bounds(Of(ModelRef.Plane)));
        Assert.Equal((new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, 0.5f, 0f)), Bounds(Of(ModelRef.Quad)));
        Assert.Equal((new Vector3(-0.5f, -0.125f, -0.5f), new Vector3(0.5f, 0.125f, 0.5f)), Bounds(Of(ModelRef.Torus)));
        Assert.Equal(meshes.Length, Shapes.BuiltInNames.Length);
    }

    [Fact]
    public void Built_in_shapes_are_always_ready_and_cannot_be_unloaded()
    {
        var content = new Content(new MemoryContent());

        Assert.True(content.IsReady(ModelRef.Torus));
        var problem = Assert.Throws<ArgumentException>(() => content.Unload(ModelRef.Sphere));
        Assert.StartsWith("The built-in shapes, such as the cube, can't be unloaded.", problem.Message);
    }

    [Fact]
    public void Drawing_a_built_in_shape_that_does_not_exist_says_so()
    {
        var view = new View();

        view.Model(1, ModelRef.Torus, Matrix4x4.Identity);
        var problem = Assert.Throws<ArgumentException>(() => view.Model(2, new ModelRef(-8), Matrix4x4.Identity));

        Assert.StartsWith("Model -8 is not a built-in shape.", problem.Message);
        Assert.Single(view.Drawn.ToArray());
    }

    [Fact]
    public void Capsule_shorter_than_its_width_is_refused()
    {
        var problem = Assert.Throws<ArgumentOutOfRangeException>(() => Shapes.Capsule(0.5f, 0.9f));

        Assert.Equal("height", problem.ParamName);
        Assert.Contains("at least twice its radius (1)", problem.Message);
    }

    [Fact]
    public void Torus_whose_tube_is_too_thick_is_refused()
    {
        var problem = Assert.Throws<ArgumentOutOfRangeException>(() => Shapes.Torus(0.5f, 0.5f));

        Assert.Equal("tube", problem.ParamName);
        Assert.Contains("thinner than its radius (0.5)", problem.Message);
    }

    [Fact]
    public void Shapes_with_too_few_segments_are_refused()
    {
        var problem = Assert.Throws<ArgumentOutOfRangeException>(() => Shapes.Sphere(segments: 2));

        Assert.Equal("segments", problem.ParamName);
        Assert.Contains("at least 3 segments", problem.Message);
    }

    [Fact]
    public void Cylinder_with_no_ends_is_refused()
    {
        var problem = Assert.Throws<ArgumentOutOfRangeException>(() => Shapes.Cylinder(0f, 0f));

        Assert.Contains("needs a top or bottom radius above zero", problem.Message);
    }

    [Theory]
    // 10 m long with a ring every metre: 11 rows of 2 columns, and 10 × 2 triangles.
    [InlineData("ribbon", 22, 20)]
    // Three sharp edges, each its own strip of 2 columns.
    [InlineData("embankment", 66, 60)]
    // Four sides, smooth, with the seam repeated: 5 columns.
    [InlineData("tube", 55, 80)]
    public void Shapes_along_a_spline_have_a_ring_every_step(string name, int vertices, int triangles)
    {
        var straight = Spline.Through([Vector3.Zero, new Vector3(0f, 0f, 10f)]);
        var mesh = name switch
        {
            "ribbon" => Shapes.Ribbon(straight, 4f, step: 1f),
            "embankment" => Shapes.Extrude(straight, [new Vector2(3f, -1f), new Vector2(2f, 0f), new Vector2(-2f, 0f), new Vector2(-3f, -1f)], step: 1f),
            _ => Shapes.Tube(straight, 0.5f, sides: 4, step: 1f),
        };

        Assert.Equal(vertices, mesh.Vertices.Length);
        Assert.Equal(triangles * 3, mesh.Indices.Length);
    }

    [Fact]
    public void A_ribbon_lies_flat_facing_up_with_uvs_in_metres()
    {
        var straight = Spline.Through([Vector3.Zero, new Vector3(0f, 0f, 10f)]);

        var mesh = Shapes.Ribbon(straight, 4f, step: 0.5f);

        Assert.All(mesh.Vertices, vertex => Assert.Equal(Vector3.UnitY, vertex.Normal));
        Assert.All(mesh.Vertices, vertex => Assert.Equal(2f, MathF.Abs(vertex.Position.X), Tolerance));

        // The outline starts on the right, which looking along +z is -x, and u counts across from there.
        Assert.All(mesh.Vertices.Where(vertex => vertex.Uv.X == 0f), vertex => Assert.Equal(-2f, vertex.Position.X, Tolerance));
        Assert.All(mesh.Vertices, vertex => Assert.Equal(vertex.Position.Z, vertex.Uv.Y, Tolerance));
        Assert.Equal(new Vector2(4f, 10f), MaxUv(mesh));
    }

    [Fact]
    public void A_tube_keeps_its_radius_round_the_spline_with_normals_pointing_out()
    {
        var straight = Spline.Through([Vector3.Zero, new Vector3(0f, 0f, 10f)]);

        var mesh = Shapes.Tube(straight, 0.5f, sides: 4, step: 1f);

        foreach (var vertex in mesh.Vertices)
        {
            var outward = vertex.Position with { Z = 0f };
            Assert.Equal(0.5f, outward.Length(), Tolerance);
            Assert.Equal(1f, Vector3.Dot(Vector3.Normalize(outward), vertex.Normal), Tolerance);
        }

        // Round a square of side 0.5 × √2.
        Assert.Equal(2f * MathF.Sqrt(2f), MaxUv(mesh).X, Tolerance);
    }

    [Fact]
    public void Bend_lays_a_mesh_along_a_spline_and_carries_on_straight_past_its_end()
    {
        float kappa = 4f / 3f * (MathF.Sqrt(2f) - 1f);
        var arc = Spline.Bezier([new SplineKnot(Vector3.UnitX, Out: new Vector3(0f, 0f, kappa)), new SplineKnot(Vector3.UnitZ, In: new Vector3(kappa, 0f, 0f))]);

        // One metre longer than the quarter circle, so its front sticks out one metre past the end.
        float length = arc.Length + 1f;
        var box = Shapes.Box(new Vector3(0.2f, 0.2f, length));
        var bent = Shapes.Bend(box, arc);

        // The arc starts at (1, 0, 0) heading +z, where the box's +x (its left) is +x.
        AssertMoved(box, bent, new Vector3(0.1f, 0.1f, -length / 2f), new Vector3(1.1f, 0.1f, 0f));

        // It ends at (0, 0, 1) heading -x, where the box's left is +z, then goes on one metre.
        AssertMoved(box, bent, new Vector3(0.1f, 0.1f, length / 2f), new Vector3(-1f, 0.1f, 1.1f));

        // The front end faces the way the spline left off, and the top still faces up.
        for (int i = 0; i < box.Vertices.Length; i++)
        {
            if (box.Vertices[i].Normal == Vector3.UnitZ)
            {
                Assert.True(Vector3.Distance(-Vector3.UnitX, bent.Vertices[i].Normal) < 1e-3f, $"The front faces {bent.Vertices[i].Normal}.");
            }
            else if (box.Vertices[i].Normal == Vector3.UnitY)
            {
                Assert.True(Vector3.Distance(Vector3.UnitY, bent.Vertices[i].Normal) < 1e-3f, $"The top faces {bent.Vertices[i].Normal}.");
            }
        }
    }

    [Theory]
    [InlineData(new float[] { 0f, 0f }, false, "An outline needs at least two points.")]
    [InlineData(new float[] { 0f, 0f, 1f, 0f }, true, "A closed outline needs at least three points.")]
    [InlineData(new float[] { 0f, 0f, 1f, 0f, 1f, 0f }, false, "Outline points 1 and 2 are in the same place.")]
    [InlineData(new float[] { 0f, 0f, 1f, 0f, 0f, 0f }, true, "Outline points 2 and 0 are in the same place.")]
    [InlineData(new float[] { 0f, 0f, float.NaN, 0f }, false, "Outline point 1 must be finite.")]
    public void Outlines_that_cant_be_swept_are_explained(float[] numbers, bool closed, string expected)
    {
        var outline = Enumerable.Range(0, numbers.Length / 2).Select(i => new Vector2(numbers[i * 2], numbers[i * 2 + 1])).ToArray();
        var straight = Spline.Through([Vector3.Zero, Vector3.UnitZ]);

        var problem = Assert.Throws<ArgumentException>(() => Shapes.Extrude(straight, outline, closed));

        Assert.Equal($"{expected} (Parameter 'outline')", problem.Message);
    }

    private static void AssertMoved(GeneratedMesh before, GeneratedMesh after, Vector3 from, Vector3 to)
    {
        int found = 0;
        for (int i = 0; i < before.Vertices.Length; i++)
        {
            if (Vector3.Distance(before.Vertices[i].Position, from) < Tolerance)
            {
                Assert.True(Vector3.Distance(after.Vertices[i].Position, to) < 1e-3f, $"{from} went to {after.Vertices[i].Position}, not {to}.");
                found++;
            }
        }

        Assert.True(found > 0, $"No vertex was at {from}.");
    }

    private static (Vertex A, Vertex B, Vertex C) Corners(GeneratedMesh mesh, int first) =>
        (mesh.Vertices[mesh.Indices[first]], mesh.Vertices[mesh.Indices[first + 1]], mesh.Vertices[mesh.Indices[first + 2]]);

    private static Vector3 Snap(Vertex vertex) => Vector3.Round(vertex.Position * 1e4f) / 1e4f;

    /// <summary>Which way u and v rise across a triangle, worked out from its corners alone.</summary>
    private static (Vector3 AlongU, Vector3 AlongV) UvDirections(Vertex a, Vertex b, Vertex c)
    {
        var edge1 = b.Position - a.Position;
        var edge2 = c.Position - a.Position;
        var uv1 = b.Uv - a.Uv;
        var uv2 = c.Uv - a.Uv;
        float determinant = uv1.X * uv2.Y - uv2.X * uv1.Y;
        return ((edge1 * uv2.Y - edge2 * uv1.Y) / determinant, (edge2 * uv1.X - edge1 * uv2.X) / determinant);
    }

    private static Vector2 MaxUv(GeneratedMesh mesh) => MaxUv(mesh.Vertices);

    private static Vector2 MaxUv(IEnumerable<Vertex> vertices) => vertices.Aggregate(new Vector2(float.MinValue), (most, vertex) => Vector2.Max(most, vertex.Uv));

    private static (Vector3 Min, Vector3 Max) Bounds(GeneratedMesh mesh) =>
        (mesh.Vertices.Aggregate(new Vector3(float.MaxValue), (least, vertex) => Vector3.Min(least, vertex.Position)),
         mesh.Vertices.Aggregate(new Vector3(float.MinValue), (most, vertex) => Vector3.Max(most, vertex.Position)));
}
