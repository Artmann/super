using System.Numerics;

namespace Rendition.Gpu.Tests;

/// <summary>
/// The bands that keep a bake from ending in a step: a chunk's lightmap blends into the live light over its
/// <see cref="BakedChunk.EdgeBlend"/> in from its sides, and a baked space's probes into the live lookup over one probe
/// spacing in from its box's faces. Every effect is off but the baked lighting, so the light compared is the bake's and
/// the live sky's or probes' alone.
/// </summary>
[Collection(GpuCollection.Name)]
[Trait("Speed", "Slow")]
[Trait("Needs", "GPU")]
public sealed class EdgeBandGpuTests(GpuFixture gpu)
{
    private const int Size = 256;
    private const int NoisePeriod = 64;
    private const int Density = 8;
    private const int MostFrames = 120;
    private const RenderEffects OnlyBaked = RenderEffects.All & ~RenderEffects.BakedLighting;

    // The chunk's box, 4 metres across x and z, in its own space.
    private static readonly Vector3 ChunkMin = new(-2f, -1f, -2f);
    private static readonly Vector3 ChunkMax = new(2f, 3f, 2f);

    // The test quad turned to face up, 4 metres square: the chunk's floor fills its box from side to side.
    private static readonly Matrix4x4 ChunkFloor = Matrix4x4.CreateScale(4f) * Matrix4x4.CreateRotationX(-MathF.PI / 2f);

