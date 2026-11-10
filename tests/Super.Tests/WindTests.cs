using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using Rendition;
using Rendition.Passes;

namespace Super.Tests;

/// <summary>
/// Wind: how far a vertex sways for its height above its part's foot, how gusts and flutter move it, and how the
/// view's wind reaches the frame block that wind.glsl reads.
/// </summary>
public class WindTests
{
    private const float Tolerance = 1e-5f;

    private static readonly Vector3 East = Vector3.UnitX;

    [Theory]
    [InlineData(0f)]
    [InlineData(0.37f)]
    [InlineData(12.5f)]
    public void No_strength_is_no_sway(float time)
    {
        var sway = Wind.Sway(new Vector3(1.3f, 0.4f, -2.2f), heightAboveBase: 0.8f, East, strength: 0f, gustiness: 0.5f, time);

        Assert.Equal(Vector3.Zero, sway);
    }

    [Fact]
    public void A_steady_wind_leans_the_tip_of_grass_1_m_tall_by_its_strength()
    {
        // At the origin at time 0 the flutter is at the middle of its swing, so only the lean is left: with no
        // gusts it is the strength times the height squared, 0.3 × 1² downwind and 0.3 × 0.5² = 0.075 half way up.
        Assert.Equal(new Vector3(0.3f, 0f, 0f), Wind.Sway(Vector3.Zero, 1f, East, 0.3f, gustiness: 0f, time: 0f));
        Assert.Equal(new Vector3(0.075f, 0f, 0f), Wind.Sway(Vector3.Zero, 0.5f, East, 0.3f, gustiness: 0f, time: 0f));
    }

    [Theory]
    [InlineData(0f, 0f, 0f)]
    [InlineData(2.4f, -1.1f, 0.3f)]
    [InlineData(-7.9f, 3.6f, 5.25f)]
    public void Sway_grows_with_the_square_of_height(float x, float z, float time)
    {
        // The two vertices share a place, so they share a gust. The flutter goes as far up as downwind, so taking
        // the rise from the move downwind leaves only the lean.
        var position = new Vector3(x, 0f, z);
        var tip = Wind.Sway(position, 1f, East, 0.35f, 0.5f, time);
        var half = Wind.Sway(position, 0.5f, East, 0.35f, 0.5f, time);
        float leanTip = tip.X - tip.Y;
        float leanHalf = half.X - half.Y;

        Assert.True(leanTip > 0.2f, $"The tip leaned only {leanTip} m.");
        Assert.Equal(4f * leanHalf, leanTip, Tolerance);
    }

    [Fact]
    public void Above_1_m_a_vertex_sways_no_further()
    {
        var position = new Vector3(0.7f, 6.5f, 1.9f);

        Assert.Equal(Wind.Sway(position, 1f, East, 0.35f, 0.5f, 2.3f), Wind.Sway(position, 4f, East, 0.35f, 0.5f, 2.3f));
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(-0.2f, 1.7f)]
    [InlineData(0f, 3.0625f)]
    [InlineData(0f, 41.9f)]
    public void Sway_never_moves_the_base(float heightAboveBase, float time)
    {
        var sway = Wind.Sway(new Vector3(-3.2f, 0.05f, 4.4f), heightAboveBase, new Vector3(0.6f, 0f, -0.3f), 1f, 1f, time);

        Assert.Equal(Vector3.Zero, sway);
    }

    [Fact]
    public void The_same_time_and_place_sway_the_same()
    {
        var position = new Vector3(2.1f, 1.4f, -0.6f);
        var direction = new Vector3(0.6f, 0f, -0.3f);

        var first = Wind.Sway(position, 0.9f, direction, 0.35f, 0.5f, 3.7f);
        var again = Wind.Sway(position, 0.9f, direction, 0.35f, 0.5f, 3.7f);
        var later = Wind.Sway(position, 0.9f, direction, 0.35f, 0.5f, 3.8f);

        Assert.Equal(first, again);
        Assert.NotEqual(first, later);
    }

