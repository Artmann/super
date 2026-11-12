using System.Globalization;
using System.Numerics;
using Rendition;

namespace Super.Tests;

public class BakeInputHashTests
{
    private static readonly BakedSpace Crypt = new("crypt", new Vector3(-8f, -1f, -12f), new Vector3(8f, 6f, 12f), 1f);
    private static readonly BakedChunk House = new("house", Matrix4x4.Identity, new Vector3(-4f, 0f, -4f), new Vector3(4f, 5f, 4f));
    private static readonly ulong[] Draws = [0x8000_0000_0000_0005, 0x8000_0000_0000_0001, 0x8000_0000_0000_0009];
    private static readonly ulong[] Lights = [7, 2, 11];

    [Fact]
    public void Mixed_light_hash_ignores_colour()
    {
        var red = Torch(LightMode.Mixed);
        var blue = red;
        blue.Radiance = new Vector3(0f, 0f, 9f);

        Assert.Equal(BakeInputHash.Light(red, Matrix4x4.Identity), BakeInputHash.Light(blue, Matrix4x4.Identity));
    }

    [Fact]
    public void Baked_light_hash_includes_colour()
    {
        var red = Torch(LightMode.Baked);
        var blue = red;
        blue.Radiance = new Vector3(0f, 0f, 9f);

        Assert.NotEqual(BakeInputHash.Light(red, Matrix4x4.Identity), BakeInputHash.Light(blue, Matrix4x4.Identity));
    }

    [Theory]
    [InlineData("position")]
    [InlineData("range")]
    [InlineData("radius")]
    [InlineData("direction")]
    [InlineData("inner angle")]
    [InlineData("outer angle")]
    [InlineData("casts shadow")]
    public void Light_hash_follows_position_range_radius_and_cone(string field)
    {
        var light = Torch(LightMode.Mixed);
        var changed = light;
        switch (field)
        {
            case "position":
                changed.Position += new Vector3(0f, 0f, 0.01f);
                break;
            case "range":
                changed.Range = 7f;
                break;
            case "radius":
                changed.Radius = 0.3f;
                break;
            case "direction":
                changed.Direction = Vector3.Normalize(new Vector3(0.2f, -1f, 0f));
                break;
            case "inner angle":
                changed.InnerAngle = 0.25f;
                break;
            case "outer angle":
                changed.OuterAngle = 0.6f;
                break;
            case "casts shadow":
                changed.CastsShadow = false;
                break;
            default:
                throw new ArgumentException(field);
        }

        Assert.NotEqual(BakeInputHash.Light(light, Matrix4x4.Identity), BakeInputHash.Light(changed, Matrix4x4.Identity));
    }

    [Fact]
    public void Light_hash_ignores_key_and_fog_halo()
    {
        var light = Torch(LightMode.Baked);
        var changed = light;
        changed.Key = 99;
        changed.Volumetric = 3f;

        Assert.Equal(BakeInputHash.Light(light, Matrix4x4.Identity), BakeInputHash.Light(changed, Matrix4x4.Identity));
    }

