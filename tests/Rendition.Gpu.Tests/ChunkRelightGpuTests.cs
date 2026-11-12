using System.Numerics;

namespace Rendition.Gpu.Tests;

/// <summary>
/// Relighting a chunk's copies by the frame's sky and sun: each copy reads the one bake, turned into its own space, so
/// a copy turned or under another sky is lit as a space baked just there would be. The camera looks straight down on a
/// 2 metre floor from 3 metres up, with every effect off but the baked lighting, so the floor's light is the bake's.
/// </summary>
[Collection(GpuCollection.Name)]
[Trait("Speed", "Slow")]
[Trait("Needs", "GPU")]
public sealed class ChunkRelightGpuTests(GpuFixture gpu)
{
    private const int Size = 256;
    private const int NoisePeriod = 64;
    private const int Density = 8;
    private const int MostFrames = 120;
    private const RenderEffects OnlyBaked = RenderEffects.All & ~RenderEffects.BakedLighting;

    // The floor fills about 25 to 231 of the 256 pixels each way; the edges, where it meets the walls, are left out.
    private const int FloorFrom = 40;
    private const int FloorTo = 216;

    private static readonly BakedSpace Room =
        new("relight-room", new Vector3(-6f, -3f, -6f), new Vector3(6f, 7f, 6f), 1f, Density);

    // The test quad turned to face up, 2 metres square at the chunk's origin.
    private static readonly Matrix4x4 Floor = Matrix4x4.CreateScale(2f) * Matrix4x4.CreateRotationX(-MathF.PI / 2f);

    // A wall 2 metres square standing on the floor's -x edge and facing +x, over the floor.
    private static readonly Matrix4x4 WestWall =
        Matrix4x4.CreateScale(2f) * Matrix4x4.CreateRotationY(MathF.PI / 2f) * Matrix4x4.CreateTranslation(-1f, 1f, 0f);

    // The same wall on the +x edge, facing -x.
    private static readonly Matrix4x4 EastWall =
        Matrix4x4.CreateScale(2f) * Matrix4x4.CreateRotationY(-MathF.PI / 2f) * Matrix4x4.CreateTranslation(1f, 1f, 0f);

