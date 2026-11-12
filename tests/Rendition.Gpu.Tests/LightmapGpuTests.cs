using System.Numerics;

namespace Rendition.Gpu.Tests;

/// <summary>
/// Lightmaps on surfaces: a plane baked under a sky, then drawn with its bake loaded, so its soft light comes from the
/// lightmap rather than the live sky. Every effect is off but the baked lighting, so nothing carried from frame to frame
/// makes two captures differ, and each capture lands on the same frame of the dither's cycle.
/// </summary>
[Collection(GpuCollection.Name)]
[Trait("Speed", "Slow")]
[Trait("Needs", "GPU")]
public sealed class LightmapGpuTests(GpuFixture gpu)
{
    private const int Size = 256;
    private const int NoisePeriod = 64;
    private const int TexelsPerMetre = 8;
    private const RenderEffects OnlyBaked = RenderEffects.All & ~RenderEffects.BakedLighting;

    private static readonly BakedSpace Room =
        new("lightmap-room", new Vector3(-6f, -3f, -6f), new Vector3(6f, 7f, 6f), 1f, TexelsPerMetre);

    // A sky of 0.5 in every direction.
    private static EnvironmentDescription EvenSky() => new()
    {
        Ceiling = new Vector3(0.5f),
        Horizon = new Vector3(0.5f),
        Floor = new Vector3(0.5f),
    };

    // The renderer's usual environment, put back after each test.
    private static EnvironmentDescription DefaultEnvironment() => new()
    {
        Ceiling = new Vector3(0.5f),
        Horizon = new Vector3(0.4f),
        Floor = new Vector3(0.2f),
    };

