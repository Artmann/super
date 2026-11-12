using System.Numerics;
using Rendition.Vulkan;

namespace Rendition.Gpu.Tests;

/// <summary>
/// A space's baked probes on what has no lightmap. A red wall, static and lit by a Baked sun, is baked with the space;
/// then the sun is gone, so only the bake still knows the wall's red light. A dynamic quad, which no bake covers, takes
/// it from the bake's probes inside the space's box, from the live probes where none of the bake's sees it, and
/// from the live lighting outside the box. Every effect is off but the baked lighting and the live probes, and the
/// live probes are traced and so a little noisy, so frames lit by them are compared within a few levels.
/// </summary>
[Collection(GpuCollection.Name)]
[Trait("Speed", "Slow")]
[Trait("Needs", "GPU")]
public sealed class BakedProbeGpuTests(GpuFixture gpu)
{
    private const int Size = 256;
    private const int NoisePeriod = 64;
    private const int TexelsPerMetre = 4;
    private const RenderEffects OnlyBaked = RenderEffects.All & ~RenderEffects.BakedLighting;

    // Probes a metre apart, x = -4 to 4 and so on.
    private static readonly BakedSpace Room =
        new("baked-probe-room", new Vector3(-4f, -4f, -4f), new Vector3(4f, 4f, 4f), 1f, TexelsPerMetre);