    [Fact]
    public void Floor_crossing_a_chunk_side_has_no_step()
    {
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        var dark = gpu.Renderer.CreateMaterial(new Material { BaseColor = new Vector4(0.05f, 0.05f, 0.05f, 1f), Roughness = 0.8f });
        try
        {
            // Two dark walls 2 metres tall make an alley 0.4 metres wide along the chunk's floor, at z = -1 facing +z and
            // at z = -0.6 facing -z, and end at the chunk's +x side. The bake knows they hide most of the sky from the
            // alley's floor; the live light beyond the side doesn't, so the dynamic floor that carries on past x = 2 is
            // lit by the whole sky.
            gpu.Renderer.SetEnvironment(EvenSky(0.5f));
            var wallSize = Matrix4x4.CreateScale(4f, 2f, 1f);
            var south = Draw(quad, wallSize * Matrix4x4.CreateTranslation(0f, 1f, -1f), dark, 2);
            var north = Draw(quad, wallSize * Matrix4x4.CreateRotationY(MathF.PI) * Matrix4x4.CreateTranslation(0f, 1f, -0.6f), dark, 3);
            DrawItem[] chunkDraws = [Draw(quad, ChunkFloor, gpu.Matte, 1), south, north];
            var beyond = new DrawItem
            {
                Mesh = quad,
                IndexCount = 6,
                Material = gpu.Matte,
                Transform = ChunkFloor * Matrix4x4.CreateTranslation(4f, 0f, 0f),
            };
            DrawItem[] draws = [.. chunkDraws, beyond];

            // From 4 metres up over the side, the row down the middle of the alley.
            var eye = new Vector3(2f, 4f, -0.8f);
            var banded = TopView(eye, Chunk(BakedChunk.DefaultEdgeBlend));
            var hard = TopView(eye, Chunk(0f));
            gpu.Renderer.Bakes = new Store(LightBakeFile.Read(gpu.Renderer.BakeChunk(banded, chunkDraws, 0, Settings())));
            Assert.True(RenderUntilFresh(banded, draws) <= MostFrames, $"The chunk's bake wasn't resident after {MostFrames} frames.");

            int bandStep = WorstStep(Capture(banded, draws), Size / 2);
            // The band isn't in the bake's hash, so the same bake is fresh without it.
            int hardStep = WorstStep(Capture(hard, draws), Size / 2);
            Assert.True(gpu.Renderer.LastBakeFrame!.ChunkFresh[0], "The chunk's bake isn't fresh without its band.");

            // 4 measured with the band; 27 without it.
            Assert.True(hardStep > 12, $"With no band the floor steps by {hardStep} at most, too little for the test to fail.");
            Assert.True(bandStep <= 4, $"With the band the floor steps by {bandStep} between neighbouring pixels, more than 4.");
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
    public void Edge_band_keeps_a_light_counted_once()
    {
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        try
        {
            // An open floor under an even white sky, with no live probes: the bake's light is the sky's, πL, and so is
            // the live light the band blends towards. A band that let either side count twice, or neither, would show.
            gpu.Renderer.SetEnvironment(EvenSky(0.5f));
            DrawItem[] draws = [Draw(quad, ChunkFloor, gpu.Matte, 1)];
            var view = TopView(new Vector3(1f, 3f, 0f), Chunk(BakedChunk.DefaultEdgeBlend));
            gpu.Renderer.Bakes = new Store(LightBakeFile.Read(gpu.Renderer.BakeChunk(view, draws, 0, Settings())));
            Assert.True(RenderUntilFresh(view, draws) <= MostFrames, $"The chunk's bake wasn't resident after {MostFrames} frames.");
            byte[] frame = Capture(view, draws);

            // From 3 metres up over x = 1 a pixel is 2.485 / 256 metres, so the chunk's centre, x = 0, is at column 25,
            // the band runs from x = 1.5, column 180, to x = 2, column 231, and its middle is near column 206.
            const int CentreColumn = 25;
            const int BandFrom = 180;
            const int BandTo = 229;
            float centre = ColumnMean(frame, CentreColumn);
            Assert.True(centre > 40f, $"The floor's centre reads {centre}, too dark to compare.");            for (int x = BandFrom; x <= BandTo; x++)
            {
                float here = ColumnMean(frame, x);
                Assert.True(MathF.Abs(here - centre) <= 3f,
                    $"Column {x} of the band reads {here:F2}, more than 3 steps from the centre's {centre:F2}.");
            }
        }
        finally
        {
            gpu.Renderer.SetEnvironment(DefaultEnvironment());
            LetGo(quad);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Moving_quad_crosses_the_space_box_without_a_jump()
    {
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        var red = gpu.Renderer.CreateMaterial(new Material { BaseColor = new Vector4(0.8f, 0.05f, 0.05f, 1f), Roughness = 1f });
        try
        {
            // The space's probes saw a red wall lit by a Baked sun, with no sky, so inside the box a dynamic quad is red.
            // The frame has no sun and a dim grey sky, so outside the box the quad is grey. Probes 2 metres apart band
            // the light from x = 2 to the box's face at x = 4.
            var room = new BakedSpace("edge-band-room", new Vector3(-4f), new Vector3(4f), 2f, 4);
            var wall = new DrawItem
            {
                Mesh = quad,
                IndexCount = 6,
                Material = red,
                Transform = Matrix4x4.CreateScale(8f) * Matrix4x4.CreateRotationY(MathF.PI) * Matrix4x4.CreateTranslation(0f, 0f, 3.5f),
                BakeId = BakeIds.Of(1ul << 12, 0),
            };
            DirectionalLight[] sun = [new() { Mode = LightMode.Baked, Direction = -Vector3.UnitZ, Radiance = new Vector3(0.5f) }];
            Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
            gpu.Renderer.Render(SideView(Vector3.UnitZ * 3f, room, [], sky: 0f), [wall]);
            var bake = LightBakeFile.Read(gpu.Renderer.Bake(
                SideView(Vector3.UnitZ * 3f, room, sun, sky: 0f), [wall], new BakeSettings(4, BakeQuality.Preview)));
            gpu.Renderer.Bakes = new Loaded(bake);
            gpu.Renderer.SetEnvironment(EvenSky(0.1f));

            int banded = WorstJump(quad, wall, room, out var bandedAt);
            gpu.Renderer.BakedProbeBand = false;
            int hard = WorstJump(quad, wall, room, out var hardAt);

            // 4 measured with the band, at x = 3.1 as the sky's grey fades in; 32 without it, at the box's face.
            Assert.True(gpu.Renderer.LastBakeFrame!.SpaceFresh, "The bake isn't drawn, so the test shows nothing.");
            Assert.True(hard > 12, $"With a hard box the quad's centre jumps by {hard} at most, at x = {hardAt:F1}, too little for the test to fail.");
            Assert.True(banded <= 4, $"The quad's centre changes by {banded} in one 10 cm step, at x = {bandedAt:F1}, more than 4.");
        }
        finally
        {
            gpu.Renderer.BakedProbeBand = true;
            gpu.Renderer.Bakes = NoBakes.Instance;
            gpu.Renderer.SetEnvironment(DefaultEnvironment());
            gpu.Renderer.DestroyMesh(quad);
            gpu.Renderer.DestroyMaterial(red);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    // The most a half-metre grey quad's centre changes, in any colour, between two of its places 10 cm apart, as it moves
    // from x = 1.5 to x = 5 with the camera 1.5 metres in front of it; and where the change ended.
    private int WorstJump(MeshHandle quad, DrawItem wall, BakedSpace room, out float at)
    {
        int worst = 0;
        at = 0f;
        Vector3? before = null;
        for (int step = 15; step <= 50; step++)
        {
            float x = step / 10f;
            var dynamic = new DrawItem
            {
                Mesh = quad,
                IndexCount = 6,
                Material = gpu.Matte,
                Transform = Matrix4x4.CreateScale(0.5f) * Matrix4x4.CreateTranslation(x, 0f, 0f),
            };
            var centre = Centre(Capture(SideView(new Vector3(x, 0f, 1.5f), room, [], sky: 1f), [wall, dynamic]));
            if (before is { } last)
            {
                var change = Vector3.Abs(centre - last);
                int most = (int)MathF.Max(change.X, MathF.Max(change.Y, change.Z));
                if (most > worst)
                {
                    worst = most;
                    at = x;
                }
            }

            before = centre;
        }

        return worst;
    }

    // The largest difference in any colour between two neighbouring pixels along row y.
    private static int WorstStep(byte[] frame, int y)
    {
        int worst = 0;
        for (int x = 1; x < Size; x++)
        {
            for (int c = 0; c < 3; c++)
            {
                int a = frame[(y * Size + x - 1) * 4 + c];
                int b = frame[(y * Size + x) * 4 + c];
                worst = Math.Max(worst, Math.Abs(a - b));
            }
        }

        return worst;
    }

    // The mean of red, green and blue down column x, over the 32 rows round the picture's middle, which evens out the
    // dither's noise.
    private static float ColumnMean(byte[] frame, int x)
    {
        float sum = 0f;
        for (int y = Size / 2 - 16; y < Size / 2 + 16; y++)
        {
            int at = (y * Size + x) * 4;
            sum += frame[at] + frame[at + 1] + frame[at + 2];
        }

        return sum / (32 * 3);
    }

    private static Vector3 Centre(byte[] frame)
    {
        int at = ((Size / 2) * Size + Size / 2) * 4;
        return new Vector3(frame[at], frame[at + 1], frame[at + 2]);
    }

    private static BakeSettings Settings() => new(Density, BakeQuality.Preview);

    private static BakedChunk Chunk(float edgeBlend) => new("edge-band", Matrix4x4.Identity, ChunkMin, ChunkMax, Density, edgeBlend);

    // A draw without detail is the point at its origin to the bake areas, so the chunk holding that point owns it.
    private static DrawItem Draw(MeshHandle mesh, Matrix4x4 transform, MaterialHandle material, ulong instance) => new()
    {
        Mesh = mesh,
        IndexCount = 6,
        Material = material,
        Transform = transform,
        BakeId = BakeIds.Of(instance << 12, 0),
    };

    // Looking straight down from eye, with +x to the right of the picture, under the environment's sky.
    private static FrameView TopView(Vector3 eye, BakedChunk chunk) => new()
    {
        Width = Size,
        Height = Size,
        View = Matrix4x4.CreateLookAt(eye, eye with { Y = 0f }, -Vector3.UnitZ),
        Projection = Projection.PerspectiveInfiniteReversedZ(MathF.PI / 4f, 1f, 0.1f),
        CameraPosition = eye,
        EnvironmentIntensity = 1f,
        Off = OnlyBaked,
        BakedChunks = new[] { chunk },
    };

    // Looking down -z from the camera, with the sky at the intensity given.
    private static FrameView SideView(Vector3 camera, BakedSpace room, DirectionalLight[] suns, float sky) => new()
    {
        Width = Size,
        Height = Size,
        View = Matrix4x4.CreateLookAt(camera, camera with { Z = 0f }, Vector3.UnitY),
        Projection = Projection.PerspectiveInfiniteReversedZ(MathF.PI / 4f, 1f, 0.1f),
        CameraPosition = camera,
        EnvironmentIntensity = sky,
        Off = OnlyBaked,
        BakedSpace = room,
        Lights = suns,
    };

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

    private static EnvironmentDescription EvenSky(float radiance) => new()
    {
        Ceiling = new Vector3(radiance),
        Horizon = new Vector3(radiance),
        Floor = new Vector3(radiance),
    };

    // The renderer's usual environment, put back after each test.
    private static EnvironmentDescription DefaultEnvironment() => new()
    {
        Ceiling = new Vector3(0.5f),
        Horizon = new Vector3(0.4f),
        Floor = new Vector3(0.2f),
    };

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

    /// <summary>A store holding one bake, fresh for any file whose inputs match it.</summary>
    private sealed class Store(LightBakeFile bake) : IBakeStore
    {
        public bool IsFresh(string file, BakeInputs inputs) => bake.Inputs == inputs;

        public LightBakeFile? Load(string file) => bake;
    }

    // A store that holds one bake and calls it fresh for every area, whatever the frame's inputs.
    private sealed class Loaded(LightBakeFile bake) : IBakeStore
    {
        public bool IsFresh(string file, BakeInputs inputs) => true;

        public LightBakeFile? Load(string file) => bake;
    }
}
