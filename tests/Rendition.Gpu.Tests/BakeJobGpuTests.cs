using System.Numerics;

namespace Rendition.Gpu.Tests;

/// <summary>
/// <see cref="BakeJob"/>: a bake run a step at a time, as a background bake runs it, gives the same file as
/// <see cref="Renderer.Bake"/> and <see cref="Renderer.BakeChunk"/>, asks to wait before every readback, and reports
/// its progress.
/// </summary>
[Collection(GpuCollection.Name)]
[Trait("Speed", "Slow")]
[Trait("Needs", "GPU")]
public sealed class BakeJobGpuTests(GpuFixture gpu)
{
    // Far more steps than any of these small bakes takes, so a job that never finishes fails rather than hangs.
    private const int MaxSteps = 20000;

    // Where the chunk stands in the world, so chunk space and world space differ.
    private static readonly Matrix4x4 Placed = Matrix4x4.CreateTranslation(10f, 0f, 5f);

    [Fact]
    public void Bake_in_single_passes_matches_a_whole_bake()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        try
        {
            var room = Room(quad);
            var view = RoomView(room);
            var settings = new BakeSettings(4, BakeQuality.Preview);

            var whole = gpu.Renderer.Bake(view, room, settings);
            byte[] stepped;
            using (var job = gpu.Renderer.CreateSpaceJob(view, room, settings))
            {
                RunInSinglePasses(job);
                stepped = job.Finish();
            }

            Assert.Equal(Normal(whole), Normal(stepped));
        }
        finally
        {
            gpu.Renderer.DestroyMesh(quad);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Chunk_job_matches_bake_chunk()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        try
        {
            var draws = ChunkDraws(quad);
            var view = ChunkView();
            var settings = new BakeSettings(8, BakeQuality.Preview);

            var whole = gpu.Renderer.BakeChunk(view, draws, 0, settings);
            byte[] stepped;
            using (var job = gpu.Renderer.CreateChunkJob(view, draws, 0, settings))
            {
                Assert.Equal(BakeFiles.Chunk("chunk", job.Inputs.Combined), job.File);
                Assert.Equal("chunk", job.Name);
                RunInSinglePasses(job);
                stepped = job.Finish();
            }

            Assert.Equal(Normal(whole), Normal(stepped));
        }
        finally
        {
            gpu.Renderer.DestroyMesh(quad);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Every_readback_step_asks_to_await_the_gpu()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        try
        {
            // The chunk reads back its sums, its filtered light and both runs of its transfer; the space its sums,
            // its filtered light and its probes.
            // Each job is made just before it runs, so the room's frame isn't drawn while the chunk's job holds the pages.
            var jobs = new Func<BakeJob>[]
            {
                () => gpu.Renderer.CreateChunkJob(ChunkView(), ChunkDraws(quad), 0, new BakeSettings(8, BakeQuality.Preview)),
                () => gpu.Renderer.CreateSpaceJob(RoomView(Room(quad)), Room(quad), new BakeSettings(4, BakeQuality.Preview)),
            };
            foreach (var create in jobs)
            {
                using (var job = create())
                {
                    var last = BakeStepResult.Continue;
                    int readBacks = 0;
                    for (int step = 0; last != BakeStepResult.Done; step++)
                    {
                        Assert.True(step < MaxSteps, $"The job {job.Name} didn't finish in {MaxSteps} steps.");
                        if (job.ReadsBack)
                        {
                            readBacks++;
                            Assert.True(last == BakeStepResult.AwaitGpu,
                                $"Step {step} of {job.Name} reads back, but the step before it returned {last}, not AwaitGpu.");
                        }

                        var result = BakeStepResult.Continue;
                        gpu.Context.SubmitAndWait(cmd => result = job.Step(cmd, 64));
                        last = result;
                    }

                    // Sums and filter for the one page, and for the chunk its two transfer runs, the space its probes.
                    Assert.True(readBacks >= 3, $"The job {job.Name} read back {readBacks} times, fewer than its three readbacks.");
                }
            }
        }
        finally
        {
            gpu.Renderer.DestroyMesh(quad);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Job_reports_progress_from_zero_to_one()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        try
        {
            var room = Room(quad);
            using var job = gpu.Renderer.CreateSpaceJob(RoomView(room), room, new BakeSettings(4, BakeQuality.Preview));
            Assert.Equal(0f, job.Progress);

            float before = 0f;
            bool between = false;
            var result = BakeStepResult.Continue;
            for (int step = 0; result != BakeStepResult.Done; step++)
            {
                Assert.True(step < MaxSteps, $"The job didn't finish in {MaxSteps} steps.");
                gpu.Context.SubmitAndWait(cmd => result = job.Step(cmd, 8));
                Assert.True(job.Progress >= before, $"Progress went down from {before} to {job.Progress} at step {step}.");
                between |= job.Progress > 0f && job.Progress < 1f;
                before = job.Progress;
            }

            Assert.Equal(1f, job.Progress);
            Assert.True(between, "Progress went from 0 to 1 with nothing in between.");
        }
        finally
        {
            gpu.Renderer.DestroyMesh(quad);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Finish_before_done_explains_itself()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        try
        {
            var room = Room(quad);
            using var job = gpu.Renderer.CreateSpaceJob(RoomView(room), room, new BakeSettings(4, BakeQuality.Preview));
            gpu.Context.SubmitAndWait(cmd => job.Step(cmd, 1));

            var error = Assert.Throws<InvalidOperationException>(() => job.Finish());
            Assert.Equal("The bake of \"job-room\" isn't done, so it has no file yet; step it until Step returns Done.", error.Message);
        }
        finally
        {
            gpu.Renderer.DestroyMesh(quad);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    // One pass a step, a submit and a wait for each, the slowest a background bake can go.
    private void RunInSinglePasses(BakeJob job)
    {
        var result = BakeStepResult.Continue;
        for (int step = 0; result != BakeStepResult.Done; step++)
        {
            Assert.True(step < MaxSteps, $"The job {job.Name} didn't finish in {MaxSteps} steps.");
            gpu.Context.SubmitAndWait(cmd => result = job.Step(cmd, 1));
            Assert.True(job.PassesInLastStep <= 1, $"Step {step} traced {job.PassesInLastStep} passes, more than the 1 asked for.");
        }
    }

    // The file as it would be had it taken no time, the only thing two bakes of the same frame may differ in.
    private static byte[] Normal(byte[] bytes)
    {
        var file = LightBakeFile.Read(bytes);
        return (file with { Report = file.Report with { Time = TimeSpan.Zero } }).ToBytes();
    }

    // A box room open at the top and front: a 4 metre floor and three walls 2 metres high.
    private DrawItem[] Room(MeshHandle quad) =>
    [
        Draw(quad, Matrix4x4.CreateScale(2f) * Matrix4x4.CreateRotationX(-MathF.PI / 2f), 1),
        Draw(quad, Matrix4x4.CreateScale(2f, 1f, 1f) * Matrix4x4.CreateTranslation(0f, 1f, -2f), 2),
        Draw(quad, Matrix4x4.CreateScale(2f, 1f, 1f) * Matrix4x4.CreateRotationY(MathF.PI / 2f) * Matrix4x4.CreateTranslation(-2f, 1f, 0f), 3),
        Draw(quad, Matrix4x4.CreateScale(2f, 1f, 1f) * Matrix4x4.CreateRotationY(-MathF.PI / 2f) * Matrix4x4.CreateTranslation(2f, 1f, 0f), 4),
    ];

    // The room's frame, drawn once without lights, then lit by a Baked point light in the middle and a Mixed sun.
    private FrameView RoomView(DrawItem[] room)
    {
        var view = gpu.View(DebugView.None);
        view.LocalLights = [];
        view.Lights = [];
        gpu.Renderer.Render(view, room);
        view.BakedSpace = new BakedSpace("job-room", new Vector3(-6f, -3f, -6f), new Vector3(6f, 7f, 6f), 2f, 4);
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

    // Plan 6's chunk: a 4 metre plane beside an 8 metre wall at its +x edge, placed away from the origin.
    private FrameView ChunkView()
    {
        var view = gpu.View(DebugView.None);
        view.BakedChunks = new[]
        {
            new BakedChunk("chunk", Placed, new Vector3(-1.5f, -1f, -1.5f), new Vector3(1.5f, 3f, 1.5f), 8),
        };
        view.LocalLights = [];
        return view;
    }

    private DrawItem[] ChunkDraws(MeshHandle quad) =>
    [
        Draw(quad, Matrix4x4.CreateScale(2f) * Matrix4x4.CreateRotationX(-MathF.PI / 2f) * Placed, 11),
        Draw(quad, Matrix4x4.CreateScale(4f) * Matrix4x4.CreateRotationY(-MathF.PI / 2f) * Matrix4x4.CreateTranslation(2f, 0f, 0f) * Placed, 12),
    ];

    private DrawItem Draw(MeshHandle mesh, Matrix4x4 transform, ulong instance) => new()
    {
        Mesh = mesh, IndexCount = 6, Material = gpu.Matte, Transform = transform, BakeId = BakeIds.Of(instance << 12, 0),
    };

    // The test quad with its second UVs the same as its first, so it has a lightmap rectangle.
    private MeshHandle CreateQuad()
    {
        var (vertices, indices) = TestMeshes.Quad();
        for (int i = 0; i < vertices.Length; i++)
        {
            vertices[i].Uv2 = vertices[i].Uv;
        }

        var handle = gpu.Renderer.CreateMesh(vertices, indices);
        gpu.RenderOneFrame();
        return handle;
    }
}
