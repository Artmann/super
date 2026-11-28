using System.Globalization;
using System.Numerics;
using Rendition;

namespace Super.Tests;

/// <summary>Grass: the rings and squares it scatters over, the looks and maps it accepts, and how it reaches a view.</summary>
public class GrassTests
{
    // The radii at a detail of 1: 16, 36, 80 and 160 metres.
    private static readonly Vector4 Rings = GrassField.Radii(1f);

    [Theory]
    [InlineData(0f, 0)]
    [InlineData(15.9f, 0)]
    [InlineData(16f, 1)]
    [InlineData(35.9f, 1)]
    [InlineData(50f, 2)]
    [InlineData(159f, 3)]
    [InlineData(160f, -1)]
    [InlineData(500f, -1)]
    public void A_square_keeps_the_finest_ring_it_is_inside(float nearest, int level)
    {
        Assert.Equal(level, GrassField.TileLevel(nearest, Rings));
    }

    [Fact]
    public void Half_the_detail_brings_every_ring_in_by_half()
    {
        Assert.Equal(new Vector4(8f, 18f, 40f, 80f), GrassField.Radii(0.5f));
        Assert.Equal(1, GrassField.TileLevel(10f, GrassField.Radii(0.5f)));
    }

    [Fact]
    public void Blades_are_drawn_in_full_over_most_of_the_first_ring_and_with_fewer_rows_out_to_the_second()
    {
        // Three quarters of 16 metres, then on to 36; at half the detail, half as far.
        Assert.Equal(new Vector4(12f, 36f, 0f, 0f), GrassField.DetailDistances(Rings));
        Assert.Equal(new Vector4(6f, 18f, 0f, 0f), GrassField.DetailDistances(GrassField.Radii(0.5f)));
    }

    [Fact]
    public void Squares_are_planned_only_in_front_of_the_camera_and_inside_the_last_ring()
    {
        var camera = new Vector3(1f, 2f, 1f);
        var tiles = PlanLookingAlongMinusZ(camera, new GrassTile[GrassField.MaxTiles], out int count);

        Assert.True(count > 0, "No square was planned in front of the camera.");
        bool sawEveryLevel = true;
        for (int level = 0; level < 4; level++)
        {
            sawEveryLevel &= tiles.Any(tile => tile.Level == level);
        }

        Assert.True(sawEveryLevel, $"The {count} squares planned don't reach every ring.");
        foreach (var tile in tiles)
        {
            // A square's side is 4 metres, so row 0 runs from z 0 to 4 and holds the camera; every row past it is behind.
            Assert.True(tile.Z <= 0, $"Square ({tile.X}, {tile.Z}) is behind a camera at z 1 looking along -z.");
            var low = new Vector2(tile.X, tile.Z) * GrassField.TileSize;
            var at = new Vector2(camera.X, camera.Z);
            float nearest = Vector2.Distance(at, Vector2.Clamp(at, low, low + new Vector2(GrassField.TileSize)));
            Assert.Equal(GrassField.TileLevel(nearest, Rings), tile.Level);
        }

        Assert.Contains(new GrassTile(0, 0, 0), tiles);
    }

    [Fact]
    public void Planning_stops_when_the_list_is_full()
    {
        var tiles = PlanLookingAlongMinusZ(new Vector3(0f, 2f, 0f), new GrassTile[10], out int count);

        Assert.Equal(10, count);
        Assert.Equal(10, tiles.Length);
    }

    [Fact]
    public void The_ground_map_moves_in_whole_texels_round_the_camera()
    {
        // It reaches a 4-metre square past the 160-metre ring each way, over 1024 texels: 328 / 1024 metres each.
        const float texel = 328f / 1024f;

        var (still, stillTexel) = GrassField.CaptureArea(new Vector3(0.1f, 5f, -0.1f), Rings);
        var (moved, _) = GrassField.CaptureArea(new Vector3(0.2f, 5f, 0f), Rings);

        Assert.Equal(texel, stillTexel);
        Assert.Equal(new Vector2(-164f), still);
        Assert.Equal(new Vector2(-164f + texel, -164f), moved);
    }

    [Fact]
    public void A_look_seen_again_keeps_its_kind()
    {
        var looks = new GrassSettings[GrassField.MaxKinds];
        int count = 0;
        var tall = new GrassSettings { Height = 1f };

        int meadow = GrassField.KindOf(looks, ref count, new GrassSettings());
        int first = GrassField.KindOf(looks, ref count, tall);
        int again = GrassField.KindOf(looks, ref count, tall);

        Assert.Equal(0, meadow);
        Assert.Equal(1, first);
        Assert.Equal(1, again);
        Assert.Equal(2, count);
    }

    [Fact]
    public void A_ninth_look_in_a_frame_is_refused()
    {
        var looks = new GrassSettings[GrassField.MaxKinds];
        int count = 0;
        for (int i = 0; i < GrassField.MaxKinds; i++)
        {
            GrassField.KindOf(looks, ref count, new GrassSettings { Height = 0.1f + i * 0.1f });
        }

        var error = Assert.Throws<ArgumentException>(() => GrassField.KindOf(looks, ref count, new GrassSettings { Height = 1.5f }));

        Assert.Equal("A frame's grass can have at most 8 different looks. (Parameter 'view')", error.Message);
    }