    [Fact]
    public void A_flutter_peak_adds_2_cm_up_and_downwind_at_the_tip()
    {
        // At 1/16 s the 4 Hz flutter is a quarter of the way round, at its peak, and at the origin its phase is 0.
        // It moves along the diagonal between downwind and up, 0.02 / √2 = 0.0141421 m on each.
        var sway = Wind.Sway(Vector3.Zero, 1f, East, 0.3f, gustiness: 0f, time: 1f / 16f);

        Assert.Equal(0.3141421f, sway.X, Tolerance);
        Assert.Equal(0.0141421f, sway.Y, Tolerance);
        Assert.Equal(0f, sway.Z, Tolerance);
    }

    [Fact]
    public void A_material_with_half_the_sway_leans_half_as_far_but_flutters_as_much()
    {
        var full = Wind.Sway(Vector3.Zero, 1f, East, 0.3f, 0f, 1f / 16f, sway: 1f);
        var half = Wind.Sway(Vector3.Zero, 1f, East, 0.3f, 0f, 1f / 16f, sway: 0.5f);

        Assert.Equal(0.15f, full.X - half.X, Tolerance);
        Assert.Equal(full.Y, half.Y, Tolerance);
        Assert.Equal(Vector3.Zero, Wind.Sway(Vector3.Zero, 1f, East, 0.3f, 0f, 1f / 16f, sway: 0f));
    }

    [Fact]
    public void Gusts_swing_the_lean_between_a_lull_and_a_peak()
    {
        // At gustiness 0.5 the gust factor runs from 1 − 0.8 × 0.5 = 0.6 in a lull to 1 at a peak, and over a
        // stretch of ground both ends are nearly reached.
        float lowest = float.MaxValue;
        float highest = float.MinValue;
        for (int x = -40; x <= 40; x++)
        {
            for (int z = -40; z <= 40; z++)
            {
                float factor = Wind.GustFactor(new Vector3(x * 0.37f, 0f, z * 0.37f), East, 0.5f, time: 0f);
                lowest = MathF.Min(lowest, factor);
                highest = MathF.Max(highest, factor);
            }
        }

        Assert.InRange(lowest, 0.6f, 0.65f);
        Assert.InRange(highest, 0.95f, 1f);
        Assert.Equal(1f, Wind.GustFactor(new Vector3(4.2f, 0f, -1.3f), East, gustiness: 0f, time: 2f));
    }

    [Fact]
    public void Gusts_travel_downwind_at_one_and_a_half_metres_a_second()
    {
        var direction = Vector3.Normalize(new Vector3(0.6f, 0f, -0.3f));
        var here = new Vector3(1.1f, 0f, 2.6f);

        // Two seconds later the gust that was here is 3 m downwind.
        float now = Wind.GustFactor(here, direction, 0.5f, time: 1f);
        float later = Wind.GustFactor(here + direction * 3f, direction, 0.5f, time: 3f);

        Assert.Equal(now, later, Tolerance);
        Assert.NotEqual(now, Wind.GustFactor(here, direction, 0.5f, time: 3f), Tolerance);
    }

    [Fact]
    public void Pack_flattens_the_direction_and_scales_it_by_the_strength()
    {
        // (3, 1, 4) flattens to (3, 0, 4), which is 5 long, so 0.5 of it is (0.3, 0, 0.4).
        var settings = new WindSettings(new Vector3(3f, 1f, 4f), 0.5f, 0.25f);

        Assert.Equal(new Vector4(0.3f, 0f, 0.4f, 0.25f), Wind.Pack(settings, RenderEffects.None));
        Assert.Equal(Vector4.Zero, Wind.Pack(null, RenderEffects.None));
        Assert.Equal(Vector4.Zero, Wind.Pack(settings, RenderEffects.Wind));
        Assert.Equal(Vector4.Zero, Wind.Pack(settings with { Strength = 0f }, RenderEffects.None));
        Assert.Equal(Vector4.Zero, Wind.Pack(new WindSettings(Vector3.UnitY, 0.5f), RenderEffects.None));

        // Other effects off leave the wind alone.
        Assert.Equal(new Vector4(0.3f, 0f, 0.4f, 0.25f), Wind.Pack(settings, RenderEffects.All & ~RenderEffects.Wind));
    }

