using System.Numerics;

namespace Rendition.Gpu.Tests;

// When a fresh bake takes a Baked sun over, the frame is drawn again without it. The clear sky is still the sky that
// sun lights, so it is worked out once from the original view: it isn't baked again for the frame without the sun,
// and its disc still shows.
[Collection(GpuCollection.Name)]
[Trait("Speed", "Slow")]
[Trait("Needs", "GPU")]
public sealed class BakedSunGpuTests(GpuFixture gpu)
{
    private static readonly BakedSpace Room = new("room", new Vector3(-3f, -3f, -3f), new Vector3(3f, 3f, 3f), 1f);

    [Fact]
    public void Dropped_baked_sun_keeps_the_sky_and_its_disc()
    {
        int errors = gpu.ValidationErrors;
        var (vertices, indices) = TestMeshes.Cube();
        var cube = gpu.Renderer.Assets.CreateDrawItem(vertices, indices, gpu.Matte) with { BakeId = BakeIds.Of(1, 0) };
        DirectionalLight[] suns =
        [
            new()
            {
                Mode = LightMode.Baked,
                Direction = Vector3.Normalize(new Vector3(0.4f, 1f, 0.6f)),
                Radiance = new Vector3(1.5f, 1.4f, 1.2f),
                CastsShadow = true,
            },
        ];
        var view = gpu.View(DebugView.None);
        view.Environment.Sky = new AnalyticSky { Turbidity = 3.5f };
        view.Lights = suns;
        view.Shadows = new ShadowSettings { Radius = 4f, LightAngle = 0.1f };
        view.Baked.Space = Room;

        int rebakes = gpu.Renderer.SkyRebakes;
        gpu.Renderer.Baking.Bakes = new EveryBakeFresh();
        try
        {
            // A sun is named by its index, so another test's sun at index 0 makes this one look like it moved on the
            // first frame, which keeps it live. Two seconds apart, it has held still long enough by the next.
            for (int i = 0; i < 3; i++)
            {
                view.Time = i * 2f;
                gpu.Renderer.Render(view, [cube]);
            }

            // The sun was really dropped, so the frame was drawn a second time without it.
            Assert.Equal([0], gpu.Renderer.Baking.LastFrame!.DroppedSuns);
            // The sky changed once, to this sun, and stayed: the frame without the sun didn't bake a sunless sky.
            Assert.Equal(1, gpu.Renderer.SkyRebakes - rebakes);
            var disc = gpu.Renderer.Last.SunDisc;
            Assert.True(disc.X + disc.Y + disc.Z > 0f, $"The sun's disc is dark: {disc}.");
        }
        finally
        {
            gpu.Renderer.Baking.Bakes = NoBakes.Instance;
            gpu.Renderer.Assets.DestroyMesh(cube.Mesh);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    // The physical sky tints the sun once. The frame drawn again without a baked local light keeps that one tint,
    // as bright and as warm as a frame with nothing baked.
    [Fact]
    public void Frame_without_a_baked_local_light_tints_the_sun_once()
    {
        var (vertices, indices) = TestMeshes.Cube();
        var cube = gpu.Renderer.Assets.CreateDrawItem(vertices, indices, gpu.Matte) with { BakeId = BakeIds.Of(1, 0) };
        var view = gpu.View(DebugView.None);
        view.Environment.Sky = new AnalyticSky { Turbidity = 3.5f };
        view.Lights = new DirectionalLight[]
        {
            new()
            {
                Direction = Vector3.Normalize(new Vector3(0.9f, 0.25f, 0.3f)),
                Radiance = new Vector3(3f),
                CastsShadow = true,
            },
        };
        view.LocalLights = new LocalLight[]
        {
            new LocalLight
            {
                Mode = LightMode.Baked, Key = 1601, Position = new Vector3(0f, 1f, 0f), Radiance = new Vector3(2f),
                Range = 10f, Radius = 0.1f,
            },
        };
        view.Shadows = new ShadowSettings { Radius = 4f, LightAngle = 0.1f };
        view.Baked.Space = Room;

        try
        {
            gpu.Renderer.Render(view, [cube]);
            var once = gpu.Renderer.Last.Light(0).Radiance;

            gpu.Renderer.Baking.Bakes = new EveryBakeFresh();
            for (int i = 0; i < 3; i++)
            {
                view.Time = i * 2f;
                gpu.Renderer.Render(view, [cube]);
            }

            Assert.Equal([0], gpu.Renderer.Baking.LastFrame!.DroppedLocalLights);
            var drawn = gpu.Renderer.Last.Light(0).Radiance;
            Assert.True(Vector3.Distance(once, drawn) < 1e-4f, $"The sun was {once} with nothing baked but {drawn} without the baked light.");
        }
        finally
        {
            gpu.Renderer.Baking.Bakes = NoBakes.Instance;
            gpu.Renderer.Assets.DestroyMesh(cube.Mesh);
        }
    }

    // A frame drawn again without its baked sun leaves the other lights as they were: the air tints the sun, and a
    // fill that is left is not taken for it.
    [Fact]
    public void Fill_left_after_the_baked_sun_is_dropped_keeps_its_colour()
    {
        var (vertices, indices) = TestMeshes.Cube();
        var cube = gpu.Renderer.Assets.CreateDrawItem(vertices, indices, gpu.Matte) with { BakeId = BakeIds.Of(1, 0) };
        var fill = new Vector3(0.4f, 0.3f, 0.2f);
        var view = gpu.View(DebugView.None);
        view.Environment.Sky = new AnalyticSky { Turbidity = 3.5f };
        view.Lights = new DirectionalLight[]
        {
            new()
            {
                Mode = LightMode.Baked,
                Direction = Vector3.Normalize(new Vector3(0.9f, 0.25f, 0.3f)),
                Radiance = new Vector3(3f),
                CastsShadow = true,
            },
            new() { Direction = Vector3.Normalize(new Vector3(-0.6f, 0.6f, -0.4f)), Radiance = fill },
        };
        view.Shadows = new ShadowSettings { Radius = 4f, LightAngle = 0.1f };
        view.Baked.Space = Room;

        gpu.Renderer.Baking.Bakes = new EveryBakeFresh();
        try
        {
            for (int i = 0; i < 3; i++)
            {
                view.Time = i * 2f;
                gpu.Renderer.Render(view, [cube]);
            }

            Assert.Equal([0], gpu.Renderer.Baking.LastFrame!.DroppedSuns);
            Assert.Equal(fill, gpu.Renderer.Last.Light(0).Radiance);
        }
        finally
        {
            gpu.Renderer.Baking.Bakes = NoBakes.Instance;
            gpu.Renderer.Assets.DestroyMesh(cube.Mesh);
        }
    }

    private sealed class EveryBakeFresh : IBakeStore
    {
        public bool IsFresh(string file, BakeInputs inputs) => true;
    }
}