    [Theory]
    [InlineData(180f)]
    [InlineData(90f)]
    public void Turned_chunk_matches_its_mirrored_bake(float degrees)
    {
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        var dark = gpu.Renderer.CreateMaterial(new Material { BaseColor = new Vector4(0.05f, 0.05f, 0.05f, 1f), Roughness = 0.8f });
        try
        {
            // The chunk, wall on -x, is baked once and placed turned. It is compared with a chunk that isn't turned but
            // was baked with the wall where the turn puts it: both are L1, so only the turn of the sky into the chunk's
            // space can part them. A turn of 180 degrees is its own inverse, so 90 is checked too: a copy turned the
            // wrong way gets the bright sky from the open side.
            var placed = Matrix4x4.CreateRotationY(degrees * MathF.PI / 180f);
            var (sun, sky) = SkyOverWall(placed);
            var settings = new BakeSettings(Density, BakeQuality.Preview);

            DrawItem[] turned = [Draw(quad, Floor * placed, gpu.Matte, 1), Draw(quad, WestWall * placed, dark, 2)];
            var turnedView = View(sun, sky, 1f, chunks: [Chunk(placed)]);
            gpu.Renderer.Bakes = new Store(LightBakeFile.Read(gpu.Renderer.BakeChunk(turnedView, turned, 0, settings)));
            Assert.True(RenderUntilFresh(turnedView, turned) <= MostFrames, $"The turned chunk's bake wasn't resident after {MostFrames} frames.");
            byte[] chunk = Capture(turnedView, turned);

            DrawItem[] mirrored = [Draw(quad, Floor, gpu.Matte, 1), Draw(quad, WestWall * placed, dark, 2)];
            var mirroredView = View(sun, sky, 1f, chunks: [Chunk(Matrix4x4.Identity, "relit-mirrored")]);
            gpu.Renderer.Bakes = new Store(LightBakeFile.Read(gpu.Renderer.BakeChunk(mirroredView, mirrored, 0, settings)));
            Assert.True(RenderUntilFresh(mirroredView, mirrored) <= MostFrames, $"The mirrored chunk's bake wasn't resident after {MostFrames} frames.");
            byte[] expected = Capture(mirroredView, mirrored);

            AssertShaded(expected, "mirrored chunk");
            float error = MeanDifference(chunk, expected);
            // 2.50 at 90 degrees and 2.72 at 180 turned right; 5.34 at 90 degrees turned the wrong way.
            Assert.True(error <= 4f,
                $"Turned {degrees} degrees, the chunk's floor is {error:F2} steps from the mirrored bake's on average, more than 4.");

            // The wall's shade is most of the floor's pattern and is in the bake whichever way the sky is turned, so a
            // sky turned the wrong way moves the mean only a little. It shows more in which way the floor's light
            // leans, 4.2 apart turned wrong against 1.2 to 1.4 turned right.
            var lean = Lean(chunk);
            var expectedLean = Lean(expected);
            Assert.True(Vector2.Distance(lean, expectedLean) <= 3f,
                $"Turned {degrees} degrees, the chunk's floor leans {lean} and the mirrored bake's {expectedLean}, more than 3 apart.");
        }
        finally
        {
            LetGo(quad);
            gpu.Renderer.DestroyMaterial(dark);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Chunk_stays_near_a_space_bake()
    {
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        var dark = gpu.Renderer.CreateMaterial(new Material { BaseColor = new Vector4(0.05f, 0.05f, 0.05f, 1f), Roughness = 0.8f });
        try
        {
            // What L1 costs: a space bake traces the whole sky, a chunk keeps only its first two bands. This clear sky is
            // bright at the horizon, which L1 smears over the zenith, so the chunk's open floor reads too bright. Measured
            // at 8.03 steps of 255 on average; an even sky gives about 1. See "Chunk transfer" in docs/design.md.
            var (sun, sky) = SkyOverWall(Matrix4x4.Identity);
            var settings = new BakeSettings(Density, BakeQuality.Preview);
            DrawItem[] draws = [Draw(quad, Floor, gpu.Matte, 1), Draw(quad, WestWall, dark, 2)];

            var chunkView = View(sun, sky, 1f, chunks: [Chunk(Matrix4x4.Identity)]);
            gpu.Renderer.Bakes = new Store(LightBakeFile.Read(gpu.Renderer.BakeChunk(chunkView, draws, 0, settings)));
            Assert.True(RenderUntilFresh(chunkView, draws) <= MostFrames, $"The chunk's bake wasn't resident after {MostFrames} frames.");
            byte[] chunk = Capture(chunkView, draws);

            var spaceView = View(sun, sky, 1f, space: Room);
            gpu.Renderer.Bakes = new Store(LightBakeFile.Read(gpu.Renderer.Bake(spaceView, draws, settings)));
            byte[] space = Capture(spaceView, draws);
            Assert.True(gpu.Renderer.LastBakeFrame!.SpaceFresh, "The space's bake isn't fresh.");

            AssertShaded(space, "space");
            float error = MeanDifference(chunk, space);
            Assert.True(error <= 9f, $"The chunk's floor is {error:F2} steps from the space's on average, more than 9.");
        }
        finally
        {
            LetGo(quad);
            gpu.Renderer.DestroyMaterial(dark);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    // A clear sky is brightest round its sun, here high on the side the wall stands on once turned by placed, so the dark
    // wall shades the floor beside it from the bright part of the sky. The sun itself is Realtime and gives no light here.
    // Live light from the sky knows nothing of the wall, so only a bake shades the floor.
    private static (DirectionalLight[] Sun, AnalyticSky Sky) SkyOverWall(Matrix4x4 placed)
    {
        var wallSide = Vector3.TransformNormal(-Vector3.UnitX, placed);
        DirectionalLight[] sun =
        [
            new() { Mode = LightMode.Realtime, Direction = Vector3.Normalize(wallSide * 0.6f + Vector3.UnitY * 0.8f) },
        ];
        return (sun, new AnalyticSky { Turbidity = 3f });
    }

    // Which way the floor's light leans across the picture: the right half's mean less the left's, and the bottom half's
    // less the top's.
    private static Vector2 Lean(byte[] frame)
    {
        const int Middle = (FloorFrom + FloorTo) / 2;
        return new Vector2(
            Mean(frame, Middle, FloorTo, FloorFrom, FloorTo) - Mean(frame, FloorFrom, Middle, FloorFrom, FloorTo),
            Mean(frame, FloorFrom, FloorTo, Middle, FloorTo) - Mean(frame, FloorFrom, FloorTo, FloorFrom, Middle));
    }

    // The floor is bright enough to compare and the wall's shade falls on one side of the picture or another.
    private static void AssertShaded(byte[] frame, string name)
    {
        float lit = Mean(frame, FloorFrom, FloorTo, FloorFrom, FloorTo);
        Assert.True(lit > 20f, $"The {name}'s floor reads {lit}, too dark to compare.");
        const int Middle = (FloorFrom + FloorTo) / 2;
        float across = MathF.Abs(Mean(frame, FloorFrom, Middle, FloorFrom, FloorTo) - Mean(frame, Middle, FloorTo, FloorFrom, FloorTo));
        float down = MathF.Abs(Mean(frame, FloorFrom, FloorTo, FloorFrom, Middle) - Mean(frame, FloorFrom, FloorTo, Middle, FloorTo));
        Assert.True(across + down > 8f, $"The {name}'s floor is the same all over ({across}, {down}), so the wall shades nothing.");
    }

    [Fact]
    public void Changing_the_sky_relights_without_a_bake()
    {
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        var dark = gpu.Renderer.CreateMaterial(new Material { BaseColor = new Vector4(0.05f, 0.05f, 0.05f, 1f), Roughness = 0.8f });
        try
        {
            gpu.Renderer.SetEnvironment(EvenSky(new Vector3(0.6f, 0.05f, 0.05f)));
            DrawItem[] draws = [Draw(quad, Floor, gpu.Matte, 1), Draw(quad, WestWall, dark, 2)];
            var view = View([], default, 1f, chunks: [Chunk(Matrix4x4.Identity)]);
            var store = new Store(LightBakeFile.Read(gpu.Renderer.BakeChunk(view, draws, 0, new BakeSettings(Density, BakeQuality.Preview))));
            gpu.Renderer.Bakes = store;
            Assert.True(RenderUntilFresh(view, draws) <= MostFrames, $"The chunk's bake wasn't resident after {MostFrames} frames.");
            byte[] red = Capture(view, draws);

            // The very next frame after the sky turns blue.
            gpu.Renderer.SetEnvironment(EvenSky(new Vector3(0.05f, 0.05f, 0.6f)));
            byte[] blue = gpu.Renderer.Capture(view, draws, out _, out _);

            var redFloor = Colour(red, FloorFrom, FloorTo, FloorFrom, FloorTo);
            var blueFloor = Colour(blue, FloorFrom, FloorTo, FloorFrom, FloorTo);
            Assert.True(redFloor.X > redFloor.Z + 20f, $"Under the red sky the floor reads {redFloor}, not red.");
            Assert.True(blueFloor.Z > blueFloor.X + 20f, $"The frame after the sky turned blue the floor reads {blueFloor}, not blue.");

            // The dark wall hides half the sky from the floor beside it. Live light from an even sky lights the whole
            // floor the same, so only the bake darkens that side: the image's left is -x.
            foreach (var (frame, name) in new[] { (red, "red"), (blue, "blue") })
            {
                float byWall = Mean(frame, FloorFrom, FloorFrom + 30, FloorFrom, FloorTo);
                float open = Mean(frame, FloorTo - 30, FloorTo, FloorFrom, FloorTo);
                Assert.True(open > byWall + 4f, $"Under the {name} sky the floor by the wall reads {byWall} and the open side {open}, so it isn't the bake's light.");
            }

            var frameBakes = gpu.Renderer.LastBakeFrame!;
            Assert.Equal([true], frameBakes.ChunkFresh);
            Assert.Equal(1, store.Loads);
        }
        finally
        {
            gpu.Renderer.SetEnvironment(DefaultEnvironment());
            LetGo(quad);
            gpu.Renderer.DestroyMaterial(dark);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Sun_bounce_follows_the_sun()
    {
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        var white = gpu.Renderer.CreateMaterial(new Material { BaseColor = Vector4.One, Roughness = 0.8f });
        try
        {
            // The chunk is turned a quarter and moved, so its sun must be turned into its space too. In the chunk's own
            // space the white wall stands on +x facing -x, and the sun shines on it from -x or from behind it, at the
            // same height, so the floor's direct light is the same and only the wall's bounce changes.
            var placed = Matrix4x4.CreateRotationY(MathF.PI / 2f) * Matrix4x4.CreateTranslation(5f, 0f, 0f);
            DrawItem[] draws = [Draw(quad, Floor * placed, gpu.Matte, 1), Draw(quad, EastWall * placed, white, 2)];
            DirectionalLight[] facing = [Sun(Vector3.TransformNormal(new Vector3(-1f, 0.5f, 0f), placed))];
            DirectionalLight[] behind = [Sun(Vector3.TransformNormal(new Vector3(1f, 0.5f, 0f), placed))];

            // The space is baked and drawn first: once the sun has moved, the renderer counts it as animated and leaves it
            // out of a space's bake, while a chunk's bounce follows it.
            var spaceView = View(facing, default, 0f, space: Room, at: placed);
            var spaceBake = LightBakeFile.Read(gpu.Renderer.Bake(spaceView, draws, new BakeSettings(Density, BakeQuality.Preview)));
            gpu.Renderer.Bakes = new Store(spaceBake);
            byte[] spaceFacing = Capture(spaceView, draws);
            Assert.True(gpu.Renderer.LastBakeFrame!.SpaceFresh, "The space's bake isn't fresh.");

            var chunkView = View(facing, default, 0f, chunks: [Chunk(placed)], at: placed);
            var chunkBake = LightBakeFile.Read(gpu.Renderer.BakeChunk(chunkView, draws, 0, new BakeSettings(Density, BakeQuality.Preview)));
            gpu.Renderer.Bakes = new Store(chunkBake);
            Assert.True(RenderUntilFresh(chunkView, draws) <= MostFrames, $"The chunk's bake wasn't resident after {MostFrames} frames.");
            byte[] chunkFacing = Capture(chunkView, draws);
            byte[] chunkBehind = Capture(View(behind, default, 0f, chunks: [Chunk(placed)], at: placed), draws);

            float lit = Mean(chunkFacing, FloorFrom, FloorTo, FloorFrom, FloorTo);
            float dim = Mean(chunkBehind, FloorFrom, FloorTo, FloorFrom, FloorTo);
            Assert.True(lit > dim + 3f, $"The floor reads {lit} with the sun on the wall and {dim} with it behind, so the bounce didn't follow.");
            float error = MeanDifference(chunkFacing, spaceFacing);
            Assert.True(error <= 8f, $"The chunk's floor is {error:F2} steps from the space's with a Mixed sun, more than 8.");
        }
        finally
        {
            LetGo(quad);
            gpu.Renderer.DestroyMaterial(white);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Sky_projection_of_a_uniform_sky_is_four_pi_y0()
    {
        int errors = gpu.ValidationErrors;
        try
        {
            var radiance = new Vector3(0.5f, 0.25f, 1f);
            gpu.Renderer.SetEnvironment(EvenSky(radiance));
            gpu.RenderOneFrame();

            var sky = gpu.Renderer.ProjectSky();

            // An even sky of radiance L projects to its integral times Y0 in the constant band, 4 pi Y0 L = 3.5449 L, and
            // nothing in the linear band.
            Assert.Equal(4, sky.Length);
            var expected = 3.5449f * radiance;
            for (int channel = 0; channel < 3; channel++)
            {
                float want = expected[channel];
                float got = sky[0][channel];
                Assert.True(MathF.Abs(got - want) <= 0.01f * want, $"Channel {channel} of C0 is {got}, not {want} within 1%.");
                for (int k = 1; k < 4; k++)
                {
                    Assert.True(MathF.Abs(sky[k][channel]) <= 0.01f * want, $"Channel {channel} of C{k} is {sky[k][channel]}, not 0.");
                }
            }
        }
        finally
        {
            gpu.Renderer.SetEnvironment(DefaultEnvironment());
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Sky_projection_of_a_sky_bright_towards_plus_x_fills_the_x_band()
    {
        int errors = gpu.ValidationErrors;
        var east = ProjectClearSky(new Vector3(0.6f, 0.8f, 0f));
        var west = ProjectClearSky(new Vector3(-0.6f, 0.8f, 0f));

        // A clear sky is brightest around its sun, so with the sun towards +x the x band (C3) is positive. The sky is
        // also brighter above than below, so C1 isn't 0 here; mirroring the sun to -x cancels it. Mirroring x maps the
        // cube's cells onto each other exactly, so C0, C1 and C2 stay the same and C3 changes sign. A sun with no z
        // leaves the sky the same at +z and -z, so C2 is 0. Each is held to 1% of C3 for the cube's own sampling.
        for (int channel = 0; channel < 3; channel++)
        {
            float x = east[3][channel];
            float bound = 0.01f * x;
            float c0 = east[0][channel];
            Assert.True(x > 0.05f * c0, $"Channel {channel} of C3 is {x} with the sun towards +x and C0 {c0}, not a twentieth of it.");
            Assert.True(MathF.Abs(x + west[3][channel]) <= bound,
                $"Channel {channel} of C3 is {x} with the sun towards +x and {west[3][channel]} towards -x, not opposite.");
            Assert.True(MathF.Abs(east[1][channel] - west[1][channel]) <= bound,
                $"Channel {channel} of C1 is {east[1][channel]} with the sun towards +x and {west[1][channel]} towards -x, so the x band leaked into y.");
            Assert.True(MathF.Abs(east[2][channel]) <= bound,
                $"Channel {channel} of C2 is {east[2][channel]} with the sun towards +x, not 0 within {bound}.");
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    // The sky's L1 set under a clear sky whose sun is towards the direction given.
    private Vector4[] ProjectClearSky(Vector3 towards)
    {
        DirectionalLight[] sun = [new() { Mode = LightMode.Realtime, Direction = Vector3.Normalize(towards) }];
        gpu.Renderer.Render(View(sun, new AnalyticSky { Turbidity = 3f }, 1f), []);
        return gpu.Renderer.ProjectSky();
    }

    private static DirectionalLight Sun(Vector3 towards) => new()
    {
        Mode = LightMode.Mixed, Direction = Vector3.Normalize(towards), Radiance = new Vector3(3f),
    };

    private static EnvironmentDescription EvenSky(Vector3 radiance) => new()
    {
        Ceiling = radiance,
        Horizon = radiance,
        Floor = radiance,
    };

    // The renderer's usual environment, put back after each test.
    private static EnvironmentDescription DefaultEnvironment() => new()
    {
        Ceiling = new Vector3(0.5f),
        Horizon = new Vector3(0.4f),
        Floor = new Vector3(0.2f),
    };

    private static BakedChunk Chunk(Matrix4x4 transform, string name = "relit") =>
        new(name, transform, new Vector3(-2f, -1f, -2f), new Vector3(2f, 3f, 2f), Density);

    // A draw without detail is the point at its origin to the bake areas, so the chunk holding that point owns it.
    private static DrawItem Draw(MeshHandle mesh, Matrix4x4 transform, MaterialHandle material, ulong instance) => new()
    {
        Mesh = mesh,
        IndexCount = 6,
        Material = material,
        Transform = transform,
        BakeId = BakeIds.Of(instance << 12, 0),
    };

    // Looking straight down on the floor at the transform given from 3 metres up, with +x to the right of the picture.
    private static FrameView View(
        DirectionalLight[] suns, AnalyticSky sky, float environment, BakedChunk[]? chunks = null, BakedSpace? space = null,
        Matrix4x4? at = null)
    {
        var placed = at ?? Matrix4x4.Identity;
        var eye = Vector3.Transform(new Vector3(0f, 3f, 0f), placed);
        var centre = Vector3.Transform(Vector3.Zero, placed);
        return new FrameView
        {
            Width = Size,
            Height = Size,
            View = Matrix4x4.CreateLookAt(eye, centre, Vector3.TransformNormal(-Vector3.UnitZ, placed)),
            Projection = Projection.PerspectiveInfiniteReversedZ(MathF.PI / 4f, 1f, 0.1f),
            CameraPosition = eye,
            EnvironmentIntensity = environment,
            Sky = sky,
            Off = OnlyBaked,
            Lights = suns,
            BakedChunks = chunks ?? [],
            BakedSpace = space,
        };
    }

    private int RenderUntilFresh(FrameView view, DrawItem[] draws)
    {
        for (int frames = 1; frames <= MostFrames; frames++)
        {
            gpu.Renderer.Render(view, draws);
            if (gpu.Renderer.LastBakeFrame!.ChunkFresh.All(fresh => fresh))
            {
                return frames;
            }
        }

        return MostFrames + 1;
    }

    // After at least 63 frames of the view, on the first frame of the dither's noise, so two captures fall on the same
    // frame of it however many frames came between them.
    private byte[] Capture(FrameView view, DrawItem[] draws)
    {
        for (int i = 0; i < NoisePeriod - 1 || gpu.Renderer.FrameNumber % NoisePeriod != 0; i++)
        {
            gpu.Renderer.Render(view, draws);
        }

        return gpu.Renderer.Capture(view, draws, out _, out _);
    }

    // The mean of red, green and blue over the pixels from x0 to x1 and y0 to y1, ends excluded.
    private static float Mean(byte[] frame, int x0, int x1, int y0, int y1)
    {
        var colour = Colour(frame, x0, x1, y0, y1);
        return (colour.X + colour.Y + colour.Z) / 3f;
    }

    private static Vector3 Colour(byte[] frame, int x0, int x1, int y0, int y1)
    {
        var sum = Vector3.Zero;
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                int at = (y * Size + x) * 4;
                sum += new Vector3(frame[at], frame[at + 1], frame[at + 2]);
            }
        }

        return sum / ((x1 - x0) * (y1 - y0));
    }

    // The mean over the floor's pixels and colours of how far apart two frames are, in steps of 255.
    private static float MeanDifference(byte[] a, byte[] b)
    {
        Assert.Equal(a.Length, b.Length);
        long sum = 0;
        int count = 0;
        for (int y = FloorFrom; y < FloorTo; y++)
        {
            for (int x = FloorFrom; x < FloorTo; x++)
            {
                int at = (y * Size + x) * 4;
                for (int c = 0; c < 3; c++)
                {
                    sum += Math.Abs(a[at + c] - b[at + c]);
                    count++;
                }
            }
        }

        return (float)sum / count;
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

    // Puts the renderer back: no store, and a frame without chunks so nothing stays in the pool.
    private void LetGo(MeshHandle quad)
    {
        gpu.Renderer.Bakes = NoBakes.Instance;
        gpu.RenderOneFrame();
        gpu.Renderer.DestroyMesh(quad);
    }

    /// <summary>A store holding one bake, fresh for any file whose inputs match it, counting its loads.</summary>
    private sealed class Store(LightBakeFile bake) : IBakeStore
    {
        private int _loads;

        public int Loads => Volatile.Read(ref _loads);

        public bool IsFresh(string file, BakeInputs inputs) => bake.Inputs == inputs;

        public LightBakeFile? Load(string file)
        {
            Interlocked.Increment(ref _loads);
            return bake;
        }
    }
}