    [Fact]
    public void The_wind_reaches_the_view_and_a_new_frame_starts_calm()
    {
        var view = new View();
        view.Begin(0f);
        Assert.Null(view.WindSettings);

        view.Wind(new Vector3(0.6f, 0f, -0.3f), 0.35f);
        Assert.Equal(new WindSettings(new Vector3(0.6f, 0f, -0.3f), 0.35f, 0.5f), view.WindSettings);

        view.Begin(0f);
        Assert.Null(view.WindSettings);
    }

    [Theory]
    [InlineData(-0.1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void A_negative_or_endless_strength_is_refused(float strength)
    {
        var view = new View();
        view.Begin(0f);

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => view.Wind(East, strength));

        Assert.StartsWith("The wind's strength must be 0 or more metres, and 0 is a calm.", error.Message);
        Assert.Equal("strength", error.ParamName);
    }

    [Theory]
    [InlineData(-0.01f)]
    [InlineData(1.01f)]
    [InlineData(float.NaN)]
    public void Gustiness_outside_0_to_1_is_refused(float gustiness)
    {
        var view = new View();
        view.Begin(0f);

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => view.Wind(East, 0.3f, gustiness));

        Assert.StartsWith("The wind's gustiness must be from 0 for steady to 1 for gusty.", error.Message);
        Assert.Equal("gustiness", error.ParamName);
    }

    [Fact]
    public void A_wind_straight_up_is_refused_but_a_calm_needs_no_direction()
    {
        var view = new View();
        view.Begin(0f);

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => view.Wind(Vector3.UnitY, 0.3f));
        view.Wind(Vector3.Zero, 0f);