    [Theory]
    [InlineData("height", "Grass height must be above 0 and at most 2 metres, not 2.5.")]
    [InlineData("width", "Grass width must be above 0 and at most 0.5 metres, not 0.75.")]
    [InlineData("clump", "Grass clump size must be from 0.25 to 8 metres, not 0.1.")]
    [InlineData("sway", "Grass sway must be from 0 to 4, not 4.5.")]
    [InlineData("density", "Grass density must be from 0 to 1, not 1.5.")]
    [InlineData("bend", "Grass bend must be from 0 to 1, not -0.5.")]
    [InlineData("colour", "Grass colours must be from 0 to 1.")]
    public void A_look_out_of_range_is_explained_with_invariant_numbers_under_swedish_culture(string setting, string message)
    {
        var look = setting switch
        {
            "height" => new GrassSettings { Height = 2.5f },
            "width" => new GrassSettings { Width = 0.75f },
            "clump" => new GrassSettings { ClumpSize = 0.1f },
            "sway" => new GrassSettings { Sway = 4.5f },
            "density" => new GrassSettings { Density = 1.5f },
            "bend" => new GrassSettings { Bend = -0.5f },
            _ => new GrassSettings { TipColor = new Vector3(1.5f, 0.5f, 0.5f) },
        };

        Assert.Equal(message, InSwedish(() => GrassSettings.Problem(look)));
    }

    [Fact]
    public void The_default_meadow_can_be_drawn()
    {
        Assert.Null(GrassSettings.Problem(new GrassSettings()));
        Assert.Null(GrassSettings.Problem(new GrassSettings { Density = 0f, Sway = 0f, Translucency = 0f }));
    }

    [Theory]
    [InlineData(4, 0f, 0f, 1f, 1f, "A grass density map's channel must be from 0 to 3, not 4.")]
    [InlineData(0, 0f, 0f, 0f, 1f, "A grass density map's Max must be above its Min along both x and z.")]
    [InlineData(0, 0f, 2f, 1f, 1f, "A grass density map's Max must be above its Min along both x and z.")]
    [InlineData(0, 0f, float.NaN, 1f, 1f, "A grass density map's Max must be above its Min along both x and z.")]
    public void A_density_map_that_cant_be_read_is_explained(int channel, float minX, float minZ, float maxX, float maxZ, string message)
    {
        var density = new GrassDensity(default, new Vector2(minX, minZ), new Vector2(maxX, maxZ), channel);

        Assert.Equal(message, GrassDensity.Problem(density));
    }

    [Theory]
    [InlineData(0.2f)]
    [InlineData(2.5f)]
    public void Renderer_grass_detail_out_of_range_is_explained_with_invariant_numbers_under_swedish_culture(float detail)
    {
        var settings = new RenderSettings(new RendererOptions { Surface = RendererSurface.Headless, Width = 64, Height = 64 });

        var error = InSwedish(() => Assert.Throws<ArgumentOutOfRangeException>(() => settings.GrassDetail = detail));

        Assert.StartsWith("Grass detail can be from 0.25 to 2.", error.Message);
        Assert.Equal(1f, settings.GrassDetail);
    }

    [Fact]
    public void A_model_drawn_with_grass_out_of_range_is_refused_by_name()
    {
        var view = new View();
        var options = new ModelOptions { Grass = new Grass(new GrassSettings { Height = 3f }) };

        var error = Assert.Throws<ArgumentException>(() => view.Model(7, ModelRef.Cube, Matrix4x4.Identity, null, options));

        Assert.Equal("Model 7's grass: Grass height must be above 0 and at most 2 metres, not 3. (Parameter 'options')", error.Message);
    }

    [Fact]
    public void A_model_drawn_with_an_unreadable_density_map_is_refused_by_name()
    {
        var view = new View();
        var map = new GrassMap(default, new Vector2(4f, 0f), new Vector2(2f, 8f));
        var options = new ModelOptions { Grass = new Grass(new GrassSettings(), map) };

        var error = Assert.Throws<ArgumentException>(() => view.Model(3, ModelRef.Cube, Matrix4x4.Identity, null, options));

        Assert.Equal("Model 3's grass: A grass density map's Max must be above its Min along both x and z. (Parameter 'options')", error.Message);
    }

    [Fact]
    public void An_entity_with_grass_draws_its_model_with_that_grass()
    {
        var world = new World();
        var view = new View();
        var look = new GrassSettings { Height = 0.8f };
        world.Spawn(Transform.Identity, new Model(ModelRef.Cube), new Grass(look));
        world.Spawn(Transform.Identity, new Model(ModelRef.Sphere));

        view.Begin(0f);
        WorldFrame.Describe(view, world);

        var drawn = view.Drawn.ToArray();
        Assert.Equal(2, drawn.Length);
        Assert.Equal(new Grass(look), drawn.Single(draw => draw.Model == ModelRef.Cube).Grass);
        Assert.Null(drawn.Single(draw => draw.Model == ModelRef.Sphere).Grass);
    }

    // Plans the squares a camera at the given place, looking along -z with a 60-degree view, may see grass in.
    private static GrassTile[] PlanLookingAlongMinusZ(Vector3 camera, GrassTile[] tiles, out int count)
    {
        var view = Matrix4x4.CreateLookAt(camera, camera - Vector3.UnitZ, Vector3.UnitY);
        var projection = Projection.PerspectiveInfiniteReversedZ(MathF.PI / 3f, 16f / 9f, 0.1f);
        Span<Vector4> planes = stackalloc Vector4[5];
        GrassField.Planes(view * projection, planes);
        count = GrassField.Plan(camera, planes, Rings, tiles);
        return tiles[..count];
    }

    private static T InSwedish<T>(Func<T> body)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
            return body();
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
