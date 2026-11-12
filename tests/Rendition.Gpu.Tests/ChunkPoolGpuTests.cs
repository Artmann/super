using System.Numerics;

namespace Rendition.Gpu.Tests;

/// <summary>
/// The chunk bake pool: a fresh chunk's bake is read off the render thread, uploaded a little a frame, shared by every
/// copy of the chunk, and let go once no copy is named. Each test bakes one chunk, a 2 metre wall facing +z with a Baked
/// lamp in front of it, and draws it with every effect off but the baked lighting and the sky off, so the lamp is the
/// only light.
/// </summary>
[Collection(GpuCollection.Name)]
[Trait("Speed", "Slow")]
[Trait("Needs", "GPU")]
public sealed class ChunkPoolGpuTests(GpuFixture gpu)
{
    private const int Size = 256;
    private const int NoisePeriod = 64;
    private const int Density = 8;
    private const int MostFrames = 120;
    private const RenderEffects OnlyBaked = RenderEffects.All & ~RenderEffects.BakedLighting;

    // The lamp's place in front of the wall, in the chunk's space.
    private static readonly Vector3 LampAt = new(0.3f, 0.2f, 1f);

    // Far enough apart that a camera on one copy never sees the other.
    private static readonly Matrix4x4 First = Matrix4x4.Identity;
    private static readonly Matrix4x4 Second = Matrix4x4.CreateTranslation(40f, 0f, 0f);

