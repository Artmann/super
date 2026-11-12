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

    private static FrameView View(float sky, RenderEffects off) => new()
    {
        Width = Size,
        Height = Size,
        View = Matrix4x4.CreateLookAt(new Vector3(0, 0, 3), Vector3.Zero, Vector3.UnitY),
        Projection = Projection.PerspectiveInfiniteReversedZ(MathF.PI / 4f, 1f, 0.1f),
        CameraPosition = new Vector3(0, 0, 3),
        EnvironmentIntensity = sky,
        Off = off,
        BakedSpace = Room,
    };

    // The 64th frame of the plane under the sky at the strength given, so two captures fall on the same noise.
    private byte[] Capture(DrawItem plane, float sky, RenderEffects off = OnlyBaked)
    {
        var view = View(sky, off);
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
}
