using System.Numerics;
using Rendition.Vulkan;

namespace Rendition.Gpu.Tests;

// A change of sky, a new gradient or a sun that moves, is recorded in the next frame's command buffer, so the sky can
// change every frame without the CPU waiting on the GPU. The blocking paths, a bake, still see the new sky first.
[Collection(GpuCollection.Name)]
[Trait("Speed", "Slow")]
[Trait("Needs", "GPU")]
public sealed class SkyUpdateGpuTests(GpuFixture gpu)
{
    private const int FramesInFlight = 2;

    private static readonly BakedSpace Room = new("sky-room", new Vector3(-3f, -3f, -3f), new Vector3(3f, 3f, 3f), 1f);

    [Fact]
    public void A_sun_moving_every_frame_rebakes_the_sky_every_frame_without_waiting()
    {
        int errors = gpu.ValidationErrors;
        var view = SunView(Towards(30f));
        gpu.Renderer.Render(view, []);
        var before = gpu.Renderer.ProjectSky();

        int rebakes = gpu.Renderer.SkyRebakes;
        int waits = gpu.Context.SubmitAndWaitCount;
        for (int frame = 1; frame <= 6; frame++)
        {
            gpu.Renderer.Render(SunView(Towards(30f + 2f * frame)), []);
        }

        Assert.Equal(waits, gpu.Context.SubmitAndWaitCount);
        Assert.Equal(6, gpu.Renderer.SkyRebakes - rebakes);
        var after = gpu.Renderer.ProjectSky();
        float moved = Vector3.Distance(Rgb(before[0]), Rgb(after[0]));
        Assert.True(moved > 1e-3f, $"The sky's constant band moved {moved} after the sun turned 12 degrees.");
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void A_new_gradient_shows_on_the_next_frame_without_waiting()
    {
        int errors = gpu.ValidationErrors;
        var colour = new Vector3(0.25f, 0.5f, 0.75f);
        try
        {
            gpu.Renderer.SetEnvironment(Even(new Vector3(1f, 0.5f, 0.25f)));
            gpu.RenderOneFrame();

            int waits = gpu.Context.SubmitAndWaitCount;
            gpu.Renderer.SetEnvironment(Even(colour));
            Assert.Equal(waits, gpu.Context.SubmitAndWaitCount);
            gpu.RenderOneFrame();

            // An even sky of radiance L projects to 4 pi Y0 L = 3.5449 L in the constant band.
            AssertClose(3.5449f * colour, Rgb(gpu.Renderer.ProjectSky()[0]), 2e-3f);
        }
        finally
        {
            gpu.Renderer.SetEnvironment(DefaultEnvironment());
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void A_sky_change_before_a_bake_reaches_the_bake()
    {
        int errors = gpu.ValidationErrors;
        var (vertices, indices) = TestMeshes.Cube();
        var cube = gpu.Renderer.CreateDrawItem(vertices, indices, gpu.Matte) with { BakeId = BakeIds.Of(1, 0) };
        try
        {
            gpu.Renderer.Render(RoomView(Towards(20f)), [cube]);

            // The bake is the first thing to see the sun towards B: no frame has drawn it.
            var bake = gpu.Renderer.Bake(RoomView(Towards(70f)), [cube], new BakeSettings(Room.TexelsPerMetre, BakeQuality.Preview));
            Assert.NotEmpty(bake);
            var baked = gpu.Renderer.ProjectSky();

            // A frame under B projects the sky the bake should have seen.
            gpu.Renderer.Render(RoomView(Towards(70f)), [cube]);
            var drawn = gpu.Renderer.ProjectSky();
            for (int k = 0; k < 4; k++)
            {
                AssertClose(Rgb(drawn[k]), Rgb(baked[k]), 1e-3f);
            }
        }
        finally
        {
            gpu.Renderer.DestroyMesh(cube.Mesh);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Flat_settles_on_the_new_sky_within_the_frames_in_flight()
    {
        int errors = gpu.ValidationErrors;
        var colour = new Vector3(0.75f, 0.25f, 0.5f);
        try
        {
            gpu.Renderer.SetEnvironment(Even(new Vector3(0.125f, 0.375f, 1f)));
            gpu.RenderOneFrame();

            gpu.Renderer.SetEnvironment(Even(colour));
            for (int i = 0; i < FramesInFlight + 1; i++)
            {
                gpu.RenderOneFrame();
            }

            AssertClose(colour, gpu.Renderer.LastFlat.Irradiance, 2e-3f);
        }
        finally
        {
            gpu.Renderer.SetEnvironment(DefaultEnvironment());
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void A_sky_that_stops_changing_finishes_its_rough_reflections_in_the_frames_after()
    {
        int errors = gpu.ValidationErrors;
        var colour = new Vector3(0.25f, 0.75f, 0.5f);
        try
        {
            gpu.Renderer.SetEnvironment(Even(new Vector3(1f, 0.125f, 0.25f)));
            gpu.RenderOneFrame();

            // One frame takes the change and a slice; the frames after it, with nothing new, take the rest.
            gpu.Renderer.SetEnvironment(Even(colour));
            for (int i = 0; i < RoughSlices.Count + FramesInFlight; i++)
            {
                gpu.RenderOneFrame();
            }

            AssertClose(colour, gpu.Renderer.LastFlat.Reflection, 2e-3f);
        }
        finally
        {
            gpu.Renderer.SetEnvironment(DefaultEnvironment());
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void A_sky_set_before_the_first_frame_reaches_every_reflection_level_in_that_frame()
    {
        var colour = new Vector3(0.25f, 0.75f, 0.5f);
        using var fresh = new GpuFixture(disableRayTracing: false);
        int errors = fresh.ValidationErrors;

        // A game sets its sky before its first frame; a screenshot of that frame must not show the default in the
        // rough reflections.
        fresh.Renderer.SetEnvironment(Even(colour));
        fresh.RenderOneFrame();
        fresh.Context.Api.DeviceWaitIdle(fresh.Context.Device).Check();

        // The next frame reads back what the first one left in the roughest level.
        fresh.RenderOneFrame();
        AssertClose(colour, fresh.Renderer.LastFlat.Reflection, 2e-3f);
        Assert.Equal(errors, fresh.ValidationErrors);
    }

    // Towards a sun the given degrees above the horizon, off to the side.
    private static Vector3 Towards(float elevation)
    {
        float radians = elevation * MathF.PI / 180f;
        return Vector3.Normalize(new Vector3(MathF.Cos(radians) * 0.8f, MathF.Sin(radians), MathF.Cos(radians) * 0.6f));
    }

    private FrameView SunView(Vector3 towards)
    {
        var view = gpu.View(DebugView.None);
        view.Sky = new AnalyticSky { Turbidity = 3f };
        view.Lights = new[]
        {
            new DirectionalLight
            {
                Mode = LightMode.Realtime, Direction = towards, Radiance = new Vector3(3f), CastsShadow = true,
            },
        };
        view.Shadows = new ShadowSettings { Radius = 4f, LightAngle = 0.1f };
        return view;
    }

    private FrameView RoomView(Vector3 towards)
    {
        var view = SunView(towards);
        view.BakedSpace = Room;
        return view;
    }

    private static EnvironmentDescription Even(Vector3 radiance) => new()
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

    private static Vector3 Rgb(Vector4 value) => new(value.X, value.Y, value.Z);

    private static void AssertClose(Vector3 expected, Vector3 actual, float tolerance)
    {
        Assert.True(Vector3.Distance(expected, actual) <= tolerance, $"Expected {expected}, got {actual}");
    }
}