        Assert.StartsWith("The wind must blow along the ground, so its direction needs some x or z.", error.Message);
        Assert.Equal("direction", error.ParamName);
        Assert.Equal(new WindSettings(Vector3.Zero, 0f, 0.5f), view.WindSettings);
    }

    [Fact]
    public void A_direction_that_is_not_finite_is_refused()
    {
        var view = new View();
        view.Begin(0f);

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => view.Wind(new Vector3(float.NaN, 0f, 1f), 0.3f));

        Assert.StartsWith("The wind's direction must be finite.", error.Message);
        Assert.Equal("direction", error.ParamName);
    }

    // The tests above only hold for the game if wind.glsl does the same sums, and there is no GPU in the tests to
    // run it on. So these read the shader's source and check its constants and lines are the ones Wind.cs uses.
    [Fact]
    public void The_shaders_constants_are_the_ones_Wind_uses()
    {
        string shader = WindShader();

        Assert.Equal(Wind.GustSpeed, ShaderConstant(shader, "WIND_GUST_SPEED"), Tolerance);
        Assert.Equal(Wind.GustSize, ShaderConstant(shader, "WIND_GUST_SIZE"), Tolerance);
        Assert.Equal(Wind.Flutter, ShaderConstant(shader, "WIND_FLUTTER"), Tolerance);
        Assert.Equal(Wind.FlutterHz, ShaderConstant(shader, "WIND_FLUTTER_HZ"), Tolerance);
        Assert.Equal(Wind.FlutterStrength, ShaderConstant(shader, "WIND_FLUTTER_STRENGTH"), Tolerance);
        Assert.Equal(Wind.FullHeight, ShaderConstant(shader, "WIND_FULL_HEIGHT"), Tolerance);
        Assert.Equal(2f * MathF.PI, ShaderConstant(shader, "WIND_TAU"), Tolerance);
    }

    [Theory]
    [InlineData("float windHash(", "uint state = (uint(cell.x) * 73856093u) ^ (uint(cell.y) * 19349663u);")]
    [InlineData("float windHash(", "state = state * 747796405u + 2891336453u;")]
    [InlineData("float windHash(", "uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;")]
    [InlineData("float windHash(", "word = (word >> 22u) ^ word;")]
    [InlineData("float windHash(", "return float(word) / 4294967295.0;")]
    [InlineData("float windGust(", "f = f * f * (3.0 - 2.0 * f);")]
    [InlineData("vec3 windSway(", "float h = min(heightAboveBase, WIND_FULL_HEIGHT);")]
    [InlineData("vec3 windSway(", "vec2 at = p.xz / WIND_GUST_SIZE - downwind.xz * (WIND_GUST_SPEED / WIND_GUST_SIZE * frame.time);")]
    [InlineData("vec3 windSway(", "float swing = 0.8 * frame.wind.w;")]
    [InlineData("vec3 windSway(", "float gust = 1.0 - swing + swing * windGust(at);")]
    [InlineData("vec3 windSway(", "float lean = strength * sway * h * h * gust;")]
    [InlineData("vec3 windSway(", "float flutter = WIND_FLUTTER * h * min(strength / WIND_FLUTTER_STRENGTH, 1.0)")]
    [InlineData("vec3 windSway(", "* sin(WIND_TAU * WIND_FLUTTER_HZ * frame.time + dot(p, vec3(3.1, 2.3, 2.7)));")]
    [InlineData("vec3 windSway(", "return downwind * lean + normalize(downwind + vec3(0.0, 1.0, 0.0)) * flutter;")]
    public void The_shader_works_out_the_sway_as_Wind_does(string function, string line)
    {
        Assert.Contains(line, ShaderFunction(WindShader(), function));
    }

    [Theory]
    [InlineData(1, 0, 2340206819u)]
    [InlineData(0, 1, 1478043691u)]
    [InlineData(-3, 7, 3365109634u)]
    public void The_hash_gives_the_PCG_word_worked_out_apart_from_the_code(int x, int y, uint word)
    {
        // The words were worked out in Python with arbitrary-size integers masked to 32 bits, the way the GPU's
        // uints wrap, so a slip in the C# shifts or casts, as for a negative cell, shows.
        Assert.Equal(word / 4294967295f, Wind.Hash(x, y));
    }

    [Theory]
    [InlineData("gbuffer.vert")]
    [InlineData("shadow.vert")]
    public void Both_vertex_shaders_sway_so_the_shadows_follow(string file)
    {
        string shader = File.ReadAllText(Path.Combine(ShaderFolder(), file));

        Assert.Contains("#include \"wind.glsl\"", shader);
        Assert.Contains("windSwayPlaced((placed * vec4(swum, 1.0)).xyz, placed, posed, draw.windBase, draw.windSway)", shader);
    }

    [Fact]
    public void Placed_sway_measures_height_from_the_foot_put_through_the_copys_matrix()
    {
        string placed = ShaderFunction(WindShader(), "vec3 windSwayPlaced(");

        Assert.Contains("float base = (placed * vec4(posed.x, windBase, posed.z, 1.0)).y;", placed);
        Assert.Contains("return world + windSway(world, world.y - base, sway);", placed);
    }

    [Fact]
    public void Placed_sway_leaves_the_vertex_where_it_is_for_a_still_material_or_with_the_switch_off()
    {
        string placed = ShaderFunction(WindShader(), "vec3 windSwayPlaced(");

        Assert.Matches(@"if \(sway <= 0\.0 \|\| \(frame\.off & OFF_WIND\) != 0\) \{\s*return world;\s*\}", placed);
    }

    [Fact]
    public void The_shaders_wind_switch_is_the_one_RenderEffects_uses()
    {
        string common = File.ReadAllText(Path.Combine(ShaderFolder(), "common.glsl"));

        Assert.Contains("const int OFF_WIND = 1 << 17;", common);
        Assert.Equal(1 << 17, (int)RenderEffects.Wind);
    }

    [Fact]
    public void Strength_is_the_settings_strength_and_0_when_calm_off_or_straight_up()
    {
        var settings = new WindSettings(new Vector3(3f, 1f, 4f), 0.5f);

        Assert.Equal(0.5f, Wind.Strength(settings, RenderEffects.None), 1e-6f);
        Assert.Equal(0f, Wind.Strength(settings, RenderEffects.Wind));
        Assert.Equal(0f, Wind.Strength(null, RenderEffects.None));
        Assert.Equal(0f, Wind.Strength(new WindSettings(Vector3.UnitY, 0.5f), RenderEffects.None));
    }

    [Fact]
    public void A_swaying_material_can_move_its_lean_plus_the_flutter()
    {
        // Strength 0.5 and a share of 0.4 lean a tip 0.2 at a gust's peak, and the flutter adds 0.02.
        var wind = new WindReach(0.5f, material => material.Index == 1 ? 0.4f : 0f);

        Assert.Equal(0.22f, wind.Pad(new MaterialHandle(1)), 1e-6f);
        Assert.Equal(0f, wind.Pad(new MaterialHandle(2)));
        Assert.Equal(0f, new WindReach(0f, _ => 1f).Pad(new MaterialHandle(1)));
        Assert.Equal(0f, default(WindReach).Pad(new MaterialHandle(1)));
    }

    [Fact]
    public void A_swaying_draw_gets_its_materials_share_and_its_parts_foot()
    {
        List<PartDetail> details = [default, new PartDetail(Vector3.Zero, 1f, [], MinY: 2.5f)];
        Func<MaterialHandle, float> swayOf = material => material.Index == 1 ? 0.7f : 0f;
        var swaying = new DrawItem { Material = new MaterialHandle(1), Detail = new DetailHandle(1) };

        var constants = SceneDraws.Constants(swaying, 0, swayOf, details);

        Assert.Equal(0.7f, constants.WindSway);
        Assert.Equal(2.5f, constants.WindBase);
    }

    [Fact]
    public void A_still_draw_gets_no_sway_and_no_foot()
    {
        List<PartDetail> details = [default, new PartDetail(Vector3.Zero, 1f, [], MinY: 2.5f)];
        var still = new DrawItem { Material = new MaterialHandle(2), Detail = new DetailHandle(1) };

        var constants = SceneDraws.Constants(still, 0, material => material.Index == 1 ? 0.7f : 0f, details);

        Assert.Equal(0f, constants.WindSway);
        Assert.Equal(0f, constants.WindBase);
    }

    [Fact]
    public void A_swaying_draw_without_bounds_sways_from_height_0()
    {
        List<PartDetail> details = [new PartDetail(Vector3.Zero, 1f, [], MinY: 2.5f)];
        var unbounded = new DrawItem { Material = new MaterialHandle(1) };

        var constants = SceneDraws.Constants(unbounded, 0, _ => 0.7f, details);

        Assert.Equal(0.7f, constants.WindSway);
        Assert.Equal(0f, constants.WindBase);
    }

    private static string WindShader() =>File.ReadAllText(Path.Combine(ShaderFolder(), "wind.glsl"));

    private static string ShaderFolder()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            string path = Path.Combine(folder.FullName, "src", "Rendition", "Shaders");
            if (File.Exists(Path.Combine(path, "wind.glsl")))
            {
                return path;
            }
        }

        throw new DirectoryNotFoundException("src/Rendition/Shaders/wind.glsl isn't in any folder above the tests.");
    }

    private static float ShaderConstant(string shader, string name)
    {
        var match = Regex.Match(shader, $@"const float {name} = ([0-9.eE+-]+);");
        Assert.True(match.Success, $"wind.glsl has no const float {name}.");
        return float.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    // The function's body, from its signature to the first closing brace at the start of a line.
    private static string ShaderFunction(string shader, string signature)
    {
        int start = shader.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"wind.glsl has no {signature}.");
        int end = shader.IndexOf("\n}", start, StringComparison.Ordinal);
        Assert.True(end > start, $"{signature} in wind.glsl never closes.");
        return shader[start..end];
    }
}
