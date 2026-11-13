using System.Globalization;
using System.Numerics;

namespace Rendition.Gpu.Tests;

/// <summary>
/// <see cref="Renderer.BakeFadeSeconds"/>: a bake that arrives while its area is drawn live fades in over that time
/// instead of popping in, and one fresh on the first frame that names its area shows at once. Every effect is off but
/// the baked lighting, and each sample is the 64th frame drawn at its time, so every sample lands on the same frame of
/// the dither's cycle and two samples differ only by the light.
/// </summary>
[Collection(GpuCollection.Name)]
[Trait("Speed", "Slow")]
[Trait("Needs", "GPU")]
public sealed class BakeFadeGpuTests(GpuFixture gpu)
{
    private const int Size = 256;
    private const int NoisePeriod = 64;
    private const int TexelsPerMetre = 4;
    private const RenderEffects OnlyBaked = RenderEffects.All & ~RenderEffects.BakedLighting;

    // Where the tests' clock starts, far from any other test's, so nothing they drew is mistaken for this one's.
    private const float Start = 500f;

    private static readonly BakedSpace Space =
        new("fade-room", new Vector3(-6f, -3f, -6f), new Vector3(6f, 7f, 6f), 2f, TexelsPerMetre);

    [Fact]
    public void Arriving_bake_fades_in_over_half_a_second()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        float fadeBefore = gpu.Renderer.BakeFadeSeconds;
        var store = new Given();
        try
        {
            gpu.Renderer.BakeFadeSeconds = 0.5f;
            var room = Room(quad);
            var view = RoomView(lit: false);
            var bake = SkyBake(view, room);
            gpu.Renderer.Bakes = store;

            // Drawn live, its bake missing; then the bake is saved and the next frame draws it.
            var live = Floor(view, room, Start);
            store.Bake = bake;
            var arrived = Floor(view, room, Start);
            var half = Floor(view, room, Start + 0.25f);
            var whole = Floor(view, room, Start + 0.5f);
            var baked = Floor(view, room, Start + 2f);

            Assert.True(MathF.Abs(baked.X - live.X) >= 6f,
                $"The baked floor reads {baked} and the live one {live}, too close to see a fade between them.");
            AssertWithin(live, arrived, 1f, "floor on the frame the bake arrived");
            AssertBetween(live, baked, half, "floor a quarter of a second after the bake arrived");
            AssertWithin(baked, whole, 1f, "floor half a second after the bake arrived");
        }
        finally
        {
            gpu.Renderer.Bakes = NoBakes.Instance;
            gpu.Renderer.BakeFadeSeconds = fadeBefore;
            gpu.Renderer.DestroyMesh(quad);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Bake_fresh_on_the_first_frame_shows_at_once()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        float fadeBefore = gpu.Renderer.BakeFadeSeconds;
        var store = new Given();
        try
        {
            gpu.Renderer.BakeFadeSeconds = 0.5f;
            var room = Room(quad);
            var view = RoomView(lit: false);
            var bake = SkyBake(view, room);
            gpu.Renderer.Bakes = store;
            var live = Floor(view, room, Start);

            // A frame that names no area forgets the room was drawn live, so the next frame is the first to name it,
            // as when a level loads with its bake already saved.
            var unnamed = view;
            unnamed.BakedSpace = null;
            gpu.Renderer.Render(unnamed, room);
            store.Bake = bake;
            var first = Floor(view, room, Start);
            var later = Floor(view, room, Start + 2f);

            Assert.True(MathF.Abs(later.X - live.X) >= 6f,
                $"The baked floor reads {later} and the live one {live}, too close to tell them apart.");
            AssertWithin(later, first, 1f, "floor on the first frame that names the room");
        }
        finally
        {
            gpu.Renderer.Bakes = NoBakes.Instance;
            gpu.Renderer.BakeFadeSeconds = fadeBefore;
            gpu.Renderer.DestroyMesh(quad);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Arriving_bake_never_dims_a_pixel_lit_by_a_baked_light()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        float fadeBefore = gpu.Renderer.BakeFadeSeconds;
        var store = new Given();
        try
        {
            gpu.Renderer.BakeFadeSeconds = 0.5f;
            var room = Room(quad);
            // No sky: the Baked point light is all that lights the floor, live or baked.
            var view = RoomView(lit: true);
            var bake = Bake(view, room);
            gpu.Renderer.Bakes = store;

            var live = Floor(view, room, Start);
            store.Bake = bake;
            var steps = new List<(float Time, Vector3 Floor)>();
            for (int step = 0; step <= 10; step++)
            {
                float since = step * 0.05f;
                steps.Add((since, Floor(view, room, Start + since)));
            }

            var baked = Floor(view, room, Start + 2f);

            Assert.True(live.X > 40f, $"The live floor reads {live}, too dark to see it dim.");
            Assert.True(baked.X > 40f, $"The baked floor reads {baked}, too dark to see it dim.");
            var least = Vector3.Min(live, baked);
            foreach (var (since, floor) in steps)
            {
                Assert.True(floor.X >= least.X - 3f && floor.Y >= least.Y - 3f && floor.Z >= least.Z - 3f,
                    $"{since} s after the bake arrived the floor reads {floor}, more than 3 steps below the live {live} and baked {baked}.");
            }
        }
        finally
        {
            gpu.Renderer.Bakes = NoBakes.Instance;
            gpu.Renderer.BakeFadeSeconds = fadeBefore;
            gpu.Renderer.DestroyMesh(quad);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Reflections_are_captured_again_when_a_fade_ends_as_on_a_fresh_load()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        float fadeBefore = gpu.Renderer.BakeFadeSeconds;
        var store = new Given();
        try
        {
            gpu.Renderer.BakeFadeSeconds = 0.5f;
            var room = Room(quad);
            var view = RoomView(lit: false);
            var bake = SkyBake(view, room);
            gpu.Renderer.Bakes = store;

            // A game captures its reflection probes again whenever this hash moves, so it must move once the fade ends:
            // a capture taken mid-fade would otherwise keep the half-faded room until something else changed.
            OneFrame(view, room, Start);
            int live = gpu.Renderer.ReflectionLightingHash(view);
            store.Bake = bake;
            OneFrame(view, room, Start);
            OneFrame(view, room, Start + 0.25f);
            int fading = gpu.Renderer.ReflectionLightingHash(view);
            OneFrame(view, room, Start + 2f);
            int faded = gpu.Renderer.ReflectionLightingHash(view);

            // The same room loaded with its bake already saved: the first frame that names it shows the bake whole.
            var unnamed = view;
            unnamed.BakedSpace = null;
            gpu.Renderer.Render(unnamed, room);
            OneFrame(view, room, Start + 3f);
            Assert.Equal(1f, gpu.Renderer.LastBakeFrame!.SpaceFade);
            int loaded = gpu.Renderer.ReflectionLightingHash(view);

            Assert.NotEqual(live, fading);
            Assert.NotEqual(fading, faded);
            Assert.Equal(loaded, faded);
        }
        finally
        {
            gpu.Renderer.Bakes = NoBakes.Instance;
            gpu.Renderer.BakeFadeSeconds = fadeBefore;
            gpu.Renderer.DestroyMesh(quad);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Zero_fade_shows_at_once()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        float fadeBefore = gpu.Renderer.BakeFadeSeconds;
        var store = new Given();
        try
        {
            gpu.Renderer.BakeFadeSeconds = 0f;
            var room = Room(quad);
            var view = RoomView(lit: false);
            var bake = SkyBake(view, room);
            gpu.Renderer.Bakes = store;

            var live = Floor(view, room, Start);
            store.Bake = bake;
            var arrived = Floor(view, room, Start);
            var later = Floor(view, room, Start + 2f);

            Assert.True(MathF.Abs(later.X - live.X) >= 6f,
                $"The baked floor reads {later} and the live one {live}, too close to tell them apart.");
            AssertWithin(later, arrived, 1f, "floor on the frame the bake arrived");
        }
        finally
        {
            gpu.Renderer.Bakes = NoBakes.Instance;
            gpu.Renderer.BakeFadeSeconds = fadeBefore;
            gpu.Renderer.DestroyMesh(quad);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Negative_fade_throws()
    {
        var original = CultureInfo.CurrentCulture;
        float fadeBefore = gpu.Renderer.BakeFadeSeconds;
        CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
        try
        {
            var error = Assert.Throws<ArgumentOutOfRangeException>(() => gpu.Renderer.BakeFadeSeconds = -0.5f);

            Assert.Contains("BakeFadeSeconds must be from 0 to 10, not -0.5.", error.Message);
            Assert.Equal(fadeBefore, gpu.Renderer.BakeFadeSeconds);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData(10.5f, "10.5")]
    [InlineData(float.NaN, "NaN")]
    [InlineData(float.PositiveInfinity, "Infinity")]
    public void Fade_over_ten_seconds_or_not_a_number_throws(float seconds, string shown)
    {
        float fadeBefore = gpu.Renderer.BakeFadeSeconds;

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => gpu.Renderer.BakeFadeSeconds = seconds);

        Assert.Contains($"BakeFadeSeconds must be from 0 to 10, not {shown}.", error.Message);
        Assert.Equal(fadeBefore, gpu.Renderer.BakeFadeSeconds);
    }

    // The room's floor at the middle of the picture: the 64th frame drawn at the time given.
    private Vector3 Floor(FrameView view, DrawItem[] room, float time)
    {
        view.Time = time;
        for (int i = 0; i < NoisePeriod - 1; i++)
        {
            gpu.Renderer.Render(view, room);
        }

        byte[] pixels = gpu.Renderer.Capture(view, room, out _, out _);
        int at = (Size / 2 * Size + Size / 2) * 4;
        return new Vector3(pixels[at], pixels[at + 1], pixels[at + 2]);
    }

    private void OneFrame(FrameView view, DrawItem[] room, float time)
    {
        view.Time = time;
        gpu.Renderer.Render(view, room);
    }

    private LightBakeFile Bake(FrameView view, DrawItem[] room) =>
        LightBakeFile.Read(gpu.Renderer.Bake(view, room, new BakeSettings(TexelsPerMetre, BakeQuality.Preview)));

    // The room baked under the full sky, though it is drawn without one: the floor is black live and lit by its bake,
    // so the two ends of a fade are far apart. The store calls any bake fresh, so this one is drawn.
    private LightBakeFile SkyBake(FrameView view, DrawItem[] room)
    {
        view.EnvironmentIntensity = 1f;
        return Bake(view, room);
    }

    // A box room open at the top and front: a 4 metre floor and three walls 2 metres high.
    private DrawItem[] Room(MeshHandle quad) =>
    [
        Draw(quad, Matrix4x4.CreateScale(2f) * Matrix4x4.CreateRotationX(-MathF.PI / 2f), 1),
        Draw(quad, Matrix4x4.CreateScale(2f, 1f, 1f) * Matrix4x4.CreateTranslation(0f, 1f, -2f), 2),
        Draw(quad, Matrix4x4.CreateScale(2f, 1f, 1f) * Matrix4x4.CreateRotationY(MathF.PI / 2f) * Matrix4x4.CreateTranslation(-2f, 1f, 0f), 3),
        Draw(quad, Matrix4x4.CreateScale(2f, 1f, 1f) * Matrix4x4.CreateRotationY(-MathF.PI / 2f) * Matrix4x4.CreateTranslation(2f, 1f, 0f), 4),
    ];

    // The room from above its open front, looking down at the middle of its floor, with no sky. Lit, a Baked point
    // light hangs a metre over that spot, so the floor is lit by it alone, live or baked; otherwise nothing lights it.
    private static FrameView RoomView(bool lit) => new()
    {
        Width = Size,
        Height = Size,
        View = Matrix4x4.CreateLookAt(new Vector3(0f, 2.5f, 4f), Vector3.Zero, Vector3.UnitY),
        Projection = Projection.PerspectiveInfiniteReversedZ(MathF.PI / 4f, 1f, 0.1f),
        CameraPosition = new Vector3(0f, 2.5f, 4f),
        EnvironmentIntensity = 0f,
        Off = OnlyBaked,
        BakedSpace = Space,
        LocalLights = lit
            ? new[]
            {
                new LocalLight
                {
                    Mode = LightMode.Baked, Key = 1501, Position = new Vector3(0f, 1f, 0f), Radiance = new Vector3(2f),
                    Range = 10f, Radius = 0.1f,
                },
            }
            : [],
    };

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

    private static void AssertWithin(Vector3 expected, Vector3 actual, float steps, string what)
    {
        var off = Vector3.Abs(actual - expected);
        Assert.True(off.X <= steps && off.Y <= steps && off.Z <= steps,
            $"The {what} reads {actual}, not within {steps} of {expected}.");
    }

    // Each channel strictly between its two ends, whichever is brighter.
    private static void AssertBetween(Vector3 a, Vector3 b, Vector3 actual, string what)
    {
        var low = Vector3.Min(a, b);
        var high = Vector3.Max(a, b);
        Assert.True(
            actual.X > low.X && actual.X < high.X && actual.Y > low.Y && actual.Y < high.Y
            && actual.Z > low.Z && actual.Z < high.Z,
            $"The {what} reads {actual}, not strictly between {a} and {b} in every channel.");
    }

    // A store with no bake until one is given, and then that bake, fresh for any inputs, as when a background bake
    // has just saved it.
    private sealed class Given : IBakeStore
    {
        public LightBakeFile? Bake { get; set; }

        public bool IsFresh(string file, BakeInputs inputs) => Bake is not null;

        public LightBakeFile? Load(string file) => Bake;
    }
}