    [Fact]
    public void A_lightmapped_plane_shows_its_baked_light()
    {
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        try
        {
            gpu.Renderer.SetEnvironment(EvenSky());
            var plane = Plane(quad, gpu.Matte);
            var bake = Bake(plane);

            // With the sky off and nothing baked, nothing lights the plane.
            var dark = Centre(Capture(plane, sky: 0f));

            // Live, the plane under a sky of L = 0.5 takes pi L of irradiance, and gives back albedo * L = 0.25. The bake
            // holds the same pi L, so with the sky off it gives the same diffuse light. Only the sky's faint reflection
            // on the matte grey, about a twentieth of the light, is missing.
            var live = Centre(Capture(plane, sky: 1f));
            gpu.Renderer.Bakes = new Loaded(bake);
            var baked = Centre(Capture(plane, sky: 0f));

            Assert.True(dark.X < 2f, $"The unlit plane reads {dark}, not black.");
            Assert.True(baked.X > 40f, $"The lightmapped plane reads {baked}, as dark as the unlit one, {dark}.");
            AssertWithin(live.X, baked.X, 0.1f, "red of the lightmapped plane");
            AssertWithin(live.Y, baked.Y, 0.1f, "green of the lightmapped plane");
            AssertWithin(live.Z, baked.Z, 0.1f, "blue of the lightmapped plane");
        }
        finally
        {
            gpu.Renderer.Bakes = NoBakes.Instance;
            gpu.Renderer.SetEnvironment(DefaultEnvironment());
            gpu.Renderer.DestroyMesh(quad);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Without_a_bake_nothing_changes()
    {
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        try
        {
            gpu.Renderer.SetEnvironment(EvenSky());
            var plane = Plane(quad, gpu.Matte);

            // The store has no file at all, so the space is lit live and the plane's draw has no lightmap.
            gpu.Renderer.Bakes = new LightBakeStore(_ => null, TextWriter.Null);
            byte[] on = Capture(plane, sky: 1f);
            byte[] off = Capture(plane, sky: 1f, off: RenderEffects.All);
            byte[] onAgain = Capture(plane, sky: 1f);

            // The frame repeats, so a difference below can only come from the switch.
            Assert.Equal(on, onAgain);
            Assert.Equal(off, on);

            // And the sky still lights the plane, so a draw with no lightmap isn't taken for one with no baked light.
            Assert.True(Centre(on).X > 40f, $"The unbaked plane read {Centre(on)}, not lit by the sky.");
        }
        finally
        {
            gpu.Renderer.Bakes = NoBakes.Instance;
            gpu.Renderer.SetEnvironment(DefaultEnvironment());
            gpu.Renderer.DestroyMesh(quad);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Turning_baked_lighting_off_falls_back_to_probes()
    {
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        try
        {
            gpu.Renderer.SetEnvironment(EvenSky());
            var plane = Plane(quad, gpu.Matte);
            var bake = Bake(plane);

            // Drawn at half the sky the bake was made under, so the live light and the baked light differ.
            byte[] live = Capture(plane, sky: 0.5f);
            gpu.Renderer.Bakes = new Loaded(bake);
            byte[] baked = Capture(plane, sky: 0.5f);
            byte[] off = Capture(plane, sky: 0.5f, off: RenderEffects.All);

            Assert.NotEqual(live, baked);
            Assert.Equal(live, off);
        }
        finally
        {
            gpu.Renderer.Bakes = NoBakes.Instance;
            gpu.Renderer.SetEnvironment(DefaultEnvironment());
            gpu.Renderer.DestroyMesh(quad);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void A_material_mask_still_reads_uv2()
    {
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        // Four texels across: the left two say convex and fully worn, the right two flat, so wear shows on the left of
        // the plane only, where the mask is read through its uv2 (u below 0.5).
        byte[] pixels =
        [
            255, 255, 255, 255, 255, 255, 255, 255,
            128, 255, 255, 255, 128, 255, 255, 255,
        ];
        var mask = gpu.Renderer.CreateTexture(TextureData.FromPixels(4, 1, TextureKind.Linear, pixels), TextureEdges.Clamp);
        var worn = gpu.Renderer.CreateMaterial(new Material
        {
            BaseColor = new Vector4(0.5f, 0.5f, 0.5f, 1f),
            Roughness = 0.8f,
            MaskTexture = mask,
            // Grey 0.5 times twice this is (1, 0.1, 0.1): red where it wears.
            Wear = new MaterialLayer(new Vector3(1f, 0.1f, 0.1f), -1f, 1f),
        });
        try
        {
            gpu.Renderer.SetEnvironment(EvenSky());
            var plane = Plane(quad, worn);
            var bake = Bake(plane);

            // Wear and the materials that carry it are effects, so they are left on.
            const RenderEffects off = OnlyBaked & ~RenderEffects.WearAndDirt & ~RenderEffects.Materials;
            byte[] live = Capture(plane, sky: 1f, off);
            gpu.Renderer.Bakes = new Loaded(bake);
            byte[] baked = Capture(plane, sky: 1f, off);

            // u = 0.2 and 0.8 lie at x = -0.6 and 0.6 on the plane, which from 3 metres away with a 45 degree view is
            // 0.6 / (3 tan 22.5) = 0.483 of the half width either side of the centre.
            foreach (var (frame, name) in new[] { (live, "live"), (baked, "baked") })
            {
                var left = Pixel(frame, 0.5f - 0.483f * 0.5f, 0.5f);
                var right = Pixel(frame, 0.5f + 0.483f * 0.5f, 0.5f);
                Assert.True(left.X > 2f * left.Y, $"The worn left of the {name} plane reads {left}, not red.");
                Assert.True(MathF.Abs(right.X - right.Y) <= 0.1f * right.Y,
                    $"The clean right of the {name} plane reads {right}, not grey.");
            }
        }
        finally
        {
            gpu.Renderer.Bakes = NoBakes.Instance;
            gpu.Renderer.SetEnvironment(DefaultEnvironment());
            gpu.Renderer.DestroyMesh(quad);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void A_fresh_bake_changes_the_reflection_lighting_hash()
    {
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        try
        {
            gpu.Renderer.SetEnvironment(EvenSky());
            var plane = Plane(quad, gpu.Matte);
            var bake = Bake(plane);
            var view = View(sky: 1f, OnlyBaked);

            gpu.Renderer.Render(view, [plane]);
            int live = gpu.Renderer.ReflectionLightingHash(view);
            gpu.Renderer.Bakes = new Loaded(bake);
            gpu.Renderer.Render(view, [plane]);
            int baked = gpu.Renderer.ReflectionLightingHash(view);
            gpu.Renderer.Bakes = NoBakes.Instance;
            gpu.Renderer.Render(view, [plane]);
            int liveAgain = gpu.Renderer.ReflectionLightingHash(view);

            Assert.NotEqual(live, baked);
            Assert.Equal(live, liveAgain);
        }
        finally
        {
            gpu.Renderer.Bakes = NoBakes.Instance;
            gpu.Renderer.SetEnvironment(DefaultEnvironment());
            gpu.Renderer.DestroyMesh(quad);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Light_from_behind_the_vertex_normal_is_not_turned_up()
    {
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        // A normal map leaning 60 degrees towards the quad's tangent, +x: n = (sin 60, 0, cos 60) = (0.866, 0, 0.5),
        // packed as n * 0.5 + 0.5 = (238, 128, 191).
        var normals = gpu.Renderer.CreateTexture(
            TextureData.FromPixels(4, 4, TextureKind.Linear, Fill(16, 238, 128, 191), normalMap: true), TextureEdges.Clamp);
        var leaning = gpu.Renderer.CreateMaterial(new Material
        {
            BaseColor = new Vector4(0.5f, 0.5f, 0.5f, 1f),
            Roughness = 1f,
            NormalTexture = normals,
        });
        try
        {
            gpu.Renderer.SetEnvironment(EvenSky());
            var plane = Plane(quad, leaning);
            var bake = Bake(plane);
            // Normal maps are a material effect, so it is left on.
            const RenderEffects off = OnlyBaked & ~RenderEffects.Materials;

            // d packed as 128 is 0.004 on each axis, near nothing, as under an even sky: the light is turned by 1.004.
            gpu.Renderer.Bakes = new Loaded(WithDirection(bake, 128, 128, 128));
            var even = Centre(Capture(plane, sky: 0f, off));

            // (166, 128, 6) is d = (0.302, 0.004, -0.953), behind the vertex normal +z, as a gutter texel or a smooth
            // normal can give. n.d = 0.867 * 0.302 + 0.498 * -0.953 = -0.213 and v.d = -0.953, so the light would be
            // turned by (-0.213 * 0.5 + 0.5) / (-0.953 * 0.5 + 0.5) = 0.394 / 0.0235 = 16.7. With the vertex normal's
            // share floored at 0.5 it is 0.394 / 0.5 = 0.787: a little darker than the even light, not 17 times brighter.
            gpu.Renderer.Bakes = new Loaded(WithDirection(bake, 166, 128, 6));
            var behind = Centre(Capture(plane, sky: 0f, off));

            Assert.True(even.X > 40f, $"The evenly lit plane reads {even}, unlit.");
            Assert.True(behind.X < even.X, $"Light from behind reads {behind}, brighter than the even light, {even}.");
            // 0.787 of the light shows as more than three quarters of the pixel, since sRGB lifts dark values.
            Assert.True(behind.X > 0.75f * even.X, $"Light from behind reads {behind}, much darker than {even}.");
        }
        finally
        {
            gpu.Renderer.Bakes = NoBakes.Instance;
            gpu.Renderer.SetEnvironment(DefaultEnvironment());
            gpu.Renderer.DestroyMesh(quad);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Theory]
    [InlineData("damaged")]
    [InlineData("page out of range")]
    public void An_unreadable_bake_is_lit_live_said_once_and_read_again_after_a_reload(string fault)
    {
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        var error = Console.Error;
        var log = new StringWriter();
        try
        {
            gpu.Renderer.SetEnvironment(EvenSky());
            var plane = Plane(quad, gpu.Matte);
            // A Baked lamp a metre in front of the plane, the only light with the sky off.
            LocalLight[] lamp =
            [
                new() { Mode = LightMode.Baked, Key = 1701, Position = new Vector3(0f, 0f, 1f), Radiance = new Vector3(2f), Range = 5f },
            ];
            Func<LightBakeFile> load;
            string reason;
            if (fault == "damaged")
            {
                load = () => throw new InvalidDataException("The bake is cut off.");
                reason = "The bake is cut off.";
            }
            else
            {
                var bake = Bake(plane);
                var misplaced = bake with { Draws = [(plane.BakeId, bake.Draws[0].Rect with { Page = 5 })] };
                load = () => misplaced;
                reason = $"Draw 0x{plane.BakeId:X16} is on page 5, but the bake has {bake.Irradiance.Count} pages.";
            }

            byte[] live = Capture(plane, sky: 0f, lights: lamp);

            // A fresh bake that can be read takes the lamp out of the live lighting. A whole capture keeps the
            // noise in step with the other two.
            gpu.Renderer.Bakes = new FreshWithoutLightmaps();
            Capture(plane, sky: 0f, lights: lamp);
            Assert.Equal([0], gpu.Renderer.LastBakeFrame!.DroppedLocalLights);

            Console.SetError(log);
            var store = new Unreadable(load);
            gpu.Renderer.Bakes = store;
            byte[] unreadable = Capture(plane, sky: 0f, lights: lamp);

            // Over 64 frames it is read once and said once, and the lamp is back.
            Assert.True(Centre(live).X > 2f, $"The lamp leaves the plane at {Centre(live)}, unlit.");
            Assert.Equal(live, unreadable);
            Assert.Equal(1, store.Loads);
            Assert.Equal([$"The bake lighting/lightmap-room.lightbake can't be read, so its area is lit live: {reason}"], Lines(log));

            // Once the store's files may have changed, it is read again, and the frame that finds it still unreadable
            // keeps the lamp.
            store.Reloads++;
            gpu.Renderer.Render(View(0f, OnlyBaked, lamp), [plane]);
            Assert.Equal(2, store.Loads);
            Assert.Equal(2, Lines(log).Length);
            Assert.False(gpu.Renderer.LastBakeFrame!.SpaceFresh);
            Assert.Empty(gpu.Renderer.LastBakeFrame.DroppedLocalLights);
        }
        finally
        {
            Console.SetError(error);
            gpu.Renderer.Bakes = NoBakes.Instance;
            gpu.Renderer.SetEnvironment(DefaultEnvironment());
            gpu.Renderer.DestroyMesh(quad);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void A_cooked_bake_lights_the_plane_as_the_bake_it_was_cooked_from()
    {
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        try
        {
            gpu.Renderer.SetEnvironment(EvenSky());
            var plane = Plane(quad, gpu.Matte);
            var bake = Bake(plane);
            var cooked = LightBakeFile.Read(Super.Cook.LightBakeCooker.Compress(bake).ToBytes());
            Assert.Equal(LightBakePageFormat.Bc6h, cooked.Irradiance[0].Format);
            Assert.Equal(LightBakePageFormat.Bc7, cooked.Direction[0].Format);

            gpu.Renderer.Bakes = new Loaded(bake);
            var raw = Centre(Capture(plane, sky: 0f));
            gpu.Renderer.Bakes = new Loaded(cooked);
            var fromCooked = Centre(Capture(plane, sky: 0f));

            // BC6H keeps about 3 significant figures of an even light, and BC7 the direction to a byte or two.
            Assert.True(raw.X > 40f, $"The lightmapped plane reads {raw}, unlit.");
            AssertWithin(raw.X, fromCooked.X, 0.03f, "red from the cooked bake");
            AssertWithin(raw.Y, fromCooked.Y, 0.03f, "green from the cooked bake");
            AssertWithin(raw.Z, fromCooked.Z, 0.03f, "blue from the cooked bake");
        }
        finally
        {
            gpu.Renderer.Bakes = NoBakes.Instance;
            gpu.Renderer.SetEnvironment(DefaultEnvironment());
            gpu.Renderer.DestroyMesh(quad);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    // The bake with every direction page one colour of RGBA8.
    private static LightBakeFile WithDirection(LightBakeFile bake, byte x, byte y, byte z) => bake with
    {
        Direction = [.. bake.Direction.Select(page =>
            new LightBakePage(page.Width, page.Height, LightBakePageFormat.Rgba8, Fill(page.Width * page.Height, x, y, z)))],
    };

    // Pixels of one RGBA8 colour, alpha 255.
    private static byte[] Fill(int pixels, byte x, byte y, byte z)
    {
        byte[] bytes = new byte[pixels * 4];
        for (int i = 0; i < bytes.Length; i += 4)
        {
            bytes[i] = x;
            bytes[i + 1] = y;
            bytes[i + 2] = z;
            bytes[i + 3] = 255;
        }

        return bytes;
    }

    private static string[] Lines(StringWriter log) =>
        log.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

    // The test quad, 2 metres square, facing the camera, static in the room.
    private static DrawItem Plane(MeshHandle mesh, MaterialHandle material) => new()
    {
        Mesh = mesh,
        IndexCount = 6,
        Material = material,
        Transform = Matrix4x4.CreateScale(2f),
        // An id keeps an instance hash's top 52 bits, so small numbers are moved up past the part's 12.
        BakeId = BakeIds.Of(1ul << 12, 0),
    };

    // Bakes the plane under the sky at full strength.
    private LightBakeFile Bake(DrawItem plane)
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        var view = View(sky: 1f, OnlyBaked);
        return LightBakeFile.Read(gpu.Renderer.Bake(view, [plane], new BakeSettings(TexelsPerMetre, BakeQuality.Preview)));
    }

    private MeshHandle CreateQuad()
    {
        var (vertices, indices) = TestMeshes.Quad();
        for (int i = 0; i < vertices.Length; i++)
        {
            vertices[i].Uv2 = vertices[i].Uv;
        }

        var handle = gpu.Renderer.CreateMesh(vertices, indices);
        gpu.RenderOneFrame(); // Uploads the mesh.
        return handle;
    }

    private static FrameView View(float sky, RenderEffects off, LocalLight[]? lights = null) => new()
    {
        Width = Size,
        Height = Size,
        View = Matrix4x4.CreateLookAt(new Vector3(0, 0, 3), Vector3.Zero, Vector3.UnitY),
        Projection = Projection.PerspectiveInfiniteReversedZ(MathF.PI / 4f, 1f, 0.1f),
        CameraPosition = new Vector3(0, 0, 3),
        EnvironmentIntensity = sky,
        Off = off,
        BakedSpace = Room,
        LocalLights = lights ?? [],
    };

    // The 64th frame of the plane under the sky at the strength given, so two captures fall on the same noise.
    private byte[] Capture(DrawItem plane, float sky, RenderEffects off = OnlyBaked, LocalLight[]? lights = null)
    {
        var view = View(sky, off, lights);
        for (int i = 0; i < NoisePeriod - 1; i++)
        {
            gpu.Renderer.Render(view, [plane]);
        }

        return gpu.Renderer.Capture(view, [plane], out _, out _);
    }

    private static Vector3 Centre(byte[] frame) => Pixel(frame, 0.5f, 0.5f);

    // The pixel at a share of the width and height, as 0 to 255.
    private static Vector3 Pixel(byte[] frame, float x, float y)
    {
        int at = ((int)(y * Size) * Size + (int)(x * Size)) * 4;
        return new Vector3(frame[at], frame[at + 1], frame[at + 2]);
    }

    private static void AssertWithin(float expected, float actual, float share, string what)
    {
        Assert.True(MathF.Abs(actual - expected) <= share * MathF.Abs(expected),
            $"The {what} is {actual}, not within {share:P0} of {expected}.");
    }

    // A store that holds one bake and calls it fresh for every area, whatever the frame's inputs.
    private sealed class Loaded(LightBakeFile bake) : IBakeStore
    {
        public bool IsFresh(string file, BakeInputs inputs) => true;

        public LightBakeFile? Load(string file) => bake;
    }

    // A store that calls every area fresh but has no lightmaps to give.
    private sealed class FreshWithoutLightmaps : IBakeStore
    {
        public bool IsFresh(string file, BakeInputs inputs) => true;
    }

    // A store that calls every area fresh, then fails to load it, counting the tries.
    private sealed class Unreadable(Func<LightBakeFile> load) : IBakeStore
    {
        public int Loads { get; private set; }

        public int Reloads { get; set; }

        public bool IsFresh(string file, BakeInputs inputs) => true;

        public LightBakeFile? Load(string file)
        {
            Loads++;
            return load();
        }
    }
}