    [Fact]
    public void Point_light_hash_ignores_the_unused_spot_direction()
    {
        var point = Torch(LightMode.Mixed);
        point.InnerAngle = 0f;
        point.OuterAngle = 0f;
        var turned = point;
        turned.Direction = Vector3.UnitX;

        Assert.Equal(BakeInputHash.Light(point, Matrix4x4.Identity), BakeInputHash.Light(turned, Matrix4x4.Identity));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-0.5f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void Light_radius_of_zero_or_less_or_not_a_size_hashes_as_the_default_radius(float radius)
    {
        var light = Torch(LightMode.Mixed);
        light.Radius = radius;
        var explicitDefault = light;
        explicitDefault.Radius = LocalLight.DefaultRadius;

        Assert.Equal(BakeInputHash.Light(light, Matrix4x4.Identity), BakeInputHash.Light(explicitDefault, Matrix4x4.Identity));
    }

    [Fact]
    public void Light_mode_is_part_of_its_hash()
    {
        Assert.NotEqual(
            BakeInputHash.Light(Torch(LightMode.Mixed), Matrix4x4.Identity),
            BakeInputHash.Light(Torch(LightMode.Baked), Matrix4x4.Identity));
    }

    [Fact]
    public void Sun_hash_follows_direction_and_mode_but_ignores_mixed_colour()
    {
        var sun = Sun(LightMode.Mixed);
        var lower = sun;
        lower.Direction = Vector3.Normalize(new Vector3(1f, 0.5f, 0f));
        var orange = sun;
        orange.Radiance = new Vector3(3f, 1f, 0f);
        var baked = sun;
        baked.Mode = LightMode.Baked;
        var bakedOrange = baked;
        bakedOrange.Radiance = new Vector3(3f, 1f, 0f);

        Assert.NotEqual(BakeInputHash.Sun(sun), BakeInputHash.Sun(lower));
        Assert.Equal(BakeInputHash.Sun(sun), BakeInputHash.Sun(orange));
        Assert.NotEqual(BakeInputHash.Sun(sun), BakeInputHash.Sun(baked));
        Assert.NotEqual(BakeInputHash.Sun(baked), BakeInputHash.Sun(bakedOrange));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(3000)]
    public void Input_hash_ignores_draw_and_light_order(int count)
    {
        var draws = new ulong[count];
        var lights = new ulong[count];
        for (int i = 0; i < count; i++)
        {
            draws[i] = 0x8000_0000_0000_0000ul | (uint)(i * 7919);
            lights[i] = (ulong)(i * 104729 + 13);
        }

        var reversedDraws = draws.Reverse().ToArray();
        var reversedLights = lights.Reverse().ToArray();
        var keptDraws = draws.ToArray();

        var space = BakeInputHash.Space(draws, lights, Crypt, 42, 5, new BakeSettings());
        var reversedSpace = BakeInputHash.Space(reversedDraws, reversedLights, Crypt, 42, 5, new BakeSettings());
        var chunk = BakeInputHash.Chunk(draws, lights, House, new BakeSettings());
        var reversedChunk = BakeInputHash.Chunk(reversedDraws, reversedLights, House, new BakeSettings());

        Assert.Equal(space.Combined, reversedSpace.Combined);
        Assert.Equal(chunk.Combined, reversedChunk.Combined);
        Assert.Equal(keptDraws, draws);
    }

    [Fact]
    public void Input_hash_is_the_same_under_sv_SE()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            ulong invariant = SwedishSensitiveHash();
            CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
            ulong swedish = SwedishSensitiveHash();

            Assert.Equal(invariant, swedish);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Chunk_hash_is_the_same_wherever_the_chunk_is_placed()
    {
        var local = Torch(LightMode.Baked);
        local.Position = new Vector3(1f, 2f, 0.5f);
        local.Direction = Vector3.Normalize(new Vector3(1f, -1f, 0f));

        var atOrigin = House;
        var placed = House with
        {
            Transform = Matrix4x4.CreateRotationY(37f * MathF.PI / 180f) * Matrix4x4.CreateTranslation(400f, 0f, -300f),
        };

        var originHash = ChunkInputs(local, atOrigin);
        var placedHash = ChunkInputs(local, placed);

        Assert.Equal(originHash.Lights, placedHash.Lights);
        Assert.Equal(originHash.Combined, placedHash.Combined);
    }

    [Fact]
    public void Chunk_name_is_not_part_of_its_hash()
    {
        var renamed = House with { Name = "cottage" };

        Assert.Equal(
            BakeInputHash.Chunk(Draws, Lights, House, new BakeSettings()).Combined,
            BakeInputHash.Chunk(Draws, Lights, renamed, new BakeSettings()).Combined);
    }

    [Theory]
    [InlineData("environment", "environment")]
    [InlineData("sun", "lights")]
    [InlineData("no sun", "lights")]
    [InlineData("probe spacing", "settings")]
    [InlineData("box", "settings")]
    public void Space_hash_follows_environment_sun_and_probe_spacing(string change, string named)
    {
        ulong environment = 42;
        ulong? sun = 5;
        var space = Crypt;
        switch (change)
        {
            case "environment":
                environment = 43;
                break;
            case "sun":
                sun = 6;
                break;
            case "no sun":
                sun = null;
                break;
            case "probe spacing":
                space = space with { ProbeSpacing = 2f };
                break;
            case "box":
                space = space with { Max = space.Max + Vector3.UnitY };
                break;
            default:
                throw new ArgumentException(change);
        }

        var before = BakeInputHash.Space(Draws, Lights, Crypt, 42, 5, new BakeSettings());
        var after = BakeInputHash.Space(Draws, Lights, space, environment, sun, new BakeSettings());

        Assert.NotEqual(before.Combined, after.Combined);
        Assert.Equal([named], after.Changes(before));
    }

    [Fact]
    public void Texel_density_changes_the_input_hash()
    {
        var before = BakeInputHash.Space(Draws, Lights, Crypt, 42, 5, new BakeSettings(20));
        var after = BakeInputHash.Space(Draws, Lights, Crypt, 42, 5, new BakeSettings(40));

        Assert.NotEqual(before.Settings, after.Settings);
        Assert.Equal(["settings"], after.Changes(before));
    }

    [Fact]
    public void Quality_does_not_change_the_input_hash()
    {
        var final = new BakeSettings(20, BakeQuality.Final);
        var preview = new BakeSettings(20, BakeQuality.Preview);

        Assert.Equal(
            BakeInputHash.Chunk(Draws, Lights, House, final),
            BakeInputHash.Chunk(Draws, Lights, House, preview));
        Assert.Equal(
            BakeInputHash.Space(Draws, Lights, Crypt, 42, 5, final).Settings,
            BakeInputHash.Space(Draws, Lights, Crypt, 42, 5, preview).Settings);
    }

    [Fact]
    public void Filtering_does_not_change_the_input_hash()
    {
        var filtered = new BakeSettings(20, filter: true);
        var raw = new BakeSettings(20, filter: false);

        Assert.Equal(
            BakeInputHash.Chunk(Draws, Lights, House, filtered),
            BakeInputHash.Chunk(Draws, Lights, House, raw));
        Assert.Equal(
            BakeInputHash.Space(Draws, Lights, Crypt, 42, 5, filtered).Settings,
            BakeInputHash.Space(Draws, Lights, Crypt, 42, 5, raw).Settings);
    }

    [Fact]
    public void Changes_names_every_part_that_moved()
    {
        var before = BakeInputHash.Space(Draws, Lights, Crypt, 42, 5, new BakeSettings());
        var after = BakeInputHash.Space([.. Draws, 0x8000_0000_0000_0010], [.. Lights, 3], Crypt, 42, 5, new BakeSettings());

        Assert.Equal(["draws", "lights"], after.Changes(before));
        Assert.Empty(before.Changes(before));
    }

    [Fact]
    public void Known_space_hash()
    {
        // Worked out once from the implementation and written in. It pins the order every field is fed in: draws
        // (count, then the sorted ids, then the count and sorted hashes of placed chunks, none here), lights (count, sorted light hashes, then 1 and the sun's hash or 0), the
        // environment as given, and settings (0 for a space, texels per metre, baker version, probe spacing,
        // min, max), then Combined over the four in that order. Reordering any of them, or changing the baker
        // version, fails here; such a change makes every saved bake stale, so it must be on purpose.
        var inputs = BakeInputHash.Space(Draws, Lights, Crypt, 42, 5, new BakeSettings());

        Assert.Equal(0x5DB0_88DE_032B_1289ul, inputs.Combined);
    }

    private static ulong SwedishSensitiveHash()
    {
        var light = Torch(LightMode.Baked);
        light.Position = new Vector3(1.5f, 2.25f, -0.75f);
        ulong lightHash = BakeInputHash.Light(light, Matrix4x4.Identity);
        ulong sunHash = BakeInputHash.Sun(Sun(LightMode.Baked));
        var space = Crypt with { ProbeSpacing = 0.5f };
        return BakeInputHash.Space(Draws, [lightHash], space, 42, sunHash, new BakeSettings()).Combined;
    }

    // The light is given in the chunk's own space, placed in the world by the chunk's transform, and handed to the
    // hash with the chunk's transform, as a frame would hand a light it finds inside a placed chunk.
    private static BakeInputs ChunkInputs(LocalLight local, BakedChunk chunk)
    {
        var world = local;
        world.Position = Vector3.Transform(local.Position, chunk.Transform);
        world.Direction = Vector3.TransformNormal(local.Direction, chunk.Transform);
        ulong light = BakeInputHash.Light(world, chunk.Transform);
        return BakeInputHash.Chunk(Draws, [light], chunk, new BakeSettings());
    }

    private static LocalLight Torch(LightMode mode) => new()
    {
        Mode = mode,
        Key = 1,
        Position = new Vector3(1f, 2f, 3f),
        Radiance = new Vector3(5f, 4f, 3f),
        Range = 6f,
        Radius = 0.2f,
        Direction = new Vector3(0f, -1f, 0f),
        InnerAngle = 0.3f,
        OuterAngle = 0.5f,
        CastsShadow = true,
    };

    private static DirectionalLight Sun(LightMode mode) => new()
    {
        Mode = mode,
        Direction = Vector3.Normalize(new Vector3(0.3f, 1f, 0.2f)),
        Radiance = new Vector3(3f, 3f, 2.8f),
        CastsShadow = true,
    };
}
