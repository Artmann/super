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
        var cube = gpu.Renderer.CreateDrawItem(vertices, indices, gpu.Matte) with { BakeId = BakeIds.Of(1, 0) };
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
        view.Sky = new AnalyticSky { Turbidity = 3.5f };
        view.Lights = suns;
        view.Shadows = new ShadowSettings { Radius = 4f, LightAngle = 0.1f };
        view.BakedSpace = Room;

        int rebakes = gpu.Renderer.SkyRebakes;
        gpu.Renderer.Bakes = new EveryBakeFresh();
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
            Assert.Equal([0], gpu.Renderer.LastBakeFrame!.DroppedSuns);
            // The sky changed once, to this sun, and stayed: the frame without the sun didn't bake a sunless sky.
            Assert.Equal(1, gpu.Renderer.SkyRebakes - rebakes);
            var disc = gpu.Renderer.LastSunDisc;
            Assert.True(disc.X + disc.Y + disc.Z > 0f, $"The sun's disc is dark: {disc}.");
        }
        finally
        {
            gpu.Renderer.Bakes = NoBakes.Instance;
            gpu.Renderer.DestroyMesh(cube.Mesh);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    private sealed class EveryBakeFresh : IBakeStore
    {
        public bool IsFresh(string file, BakeInputs inputs) => true;
    }
}