    [Fact]
    public void Chunk_bake_loads_without_blocking_a_frame()
    {
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        var store = new HeldStore();
        try
        {
            var wall = Wall(quad, First);
            LocalLight[] lamps = [Lamp(First, 7401)];
            store.Hold(Bake(wall, First, lamps));
            // Drawn with no chunk named, the Baked lamp is real-time.
            byte[] live = Capture(View(First, lamps, []), wall);

            gpu.Renderer.Bakes = store;
            var view = View(First, lamps, [Chunk(First)]);
            // While the load is held every frame still draws: the chunk is pending, so its lamp is drawn live and the
            // frame is the same as one with no chunk at all.
            byte[] pending = Capture(view, wall);
            var frame = gpu.Renderer.LastBakeFrame!;
            Assert.True(frame.ChunkPending(0), "The chunk isn't pending while its load is held.");
            Assert.Equal([false], frame.ChunkFresh);
            Assert.Empty(frame.DroppedLocalLights);
            Assert.Equal(0, gpu.Renderer.ChunkBakes.Resident);
            Assert.Equal(1, gpu.Renderer.ChunkBakes.Pending);
            Assert.Equal(live, pending);
            Assert.Equal(1, store.Loads);
            Assert.NotEqual(Environment.CurrentManagedThreadId, store.LoadThread);

            store.Release();
            int frames = RenderUntilFresh(view, wall);

            Assert.True(frames <= MostFrames, $"The chunk's bake wasn't resident after {MostFrames} frames.");
            frame = gpu.Renderer.LastBakeFrame!;
            Assert.False(frame.ChunkPending(0));
            Assert.Equal([0], frame.DroppedLocalLights);
            Assert.Equal(1, gpu.Renderer.ChunkBakes.Resident);
            Assert.Equal(0, gpu.Renderer.ChunkBakes.Pending);
            Assert.Equal(1, store.Loads);
        }
        finally
        {
            store.Release();
            LetGo(quad);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Two_copies_share_one_bake()
    {
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        var store = new HeldStore();
        try
        {
            var first = Wall(quad, First);
            var second = Wall(quad, Second);
            // Each copy has its own lamp, at the same place in the chunk: one key in two places would be a light that
            // moved, which is kept out of bakes.
            LocalLight[] lamps = [Lamp(First, 7411), Lamp(Second, 7412)];
            store.Hold(Bake(first, First, [lamps[0]]));
            store.Release();
            byte[] unlit = Capture(View(First, [], []), first, second);

            gpu.Renderer.Bakes = store;
            BakedChunk[] chunks = [Chunk(First), Chunk(Second)];
            int frames = RenderUntilFresh(View(First, lamps, chunks), first, second);
            Assert.True(frames <= MostFrames, $"The chunks' bake wasn't resident after {MostFrames} frames.");

            byte[] seenFromFirst = Capture(View(First, lamps, chunks), first, second);
            byte[] seenFromSecond = Capture(View(Second, lamps, chunks), first, second);

            var frame = gpu.Renderer.LastBakeFrame!;
            Assert.Equal([true, true], frame.ChunkFresh);
            // Both lamps are left to the bake, so the walls' light comes from the one lightmap.
            Assert.Equal([0, 1], frame.DroppedLocalLights);
            Assert.Equal(1, gpu.Renderer.ChunkBakes.Resident);
            Assert.Equal(1, store.Loads);
            Assert.NotEqual(unlit, seenFromFirst);
            int lit = 0;
            for (int i = 0; i < seenFromFirst.Length; i++)
            {
                Assert.True(Math.Abs(seenFromFirst[i] - seenFromSecond[i]) <= 1,
                    $"Byte {i} reads {seenFromFirst[i]} on the first copy but {seenFromSecond[i]} on the second.");
                lit += seenFromFirst[i] > unlit[i] + 8 ? 1 : 0;
            }

            Assert.True(lit > 1000, $"Only {lit} bytes are lit by the bake, so the walls' light isn't from it.");
        }
        finally
        {
            store.Release();
            LetGo(quad);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Chunk_bake_is_released_when_no_copy_is_named()
    {
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        var store = new HeldStore();
        try
        {
            var wall = Wall(quad, First);
            LocalLight[] lamps = [Lamp(First, 7421)];
            store.Hold(Bake(wall, First, lamps));
            store.Release();
            gpu.Renderer.Bakes = store;
            int frames = RenderUntilFresh(View(First, lamps, [Chunk(First)]), wall);
            Assert.True(frames <= MostFrames, $"The chunk's bake wasn't resident after {MostFrames} frames.");
            Assert.Equal(1, gpu.Renderer.ChunkBakes.Resident);
            var pages = gpu.Renderer.ChunkBakes.Entries.Single().Transfer.Select(slot => new TextureHandle(slot)).ToArray();
            Assert.All(pages, page => Assert.True(gpu.Renderer.Resources.IsLive(page)));

            // Straight after one frame that names no copy, the bake is let go, while the frame before it may still be
            // drawing from it: its pages wait for that frame instead of going at once.
            var without = View(First, lamps, []);
            gpu.Renderer.Render(without, [wall]);
            Assert.Equal(0, gpu.Renderer.ChunkBakes.Resident);
            Assert.Equal(0, gpu.Renderer.ChunkBakes.Pending);
            Assert.All(pages, page => Assert.True(gpu.Renderer.Resources.IsDying(page), "A page went before the frames drawing it were done."));
            for (int i = 0; i < 3; i++)
            {
                gpu.Renderer.Render(without, [wall]);
            }

            Assert.All(pages, page => Assert.False(gpu.Renderer.Resources.IsDying(page) || gpu.Renderer.Resources.IsLive(page)));

            Assert.Equal(errors, gpu.ValidationErrors);
        }
        finally
        {
            store.Release();
            LetGo(quad);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Reloading_the_store_reads_its_chunk_bakes_again()
    {
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        var store = new HeldStore();
        try
        {
            var wall = Wall(quad, First);
            LocalLight[] lamps = [Lamp(First, 7431)];
            store.Hold(Bake(wall, First, lamps));
            store.Release();
            gpu.Renderer.Bakes = store;
            var view = View(First, lamps, [Chunk(First)]);
            Assert.True(RenderUntilFresh(view, wall) <= MostFrames, $"The chunk's bake wasn't resident after {MostFrames} frames.");

            store.Reloads++;
            gpu.Renderer.Render(view, [wall]);

            Assert.True(gpu.Renderer.LastBakeFrame!.ChunkPending(0), "The chunk's old bake was kept through the reload.");
            Assert.True(RenderUntilFresh(view, wall) <= MostFrames, $"The chunk's bake wasn't resident again after {MostFrames} frames.");
            Assert.Equal(2, store.Loads);
            Assert.Equal(1, gpu.Renderer.ChunkBakes.Resident);
        }
        finally
        {
            store.Release();
            LetGo(quad);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Chunk_bake_with_a_wrong_transfer_format_is_lit_live()
    {
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        var store = new HeldStore();
        try
        {
            var wall = Wall(quad, First);
            LocalLight[] lamps = [Lamp(First, 7441)];
            var bake = Bake(wall, First, lamps);
            // The sun tint written as RGBA16F, as a wrong cook might: the shader reads it as RGBA8, so it must never go up.
            var tint = bake.Transfer!.SunTint;
            var wrong = new LightBakePage(tint.Width, tint.Height, LightBakePageFormat.Rgba16f, new byte[tint.Width * tint.Height * 8]);
            store.Hold(bake with { Transfer = bake.Transfer with { SunTint = wrong } });
            store.Release();
            byte[] live = Capture(View(First, lamps, []), wall);

            gpu.Renderer.Bakes = store;
            var view = View(First, lamps, [Chunk(First)]);
            int frames = 0;
            do
            {
                gpu.Renderer.Render(view, [wall]);
                frames++;
            }
            while (gpu.Renderer.LastBakeFrame!.ChunkPending(0) && frames < MostFrames);

            Assert.True(frames < MostFrames, $"The chunk was still pending after {MostFrames} frames.");
            // Up to a whole number of the noise's periods since the live capture, so the capture lands on its frame.
            for (; frames % NoisePeriod != 0; frames++)
            {
                gpu.Renderer.Render(view, [wall]);
            }

            byte[] refused = Capture(view, wall);
            var frame = gpu.Renderer.LastBakeFrame!;
            Assert.Equal([false], frame.ChunkFresh);
            Assert.False(frame.ChunkPending(0));
            Assert.Empty(frame.DroppedLocalLights);
            Assert.Equal(0, gpu.Renderer.ChunkBakes.Resident);
            Assert.Equal(1, store.Loads);
            Assert.Equal(live, refused);
        }
        finally
        {
            store.Release();
            LetGo(quad);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    // Renders until the frame's chunks are all fresh, and gives how many frames that took, or more than MostFrames.
    private int RenderUntilFresh(FrameView view, params DrawItem[] draws)
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

    // Puts the renderer back: no store, and a frame without chunks so nothing stays in the pool.
    private void LetGo(MeshHandle quad)
    {
        gpu.Renderer.Bakes = NoBakes.Instance;
        gpu.RenderOneFrame();
        gpu.Renderer.DestroyMesh(quad);
    }

    // A chunk 4 metres across round its origin, where its wall stands.
    private static BakedChunk Chunk(Matrix4x4 transform) =>
        new("pooled", transform, new Vector3(-2f), new Vector3(2f), Density);

    private static LocalLight Lamp(Matrix4x4 chunk, int key) => new()
    {
        Mode = LightMode.Baked, Key = key, Position = Vector3.Transform(LampAt, chunk), Radiance = new Vector3(3f),
        Range = 4f, Radius = 0.02f,
    };

    // The test quad scaled to 2 metres, facing +z at the chunk's origin. A draw without detail is the point at its
    // origin to the bake areas, so the chunk standing there owns it. Every copy has the same id, as copies of a chunk do.
    private DrawItem Wall(MeshHandle mesh, Matrix4x4 chunk) => new()
    {
        Mesh = mesh,
        IndexCount = 6,
        Material = gpu.Matte,
        Transform = Matrix4x4.CreateScale(2f) * chunk,
        BakeId = BakeIds.Of(1ul << 12, 0),
    };

    // Bakes the one copy of the chunk named, with only its own draw and lamp in the frame.
    private LightBakeFile Bake(DrawItem wall, Matrix4x4 chunk, LocalLight[] lamps)
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        var view = View(chunk, lamps, [Chunk(chunk)]);
        return LightBakeFile.Read(gpu.Renderer.BakeChunk(view, [wall], 0, new BakeSettings(Density, BakeQuality.Preview)));
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

    // Looking at the copy of the chunk at the transform given from 3 metres in front of it, with the sky off.
    private static FrameView View(Matrix4x4 chunk, LocalLight[] lamps, BakedChunk[] chunks)
    {
        var eye = Vector3.Transform(new Vector3(0f, 0f, 3f), chunk);
        var at = Vector3.Transform(Vector3.Zero, chunk);
        return new FrameView
        {
            Width = Size,
            Height = Size,
            View = Matrix4x4.CreateLookAt(eye, at, Vector3.UnitY),
            Projection = Projection.PerspectiveInfiniteReversedZ(MathF.PI / 4f, 1f, 0.1f),
            CameraPosition = eye,
            EnvironmentIntensity = 0f,
            Off = OnlyBaked,
            LocalLights = lamps,
            BakedChunks = chunks,
        };
    }

    // The 64th frame, so two captures fall on the same frame of the dither's noise.
    private byte[] Capture(FrameView view, params DrawItem[] draws)
    {
        for (int i = 0; i < NoisePeriod - 1; i++)
        {
            gpu.Renderer.Render(view, draws);
        }

        return gpu.Renderer.Capture(view, draws, out _, out _);
    }

    /// <summary>
    /// A store holding one chunk bake, fresh for any chunk file whose inputs match it. Its Load waits until the test
    /// releases it, and gives up after a while so a load made on the render thread fails the test rather than hanging it.
    /// </summary>
    private sealed class HeldStore : IBakeStore
    {
        private readonly ManualResetEventSlim _gate = new(false);
        private LightBakeFile? _bake;
        private int _loads;

        public int Loads => Volatile.Read(ref _loads);

        public int LoadThread { get; private set; }

        public int Reloads { get; set; }

        public void Hold(LightBakeFile bake) => _bake = bake;

        public void Release() => _gate.Set();

        public bool IsFresh(string file, BakeInputs inputs) =>
            file.StartsWith("lighting/pooled/", StringComparison.Ordinal) && _bake is { } bake && bake.Inputs == inputs;

        public LightBakeFile? Load(string file)
        {
            Interlocked.Increment(ref _loads);
            LoadThread = Environment.CurrentManagedThreadId;
            if (!_gate.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new IOException("The test held the load and never let it go.");
            }

            return _bake;
        }
    }
}
