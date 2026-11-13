using System.Numerics;

namespace Rendition.Gpu.Tests;

/// <summary>
/// <see cref="Renderer.AutoBake"/>: a frame that names an unbaked area has it baked a slice a frame, within the budget,
/// without waiting for the GPU, and the file is the one a whole bake makes.
/// </summary>
[Collection(GpuCollection.Name)]
[Trait("Speed", "Slow")]
[Trait("Needs", "GPU")]
public sealed class AutoBakeGpuTests(GpuFixture gpu)
{
    // Far more frames than the room's Preview bake takes, so a bake that never finishes fails rather than hangs.
    private const int MaxFrames = 2000;

    // The budget each test bakes at, in milliseconds a frame.
    private const float Budget = 4f;

    // Game time between frames: a twentieth of a second, so the lights settle after 20 frames.
    private const float FrameSeconds = 0.05f;

    [Fact]
    public void Missing_space_bake_is_baked_in_the_background()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad(gpu);
        BakeDone? done = null;
        try
        {
            var room = Room(gpu, quad);
            var view = RoomView(gpu, room);
            gpu.Renderer.AutoBake = new AutoBake(Budget, true, [BakeQuality.Preview], d => done = d);

            int frames = RenderUntil(view, room, () => done is not null);

            Assert.True(done is not null, $"No background bake was done after {frames} frames.");
            var bake = done.Value;
            Assert.Equal(BakeFiles.Space("auto-room"), bake.File);
            Assert.Equal("auto-room", bake.Name);
            Assert.Equal(BakeQuality.Preview, bake.Quality);
            Assert.Equal(gpu.Renderer.LastBakeFrame!.Space, bake.Inputs);
            var file = LightBakeFile.Read(bake.Bytes);
            Assert.Equal(BakeQuality.Preview, file.Quality);
            Assert.Equal(bake.Inputs, file.Inputs);
        }
        finally
        {
            gpu.Renderer.AutoBake = null;
            gpu.Renderer.DestroyMesh(quad);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Background_bake_never_waits_for_the_gpu()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad(gpu);
        BakeDone? done = null;
        try
        {
            var room = Room(gpu, quad);
            var view = RoomView(gpu, room);

            // Setting it prepares baking, which may wait the first time; the frames after it never do.
            gpu.Renderer.AutoBake = new AutoBake(Budget, true, [BakeQuality.Preview], d => done = d);
            int waits = gpu.Context.SubmitAndWaitCount;

            int frames = RenderUntil(view, room, () => done is not null);

            Assert.True(done is not null, $"No background bake was done after {frames} frames.");
            Assert.Equal(waits, gpu.Context.SubmitAndWaitCount);
        }
        finally
        {
            gpu.Renderer.AutoBake = null;
            gpu.Renderer.DestroyMesh(quad);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Background_bake_keeps_every_slice_within_budget()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad(gpu);
        BakeDone? done = null;
        try
        {
            var room = Room(gpu, quad);

            // Dense enough that a trace pass takes about a quarter of a millisecond on a desktop GPU, so 64 passes in
            // one slice would be far over the budget, while one page's filter still fits. Final, since a Preview
            // traces only 64 passes, which a slow ramp would still be ramping through.
            var view = RoomView(gpu, room, texelsPerMetre: 180);
            gpu.Renderer.AutoBake = new AutoBake(Budget, true, [BakeQuality.Final], d => done = d);

            // The first slices find out how long a pass takes, so only those from the fourth on must fit.
            int measured = gpu.Renderer.BakeSlicesMeasured;
            int slices = 0;
            float largest = 0f;
            int largestAt = -1;
            int frames = RenderUntil(view, room, () =>
            {
                if (gpu.Renderer.BakeSlicesMeasured != measured && gpu.Renderer.BakeProgress is { } progress)
                {
                    measured = gpu.Renderer.BakeSlicesMeasured;
                    slices++;
                    if (slices >= 4 && progress.LastSliceMilliseconds > largest)
                    {
                        largest = progress.LastSliceMilliseconds;
                        largestAt = slices;
                    }
                }

                return done is not null;
            });

            Assert.True(done is not null, $"No background bake was done after {frames} frames.");
            Assert.True(slices >= 4, $"Only {slices} slices were measured, so the GPU may not time them.");
            Assert.True(largest <= 1.5f * Budget,
                $"Slice {largestAt} of {slices} took {largest} ms, more than 1.5 times the {Budget} ms budget.");
        }
        finally
        {
            gpu.Renderer.AutoBake = null;
            gpu.Renderer.DestroyMesh(quad);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Background_bake_matches_a_whole_bake()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad(gpu);
        BakeDone? done = null;
        try
        {
            var room = Room(gpu, quad);
            var view = RoomView(gpu, room);
            gpu.Renderer.AutoBake = new AutoBake(Budget, true, [BakeQuality.Preview], d => done = d);

            int frames = RenderUntil(view, room, () => done is not null);
            Assert.True(done is not null, $"No background bake was done after {frames} frames.");
            gpu.Renderer.AutoBake = null;

            var whole = gpu.Renderer.Bake(view, room, new BakeSettings(4, BakeQuality.Preview));

            Assert.Equal(Normal(whole), Normal(done.Value.Bytes));
        }
        finally
        {
            gpu.Renderer.AutoBake = null;
            gpu.Renderer.DestroyMesh(quad);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Background_bake_waits_until_lights_settle()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad(gpu);
        BakeDone? done = null;
        try
        {
            var room = Room(gpu, quad);
            var view = RoomView(gpu, room);
            gpu.Renderer.AutoBake = new AutoBake(Budget, true, [BakeQuality.Preview], d => done = d);

            // Time going back starts the watch for moving lights over, so the lights settle a second after this.
            const float start = -1000f;
            int frame = 0;
            for (; start + frame * FrameSeconds < start + AnimatedLights.SettleSeconds - FrameSeconds; frame++)
            {
                view.Time = start + frame * FrameSeconds;
                gpu.Renderer.Render(view, room);
                Assert.True(gpu.Renderer.BakeProgress is null,
                    $"A background bake started at frame {frame}, {frame * FrameSeconds} seconds in, before the lights settled.");
            }

            Assert.True(frame > 10, $"Only {frame} frames were drawn before the lights settled.");
            int unsettled = frame;
            for (; gpu.Renderer.BakeProgress is null; frame++)
            {
                Assert.True(frame < unsettled + 100, "No background bake started in the 100 frames after the lights settled.");
                view.Time = start + frame * FrameSeconds;
                gpu.Renderer.Render(view, room);
            }
        }
        finally
        {
            gpu.Renderer.AutoBake = null;
            gpu.Renderer.DestroyMesh(quad);
        }

        Assert.Null(done);
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void No_trace_never_bakes()
    {
        byte[] without = DrawWithoutTracing(autoBake: false);
        byte[] with = DrawWithoutTracing(autoBake: true);

        Assert.True(without.SequenceEqual(with), "A renderer that can't trace drew another frame with AutoBake set.");
    }

    // Draws the room on a renderer that can't trace, with or without AutoBake, and captures the last frame.
    private static byte[] DrawWithoutTracing(bool autoBake)
    {
        using var fresh = new GpuFixture(disableRayTracing: true);
        Assert.False(fresh.Renderer.CanTrace);
        var quad = CreateQuad(fresh);
        var room = Room(fresh, quad);
        var view = RoomView(fresh, room);
        if (autoBake)
        {
            fresh.Renderer.AutoBake = new AutoBake(Budget, true, [BakeQuality.Preview],
                _ => Assert.Fail("A renderer that can't trace finished a background bake."));
        }

        for (int frame = 0; frame < 60; frame++)
        {
            view.Time = frame * FrameSeconds;
            fresh.Renderer.Render(view, room);
            Assert.Null(fresh.Renderer.BakeProgress);
        }

        view.Time = 60 * FrameSeconds;
        byte[] pixels = fresh.Renderer.Capture(view, room, out _, out _);
        fresh.Renderer.DestroyMesh(quad);
        Assert.Equal(0, fresh.ValidationErrors);
        return pixels;
    }

    // Draws the room a frame at a time, game time moving on, until done says so or MaxFrames have been drawn.
    private int RenderUntil(FrameView view, DrawItem[] room, Func<bool> done)
    {
        for (int frame = 0; frame < MaxFrames; frame++)
        {
            view.Time = frame * FrameSeconds;
            gpu.Renderer.Render(view, room);
            if (done())
            {
                return frame + 1;
            }
        }

        return MaxFrames;
    }

    // The file as it would be had it taken no time, the only thing two bakes of the same frame may differ in.
    private static byte[] Normal(byte[] bytes)
    {
        var file = LightBakeFile.Read(bytes);
        return (file with { Report = file.Report with { Time = TimeSpan.Zero } }).ToBytes();
    }

    // A box room open at the top and front: a 4 metre floor and three walls 2 metres high.
    private static DrawItem[] Room(GpuFixture g, MeshHandle quad) =>
    [
        Draw(g, quad, Matrix4x4.CreateScale(2f) * Matrix4x4.CreateRotationX(-MathF.PI / 2f), 1),
        Draw(g, quad, Matrix4x4.CreateScale(2f, 1f, 1f) * Matrix4x4.CreateTranslation(0f, 1f, -2f), 2),
        Draw(g, quad, Matrix4x4.CreateScale(2f, 1f, 1f) * Matrix4x4.CreateRotationY(MathF.PI / 2f) * Matrix4x4.CreateTranslation(-2f, 1f, 0f), 3),
        Draw(g, quad, Matrix4x4.CreateScale(2f, 1f, 1f) * Matrix4x4.CreateRotationY(-MathF.PI / 2f) * Matrix4x4.CreateTranslation(2f, 1f, 0f), 4),
    ];

    // The room's frame, drawn once without lights, then lit by a Baked point light in the middle and a Mixed sun.
    private static FrameView RoomView(GpuFixture g, DrawItem[] room, int texelsPerMetre = 4)
    {
        var view = g.View(DebugView.None);
        view.LocalLights = [];
        view.Lights = [];
        g.Renderer.Render(view, room);
        view.BakedSpace = new BakedSpace("auto-room", new Vector3(-6f, -3f, -6f), new Vector3(6f, 7f, 6f), 2f, texelsPerMetre);
        view.LocalLights = new[]
        {
            new LocalLight
            {
                Mode = LightMode.Baked, Key = 1401, Position = new Vector3(0f, 1f, 0f), Radiance = new Vector3(10f),
                Range = 10f, Radius = 0.1f,
            },
        };
        view.Lights = new[]
        {
            new DirectionalLight
            {
                Mode = LightMode.Mixed, Direction = Vector3.Normalize(new Vector3(-0.3f, -1f, 0.4f)),
                Radiance = new Vector3(2f), CastsShadow = true,
            },
        };
        return view;
    }

    private static DrawItem Draw(GpuFixture g, MeshHandle mesh, Matrix4x4 transform, ulong instance) => new()
    {
        Mesh = mesh, IndexCount = 6, Material = g.Matte, Transform = transform, BakeId = BakeIds.Of(instance << 12, 0),
    };

    // The test quad with its second UVs the same as its first, so it has a lightmap rectangle.
    private static MeshHandle CreateQuad(GpuFixture g)
    {
        var (vertices, indices) = TestMeshes.Quad();
        for (int i = 0; i < vertices.Length; i++)
        {
            vertices[i].Uv2 = vertices[i].Uv;
        }

        var handle = g.Renderer.CreateMesh(vertices, indices);
        g.RenderOneFrame();
        return handle;
    }
}