    // The sun shines along +z, onto the wall's front, which faces -z: towards it is -z.
    private static readonly DirectionalLight[] WallSun =
    [
        new() { Mode = LightMode.Baked, Direction = -Vector3.UnitZ, Radiance = new Vector3(3f) },
    ];

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
    public void A_dynamic_quad_in_the_space_reads_baked_probes()
    {
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        var red = gpu.Renderer.CreateMaterial(new Material { BaseColor = new Vector4(0.8f, 0.05f, 0.05f, 1f), Roughness = 1f });
        try
        {
            var wall = Wall(quad, red);
            // A grey quad at the centre of the space, facing the camera and the wall behind it.
            var dynamic = Dynamic(quad, Matrix4x4.CreateScale(2f));
            var bake = Bake(wall);
            var camera = new Vector3(0f, 0f, 3f);

            // With no sun and no sky, nothing live lights the quad.
            var live = Centre(Capture([wall, dynamic], camera, sky: 0f));
            gpu.Renderer.Bakes = new Loaded(bake);
            var baked = Centre(Capture([wall, dynamic], camera, sky: 0f));
            var switchedOff = Centre(Capture([wall, dynamic], camera, sky: 0f, RenderEffects.All));

            // The probes between the quad and the wall saw the sunlit red wall, which filled most of their view
            // towards +z, and nothing else: the bake had no sky. So the quad takes red light only.
            Assert.True(live.X < 2f, $"The quad with nothing live to light it reads {live}, not black.");
            Assert.True(baked.X > 20f, $"The quad in the baked space reads {baked}, as dark as the unlit one, {live}.");
            Assert.True(baked.X > 3f * baked.Y && baked.X > 3f * baked.Z, $"The quad in the baked space reads {baked}, not red.");
            Assert.True(switchedOff.X < 2f, $"With baked lighting off the quad reads {switchedOff}, still lit by the bake.");
        }
        finally
        {
            gpu.Renderer.Bakes = NoBakes.Instance;
            gpu.Renderer.SetEnvironment(DefaultEnvironment());
            gpu.Renderer.DestroyMesh(quad);
            gpu.Renderer.DestroyMaterial(red);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Outside_the_space_the_live_lighting_is_used()
    {
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        var red = gpu.Renderer.CreateMaterial(new Material { BaseColor = new Vector4(0.8f, 0.05f, 0.05f, 1f), Roughness = 1f });
        try
        {
            var wall = Wall(quad, red);
            var bake = Bake(wall);

            // Half a metre square at x = 4.5, all of it outside the box, which ends at 4, but within a probe spacing of
            // its last probes, at x = 4: they would still weigh 1 - 0.5 = 0.5 there if the box didn't stop them.
            var dynamic = Dynamic(quad, Matrix4x4.CreateScale(0.5f) * Matrix4x4.CreateTranslation(4.5f, 0f, 0f));
            var camera = new Vector3(4.5f, 0f, 1.5f);
            gpu.Renderer.SetEnvironment(EvenSky());

            byte[] live = Capture([wall, dynamic], camera, sky: 1f);
            gpu.Renderer.Bakes = new Loaded(bake);
            byte[] baked = Capture([wall, dynamic], camera, sky: 1f);

            // The sky lights the quad, the same whether the space is baked or not.
            Assert.True(gpu.Renderer.LastBakeFrame!.SpaceFresh, "The bake isn't drawn, so the test shows nothing.");
            Assert.True(Centre(live).X > 40f, $"The quad outside the space reads {Centre(live)}, not lit by the sky.");
            Assert.Equal(live, baked);
        }
        finally
        {
            gpu.Renderer.Bakes = NoBakes.Instance;
            gpu.Renderer.SetEnvironment(DefaultEnvironment());
            gpu.Renderer.DestroyMesh(quad);
            gpu.Renderer.DestroyMaterial(red);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Outside_the_space_live_probes_are_used()
    {
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        var red = gpu.Renderer.CreateMaterial(new Material { BaseColor = new Vector4(0.8f, 0.05f, 0.05f, 1f), Roughness = 1f });
        var green = gpu.Renderer.CreateMaterial(new Material { BaseColor = new Vector4(0.05f, 0.8f, 0.05f, 1f), Roughness = 1f });
        try
        {
            var bake = Bake(Wall(quad, red));

            // The same grey quad as above, just outside the box. Behind the camera, a 4 metre green wall, dynamic and all
            // of it outside the box too (x = 4.5 to 8.5), fills much of what the live probes see towards +z, so they
            // give the quad a green cast the sky alone can't. The baked red wall is left out of the frame: its lightmap
            // holds the bake's sun, so the live probes would see it lit differently with the bake loaded and without.
            var dynamic = Dynamic(quad, Matrix4x4.CreateScale(0.5f) * Matrix4x4.CreateTranslation(4.5f, 0f, 0f));
            var greenWall = Dynamic(quad, Matrix4x4.CreateScale(4f) * Matrix4x4.CreateRotationY(MathF.PI)
                * Matrix4x4.CreateTranslation(6.5f, 0f, 2.5f)) with { Material = green };
            DrawItem[] draws = [dynamic, greenWall];
            var camera = new Vector3(4.5f, 0f, 1.5f);
            // 3 x 3 x 3 probes, x = 4.2 to 6.2, round the quad and clear of the box.
            var probes = new ProbeVolumeSettings(new Vector3(4.2f, -1f, -1f), new Vector3(6.2f, 1f, 1f), 1f);
            // Materials on too, or the live probes would see the green wall as grey clay.
            const RenderEffects off = OnlyBaked & ~RenderEffects.LightProbes & ~RenderEffects.Materials;
            gpu.Renderer.SetEnvironment(EvenSky());

            var skyOnly = Centre(Capture(draws, camera, sky: 1f, off));
            byte[] live = Capture(draws, camera, sky: 1f, off, probes);
            gpu.Renderer.Bakes = new Loaded(bake);
            byte[] baked = Capture(draws, camera, sky: 1f, off, probes);

            // The live probes light the quad, not the sky alone: greener than the sky's grey.
            var fromProbes = Centre(live);
            Assert.True(gpu.Renderer.LastBakeFrame!.SpaceFresh, "The bake isn't drawn, so the test shows nothing.");
            Assert.True(fromProbes.Y > 20f, $"The quad lit by the live probes reads {fromProbes}, unlit.");
            Assert.True(fromProbes.Y - fromProbes.X > skyOnly.Y - skyOnly.X + 10f,
                $"The quad lit by the live probes reads {fromProbes}, no greener than under the sky alone, {skyOnly}.");
            AssertAlike(live, baked);
        }
        finally
        {
            gpu.Renderer.Bakes = NoBakes.Instance;
            gpu.Renderer.SetEnvironment(DefaultEnvironment());
            gpu.Renderer.DestroyMesh(quad);
            gpu.Renderer.DestroyMaterial(red);
            gpu.Renderer.DestroyMaterial(green);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void In_the_space_where_no_baked_probe_sees_live_probes_are_used()
    {
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        var red = gpu.Renderer.CreateMaterial(new Material { BaseColor = new Vector4(0.8f, 0.05f, 0.05f, 1f), Roughness = 1f });
        var green = gpu.Renderer.CreateMaterial(new Material { BaseColor = new Vector4(0.05f, 0.8f, 0.05f, 1f), Roughness = 1f });
        try
        {
            var bake = Bake(Wall(quad, red));
            var walledIn = EveryProbeInside(bake);

            // A grey quad at the centre of the space, with a 4 metre green wall behind the camera, dynamic, which fills
            // much of what the live probes see towards +z. The baked red wall is left out of the frame, as above.
            var dynamic = Dynamic(quad, Matrix4x4.CreateScale(0.5f));
            var greenWall = Dynamic(quad, Matrix4x4.CreateScale(4f) * Matrix4x4.CreateRotationY(MathF.PI)
                * Matrix4x4.CreateTranslation(0f, 0f, 2.5f)) with { Material = green };
            DrawItem[] draws = [dynamic, greenWall];
            var camera = new Vector3(0f, 0f, 1.5f);
            // 3 x 3 x 3 live probes round the quad, all inside the baked space.
            var probes = new ProbeVolumeSettings(new Vector3(-1f, -1f, -1f), new Vector3(1f, 1f, 1f), 1f);
            const RenderEffects off = OnlyBaked & ~RenderEffects.LightProbes & ~RenderEffects.Materials;
            gpu.Renderer.SetEnvironment(EvenSky());

            var skyOnly = Centre(Capture(draws, camera, sky: 1f, off));
            byte[] live = Capture(draws, camera, sky: 1f, off, probes);
            gpu.Renderer.Bakes = new Loaded(bake);
            var fromBake = Centre(Capture(draws, camera, sky: 1f, off, probes));
            gpu.Renderer.Bakes = new Loaded(walledIn);
            byte[] fellBack = Capture(draws, camera, sky: 1f, off, probes);

            // With its probes able to see, the bake wins over the live probes: the quad takes the red wall's light,
            // not the green. With every baked probe inside a wall, none of them sees the quad, so the live probes
            // light it as with no bake at all.
            var fromProbes = Centre(live);
            Assert.True(gpu.Renderer.LastBakeFrame!.SpaceFresh, "The bake isn't drawn, so the test shows nothing.");
            Assert.True(fromProbes.Y > 20f && fromProbes.Y > fromProbes.X + 10f,
                $"The quad lit by the live probes reads {fromProbes}, not green.");
            Assert.True(fromBake.X > fromBake.Y, $"The quad over a bake that sees reads {fromBake}, not the bake's red.");
            AssertAlike(live, fellBack);
        }
        finally
        {
            gpu.Renderer.Bakes = NoBakes.Instance;
            gpu.Renderer.SetEnvironment(DefaultEnvironment());
            gpu.Renderer.DestroyMesh(quad);
            gpu.Renderer.DestroyMaterial(red);
            gpu.Renderer.DestroyMaterial(green);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    // The same bake with every probe marked inside a wall, as the fold marks one: the second moment of the first texel
    // of its depth tile negative. probes.glsl leaves such a probe out.
    private static LightBakeFile EveryProbeInside(LightBakeFile bake)
    {
        var depth = bake.ProbeDepth!;
        var pixels = (byte[])depth.Pixels.Clone();
        var halves = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Half>(pixels.AsSpan());
        const int tile = ProbeVolume.DepthTexels + 2;
        long count = ProbeGrid.Total(bake.Probes!.Value);
        for (int probe = 0; probe < count; probe++)
        {
            int x = probe % BakedProbeAtlas.Columns * tile;
            int y = probe / BakedProbeAtlas.Columns * tile;
            halves[(x + y * depth.Width) * 4 + 1] = (Half)(-1f);
        }

        return bake with { ProbeDepth = depth with { Pixels = pixels } };
    }

    // An 8 metre red wall at z = 3.5, inside the space, turned to face -z. Static, so the bake covers it.
    private static DrawItem Wall(MeshHandle mesh, MaterialHandle material) => new()
    {
        Mesh = mesh,
        IndexCount = 6,
        Material = material,
        Transform = Matrix4x4.CreateScale(8f) * Matrix4x4.CreateRotationY(MathF.PI) * Matrix4x4.CreateTranslation(0f, 0f, 3.5f),
        // An id keeps an instance hash's top 52 bits, so small numbers are moved up past the part's 12.
        BakeId = BakeIds.Of(1ul << 12, 0),
    };

    // A matte grey quad facing +z with no bake id, so no bake covers it.
    private DrawItem Dynamic(MeshHandle mesh, Matrix4x4 transform) => new()
    {
        Mesh = mesh,
        IndexCount = 6,
        Material = gpu.Matte,
        Transform = transform,
    };

    // Bakes the wall under its sun, with no sky.
    private LightBakeFile Bake(DrawItem wall)
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");

        // The renderer is shared, so a sun another test drew differently at the same index would count as moving, and
        // the bake would leave it live. A frame without lights forgets them all.
        gpu.Renderer.Render(View(Vector3.UnitZ * 3f, sky: 0f, OnlyBaked, []), [wall]);
        var view = View(Vector3.UnitZ * 3f, sky: 0f, OnlyBaked, WallSun);
        var bake = LightBakeFile.Read(gpu.Renderer.Bake(view, [wall], new BakeSettings(TexelsPerMetre, BakeQuality.Preview)));
        Assert.NotNull(bake.Probes);
        return bake;
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

    // Looking down -z from the camera.
    private static FrameView View(
        Vector3 camera, float sky, RenderEffects off, DirectionalLight[] suns, ProbeVolumeSettings? probes = null) => new()
    {
        Width = Size,
        Height = Size,
        View = Matrix4x4.CreateLookAt(camera, camera with { Z = 0f }, Vector3.UnitY),
        Projection = Projection.PerspectiveInfiniteReversedZ(MathF.PI / 4f, 1f, 0.1f),
        CameraPosition = camera,
        EnvironmentIntensity = sky,
        Off = off,
        BakedSpace = Room,
        Lights = suns,
        LightProbes = probes,
    };

    // The 64th frame, with no lights, so two captures fall on the same noise.
    private byte[] Capture(
        DrawItem[] draws, Vector3 camera, float sky, RenderEffects off = OnlyBaked, ProbeVolumeSettings? probes = null)
    {
        var view = View(camera, sky, off, [], probes);
        for (int i = 0; i < NoisePeriod - 1; i++)
        {
            gpu.Renderer.Render(view, draws);
        }

        return gpu.Renderer.Capture(view, draws, out _, out _);
    }

    // Two frames lit by the live probes, the same but for noise. The probes trace new directions every frame, so the
    // same view a few dozen frames later is a step or two of 255 off here and there; what the bake would change is
    // far more.
    private static void AssertAlike(byte[] expected, byte[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        int worst = 0;
        int at = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            int difference = Math.Abs(expected[i] - actual[i]);
            if (difference > worst)
            {
                worst = difference;
                at = i;
            }
        }

        Assert.True(worst <= 3, $"The frames differ by {worst} at byte {at}: {expected[at]} against {actual[at]}.");
    }

    private static Vector3 Centre(byte[] frame)
    {
        int at = ((Size / 2) * Size + Size / 2) * 4;
        return new Vector3(frame[at], frame[at + 1], frame[at + 2]);
    }

    // A store that holds one bake and calls it fresh for every area, whatever the frame's inputs.
    private sealed class Loaded(LightBakeFile bake) : IBakeStore
    {
        public bool IsFresh(string file, BakeInputs inputs) => true;

        public LightBakeFile? Load(string file) => bake;
    }
}
