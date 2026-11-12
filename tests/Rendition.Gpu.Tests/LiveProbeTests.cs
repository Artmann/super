using System.Numerics;

namespace Rendition.Gpu.Tests;

/// <summary>
/// The light probes as they update a few a frame: each test renders a scene from <see cref="ProbeScenes"/> until the
/// probes round the camera have all been updated, and reads them back through the real lookup in probes.glsl. A test
/// of the rasterized path makes a renderer of its own without ray queries; a test of the traced path uses the shared
/// renderer, with its probes made anew.
/// </summary>
[Collection(GpuCollection.Name)]
[Trait("Speed", "Slow")]
[Trait("Needs", "GPU")]
public sealed class LiveProbeTests(GpuFixture shared)
{
    private const int Size = 256;

    // Cascades 0 and 1 hold 32 by 16 by 32 probes each at High, and 32 are rasterized a frame, so they settle in about
    // 1,024 frames.
    private const int MaxFrames = 2000;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_red_wall_beside_a_probe_tints_it_red(bool traced)
    {
        var (red, mostUpdated) = Settled(
            traced, gpu => ProbeScenes.RedWall(gpu, lightIsSpot: false), new Vector3(0.7f, 0.3f, 0f), Vector3.UnitX);
        var (white, _) = Settled(
            traced, gpu => ProbeScenes.RedWall(gpu, lightIsSpot: false, wallIsWhite: true), new Vector3(0.7f, 0.3f, 0f), Vector3.UnitX);

        Assert.True(red.X > 0.01f, $"The red wall gave {red}.");
        Assert.True(red.X > 2f * red.Y && red.X > 2f * red.Z, $"The red wall gave {red}.");
        Assert.True(white.X > 0.01f && white.X < 1.2f * white.Y, $"The white wall gave {white}.");
        if (traced)
        {
            // Only the traced path updates more probes a frame than rasterizing can.
            Assert.True(mostUpdated > ProbeBudget.For(ProbeQuality.High).Raster, $"At most {mostUpdated} probes were updated in a frame.");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_spot_light_lights_the_probe_only_inside_its_cone(bool traced)
    {
        using var probes = Open(traced);
        var scene = probes.Scene(gpu => ProbeScenes.RedWall(gpu, lightIsSpot: true));
        Settle(probes.Gpu, scene);

        // Facing down, each reads the floor under it. The spot's 30° cone reaches 0.9 m from the origin on the floor,
        // so (0, 0, -1.5), 45° off its axis, is outside it; a point light would light it a third as brightly.
        var samples = probes.Gpu.Renderer.SampleProbeIrradiance(
        [
            (new Vector3(0f, 0.3f, 0f), -Vector3.UnitY),
            (new Vector3(0f, 0.3f, -1.5f), -Vector3.UnitY),
        ]);

        Assert.True(samples[0].X > 0.01f, $"Inside the cone the probes read {samples[0]}.");
        Assert.True(samples[1].X >= 0f, $"Outside the cone the probes read {samples[1]}.");
        Assert.True(samples[0].X >= 10f * samples[1].X, $"Inside the cone {samples[0]}, outside it {samples[1]}.");
        Assert.Equal(0, probes.NewValidationErrors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_closed_box_reads_black_under_a_bright_sky(bool traced)
    {
        using var probes = Open(traced);
        var scene = probes.Scene(ProbeScenes.ClosedBox);
        Settle(probes.Gpu, scene);

        var samples = probes.Gpu.Renderer.SampleProbeIrradiance(
        [
            (new Vector3(0.2f, 0.2f, 0.2f), Vector3.UnitY),
            (new Vector3(3f, 0.5f, 0f), Vector3.UnitX),
        ]);

        // At least 0 on every channel: -1 would mean no probe could tell, and the lighting would use the bright sky.
        var inside = samples[0];
        Assert.True(inside.X >= 0f && inside.Y >= 0f && inside.Z >= 0f, $"Inside the box the probes read {inside}.");
        Assert.True(inside.X < 0.01f && inside.Y < 0.01f && inside.Z < 0.01f, $"Inside the box the probes read {inside}.");
        Assert.True(samples[1].X > 1f, $"Outside the box the probes read {samples[1]}.");
        Assert.Equal(0, probes.NewValidationErrors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_probe_inside_a_solid_box_is_left_out(bool traced)
    {
        using var probes = Open(traced);
        var gpu = probes.Gpu;
        var scene = probes.Scene(ProbeScenes.SolidBox);
        Settle(gpu, scene);

        // Every probe inside the box sees only the backs of its faces, so none of them counts. What is left is either
        // nothing (-1) or the coarser cascades' probes outside the box: their weights through it are tiny, but the
        // lookup normalizes by the total, so they read as the sky. A probe inside taken as real would read black.
        float spacing = ProbeCascades.Layouts(ProbeQuality.High)[2].Spacing;
        Assert.True(spacing > 1.1f, $"Cascade 2's probes are {spacing} m apart, inside the box.");

        var inside = gpu.Renderer.SampleProbeIrradiance([(new Vector3(0.2f, 0.2f, 0.2f), Vector3.UnitY)])[0];

        Assert.True(inside == new Vector3(-1f) || inside.X > 1f, $"Inside the solid box the probes read {inside}.");
        Assert.Equal(0, probes.NewValidationErrors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_white_room_lit_by_one_light_settles_and_does_not_grow(bool traced)
    {
        using var probes = Open(traced);
        var gpu = probes.Gpu;
        var scene = probes.Scene(ProbeScenes.WhiteRoom);
        Settle(gpu, scene);
        if (traced)
        {
            // Traced probes settle in a few dozen frames, before any bounce has built up, and a traced update adds only a
            // quarter of what changed, so the bounce goes on growing for thousands of frames. It is let finish first, so
            // the readings below can tell a probe that keeps growing from one still filling in.
            Bounce(gpu, scene, traced);
        }

        var centre = new Vector3(0.2f, 1.3f, 0.2f);
        var readings = new List<float>();
        for (int frame = 0; frame <= 500; frame++)
        {
            if (frame % 50 == 0)
            {
                readings.Add(gpu.Renderer.SampleProbeIrradiance([(centre, Vector3.UnitY)])[0].X);
            }

            Render(gpu, scene, Vector3.Zero);
        }

        // 128 rays a probe are noisier than six rasterized faces, so the traced readings may wander a little more.
        float tolerance = traced ? 0.05f : 0.02f;
        Assert.True(readings[0] > 0.01f, $"The room's centre read {readings[0]}.");
        float last = readings[^1];
        float before = readings[^2];
        Assert.True(MathF.Abs(last - before) <= tolerance * before, $"The readings were {string.Join(", ", readings)}.");
        Assert.All(readings, reading => Assert.True(reading <= 2f * readings[0], $"The readings were {string.Join(", ", readings)}."));
        Assert.Equal(0, probes.NewValidationErrors);
    }

    [Fact]
    public void Traced_and_rasterized_probes_agree_on_a_simple_room()
    {
        (Vector3 Point, Vector3 Normal)[] points =
        [
            (new Vector3(0.2f, 1.3f, 0.2f), Vector3.UnitY),
            (new Vector3(0.2f, 1.3f, 0.2f), -Vector3.UnitY),
            (new Vector3(0.7f, 1.3f, 0.2f), Vector3.UnitX),
            (new Vector3(-0.7f, 1.3f, -0.7f), -Vector3.UnitZ),
            // Below the plane of probes at y = 3, which sit 5 cm under the ceiling: as close as a rasterized face's
            // near plane, so they see through it there and the paths can't agree.
            (new Vector3(0.2f, 2.2f, 0.2f), Vector3.UnitY),
        ];

        var rasterized = Converged(traced: false, points);
        var traced = Converged(traced: true, points);

        string report = string.Join("; ", points.Select((p, i) =>
            $"at {p.Point} facing {p.Normal} rasterized {Luminance(rasterized[i])}, traced {Luminance(traced[i])}"));
        for (int i = 0; i < points.Length; i++)
        {
            float r = Luminance(rasterized[i]);
            float t = Luminance(traced[i]);
            Assert.True(r > 0.01f, $"The rasterized probes read too little: {report}.");
            Assert.True(MathF.Abs(t - r) <= 0.15f * r, $"The two paths differ by more than 15%: {report}.");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_glass_pane_does_not_block_or_tint_the_probes(bool traced)
    {
        var point = new Vector3(0.7f, 0.3f, 0f);
        var (open, _) = Settled(traced, gpu => ProbeScenes.RedWall(gpu, lightIsSpot: false), point, Vector3.UnitX);
        var (glazed, _) = Settled(traced, ProbeScenes.RedWallBehindGlass, point, Vector3.UnitX);

        // The traced probes start again with the same rays, so the two match but for the pane.
        Assert.True(open.X > 0.01f, $"Without the pane the probes read {open}.");
        Assert.True(Vector3.Distance(open, glazed) <= 0.05f * open.Length(), $"Without the pane the probes read {open}, with it {glazed}.");
    }

    [Fact]
    public void Traced_shadow_rays_are_stopped_by_a_solid_pane_and_not_by_glass()
    {
        // Facing down, under the pane: the floor there is in the pane's shadow, and the rays see only the floor.
        var point = new Vector3(0.2f, 0.3f, 0.2f);
        var (open, _) = Settled(true, gpu => ProbeScenes.ShadedFloor(gpu, ProbeScenes.Pane.None), point, -Vector3.UnitY);
        var (solid, _) = Settled(true, gpu => ProbeScenes.ShadedFloor(gpu, ProbeScenes.Pane.Solid), point, -Vector3.UnitY);
        var (glass, _) = Settled(true, gpu => ProbeScenes.ShadedFloor(gpu, ProbeScenes.Pane.Glass), point, -Vector3.UnitY);

        Assert.True(open.X > 0.01f, $"Without the pane the probes read {open}.");
        Assert.True(solid.X < 0.5f * open.X, $"Without the pane the probes read {open}, under a solid one {solid}.");
        Assert.True(Vector3.Distance(open, glass) <= 0.05f * open.Length(), $"Without the pane the probes read {open}, under glass {glass}.");
    }

    [Fact]
    public void Scrolling_the_camera_keeps_the_probes_it_did_not_leave()
    {
        using var gpu = new GpuFixture(disableRayTracing: true);
        var scene = ProbeScenes.RedWall(gpu, lightIsSpot: false);
        Settle(gpu, scene);
        var point = new Vector3(0.7f, 0.3f, 0f);
        var before = gpu.Renderer.SampleProbeIrradiance([(point, Vector3.UnitX)])[0];

        Render(gpu, scene, new Vector3(0f, 0f, 1f));
        var after = gpu.Renderer.SampleProbeIrradiance([(point, Vector3.UnitX)])[0];

        Assert.True(before.X > 0.01f, $"Before the move the probes read {before}.");
        Assert.True(Vector3.Distance(before, after) <= 0.01f * before.Length(), $"Before the move {before}, after it {after}.");
        Assert.Equal(0, gpu.ValidationErrors);
    }

    [Fact]
    public void No_frame_on_the_load_path_waits_for_probes()
    {
        using var gpu = new GpuFixture(disableRayTracing: true);
        var scene = ProbeScenes.RedWall(gpu, lightIsSpot: false);

        Render(gpu, scene, Vector3.Zero);

        Assert.InRange(gpu.Renderer.ProbeUpdatesLastFrame, 1, ProbeBudget.For(ProbeQuality.High).Raster);
        Assert.False(gpu.Renderer.ProbesSettled);
        Assert.Equal(0, gpu.ValidationErrors);
    }

    [Fact]
    public void A_frame_stops_taking_probes_at_its_triangle_budget()
    {
        using var gpu = new GpuFixture(disableRayTracing: true);
        var scene = ProbeScenes.RedWall(gpu, lightIsSpot: false);

        // One triangle is less than any probe sees here, so only the first probe of each batch is drawn; the rest
        // stay uncaptured for a later frame rather than being marked as done.
        gpu.Renderer.ProbeTrianglesPerFrame = 1;
        Render(gpu, scene, Vector3.Zero);
        int capped = gpu.Renderer.ProbeUpdatesLastFrame;
        gpu.Renderer.ProbeTrianglesPerFrame = Rendition.Passes.ProbeCapturePass.MaxTrianglesPerFrame;
        Render(gpu, scene, Vector3.Zero);

        Assert.Equal(1, capped);
        Assert.Equal(ProbeBudget.For(ProbeQuality.High).Raster, gpu.Renderer.ProbeUpdatesLastFrame);
        Assert.Equal(0, gpu.ValidationErrors);
    }

    [Fact]
    public void A_teleport_forgets_every_probe_of_the_finest_cascade()
    {
        using var gpu = new GpuFixture(disableRayTracing: true);
        var scene = ProbeScenes.RedWall(gpu, lightIsSpot: false);
        Settle(gpu, scene);
        var before = gpu.Renderer.SampleProbeIrradiance([(new Vector3(0.7f, 0.3f, 0f), Vector3.UnitX)])[0];

        // Cascade 0 is 32 probes 0.5 m apart, so 20 m is wider than its box and every one of its slots is new. The probes
        // round x = 16.7 take the slots the probes round x = 0.7 had, with the same weights, so if the slots kept their
        // tiles the red wall's light would read there unchanged. No probe is updated, so only the invalidation can clear them.
        gpu.Renderer.ProbeUpdatesPaused = true;
        Render(gpu, scene, new Vector3(20f, 0f, 0f));
        var after = gpu.Renderer.SampleProbeIrradiance([(new Vector3(16.7f, 0.3f, 0f), Vector3.UnitX)])[0];

        Assert.True(before.X > 0.01f && before.X > 2f * before.Y, $"Before the teleport the probes read {before}.");
        Assert.True(Vector3.Distance(before, after) > 0.1f * before.Length(), $"Before the teleport {before}, after it {after}.");
        Assert.Equal(0, gpu.ValidationErrors);
    }

    [Fact]
    public void The_reflection_lighting_hash_keeps_still_when_a_scroll_unsettles_the_probes()
    {
        using var gpu = new GpuFixture(disableRayTracing: true);
        var scene = ProbeScenes.RedWall(gpu, lightIsSpot: false);
        Render(gpu, scene, Vector3.Zero);
        int unsettled = gpu.Renderer.ReflectionLightingHash(View(scene, Vector3.Zero));
        Settle(gpu, scene);
        int settled = gpu.Renderer.ReflectionLightingHash(View(scene, Vector3.Zero));

        // Half a metre is one cell of cascade 0, so the move brings in a slab of new probes and the probes are no longer
        // settled; the reflections were captured with settled probes and must not be captured again for it.
        Render(gpu, scene, new Vector3(0.5f, 0f, 0f));
        Assert.False(gpu.Renderer.ProbesSettled);
        int scrolled = gpu.Renderer.ReflectionLightingHash(View(scene, new Vector3(0.5f, 0f, 0f)));

        Assert.NotEqual(unsettled, settled);
        Assert.Equal(settled, scrolled);
        Assert.Equal(0, gpu.ValidationErrors);
    }

    [Fact]
    public void Scrolling_forgets_the_probes_it_left()
    {
        using var gpu = new GpuFixture(disableRayTracing: true);
        gpu.Renderer.ClearProbesForTest();
        gpu.Renderer.ProbeUpdatesPaused = true;
        Render(gpu, new ProbeScene([], [], Vector3.Zero), Vector3.Zero);

        // Cascade 0 is 16 m across, so moving 4 m along x hands the slots of the probes round x = -6.3 to the probes
        // round x = 9.7. The move clears them, so they read as uncaptured rather than as the green written here.
        WriteAround(gpu, new Vector3(-6.3f, 0.3f, 0.2f), new Vector3(0f, 1f, 0f));

        // Surfaces read the cascades once a frame starts with one written, so a frame in place comes first.
        Render(gpu, new ProbeScene([], [], Vector3.Zero), Vector3.Zero);
        var written = gpu.Renderer.SampleProbeIrradiance([(new Vector3(-6.3f, 0.3f, 0.2f), Vector3.UnitY)])[0];
        Assert.True(Vector3.Distance(written, new Vector3(0f, 1f, 0f)) < 1e-3f, $"The written probes read {written}.");

        Render(gpu, new ProbeScene([], [], Vector3.Zero), new Vector3(4f, 0f, 0f));
        var moved = gpu.Renderer.SampleProbeIrradiance([(new Vector3(9.7f, 0.3f, 0.2f), Vector3.UnitY)])[0];

        Assert.True(moved.Y < 0.5f, $"The probes round x = 9.7 read {moved}.");
        Assert.Equal(0, gpu.ValidationErrors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Moving_the_light_changes_the_probe_and_no_frame_goes_over_budget(bool traced)
    {
        const int MoveFrames = 100;
        const int AfterFrames = 300;
        const int LowFrom = (MoveFrames + AfterFrames) / 2;

        using var probes = Open(traced);
        var gpu = probes.Gpu;
        var scene = probes.Scene(gpu => ProbeScenes.RedWall(gpu, lightIsSpot: false));
        Settle(gpu, scene);

        // Settled counts only the two finest cascades, and a never-captured probe goes before a dirty one wherever it
        // is, so the coarse cascades are filled first: otherwise they would hold back the probes the light dirties.
        var layouts = ProbeCascades.Layouts(ProbeQuality.High);
        int coarse = (layouts[2].Total + layouts[3].Total) / Budget(ProbeQuality.High, traced);
        for (int frame = 0; frame < coarse; frame++)
        {
            Render(gpu, scene, Vector3.Zero);
        }

        var point = new Vector3(0.7f, 0.3f, 1.5f);
        var before = gpu.Renderer.SampleProbeIrradiance([(point, Vector3.UnitX)])[0];
        var start = scene.Lights[0].Position;

        // The light slides 2 m towards +z, nearer the point, over 100 frames. Half way through the 400 frames the
        // quality drops to Low, which makes the probes' storage again, and from that frame on a frame may update only
        // Low's count. The new storage captures every probe afresh, so the probe is also read just before the drop,
        // where only the light's move can have changed it. An update blends only part of the way to the new light, and
        // the probe has had about one by then, so that check asks for less.
        var budgets = new List<(int Frame, int Updated, int Budget)>();
        var beforeLow = Vector3.Zero;
        for (int frame = 1; frame <= MoveFrames + AfterFrames; frame++)
        {
            if (frame <= MoveFrames)
            {
                scene.Lights[0].Position = start + new Vector3(0f, 0f, 2f * frame / MoveFrames);
            }

            if (frame == LowFrom)
            {
                beforeLow = gpu.Renderer.SampleProbeIrradiance([(point, Vector3.UnitX)])[0];
                gpu.Renderer.ProbeQuality = ProbeQuality.Low;
            }

            Render(gpu, scene, Vector3.Zero);
            var quality = frame >= LowFrom ? ProbeQuality.Low : ProbeQuality.High;
            budgets.Add((frame, gpu.Renderer.ProbeUpdatesLastFrame, Budget(quality, traced)));
        }

        var after = gpu.Renderer.SampleProbeIrradiance([(point, Vector3.UnitX)])[0];

        Assert.All(budgets, entry => Assert.True(
            entry.Updated <= entry.Budget, $"Frame {entry.Frame} updated {entry.Updated} probes, over its budget of {entry.Budget}."));
        Assert.Contains(budgets, entry => entry.Frame < LowFrom && entry.Updated > Budget(ProbeQuality.Low, traced));
        Assert.True(before.X > 0.01f, $"Before the move the probes read {before}.");
        // This is the check that proves LightChanged works: the drop to Low recaptures every probe, so the last check
        // would pass without it. 1.2x is tuned to about one blend update, as hysteresis keeps a probe from jumping all
        // the way to the moved light's value at once.
        Assert.True(beforeLow.X >= 1.2f * before.X, $"Before the move the probes read {before}, before the drop to Low {beforeLow}.");
        Assert.True(after.X >= 1.5f * before.X, $"Before the move the probes read {before}, after it {after}.");
        Assert.Equal(0, probes.NewValidationErrors);
    }

    [Fact]
    public void The_probe_view_draws_a_red_tinted_sphere_beside_the_red_wall()
    {
        using var gpu = new GpuFixture(disableRayTracing: true);
        var scene = ProbeScenes.RedWall(gpu, lightIsSpot: false);
        Settle(gpu, scene);

        // From 3 m back the probe at (0.5, 0.5, 0) has only black sky behind it, so without a sphere the pixel is black.
        // The point 5 cm to its +x is on its sphere, 7.5 cm in radius, where the surface faces the red wall at x = 1.1.
        // No probe is updated, so the sphere shows what settled.
        gpu.Renderer.ProbeUpdatesPaused = true;
        byte[] pixels = Capture(gpu, scene, DebugView.Probes, RenderEffects.AntiAliasing);
        var (r, g, b) = PixelAt(pixels, new Vector3(0.55f, 0.5f, 0f));

        Assert.True(r > 20, $"The sphere's +x side is ({r}, {g}, {b}).");
        Assert.True(r > 2 * g && r > 2 * b, $"The sphere's +x side is ({r}, {g}, {b}).");
        Assert.Equal(0, gpu.ValidationErrors);
    }

    [Fact]
    public void With_light_probes_off_the_probe_view_draws_no_spheres_and_makes_no_storage()
    {
        using var gpu = new GpuFixture(disableRayTracing: true);
        var scene = ProbeScenes.RedWall(gpu, lightIsSpot: false);

        byte[] pixels = Capture(gpu, scene, DebugView.Probes, RenderEffects.AntiAliasing | RenderEffects.LightProbes);
        var (r, g, b) = PixelAt(pixels, new Vector3(0.55f, 0.5f, 0f));

        Assert.Equal((0, 0, 0), (r, g, b));
        Assert.False(gpu.Renderer.ProbeStorageAllocated);
        Assert.Equal(0, gpu.ValidationErrors);
    }

    // The probe view's camera: 3 m back from the origin and looking at it.
    private static readonly Vector3 ProbeViewCamera = new(0f, 0f, 3f);

    private static byte[] Capture(GpuFixture gpu, ProbeScene scene, DebugView debugView, RenderEffects off)
    {
        var view = View(scene, ProbeViewCamera);
        view.View = Matrix4x4.CreateLookAt(ProbeViewCamera, Vector3.Zero, Vector3.UnitY);
        view.DebugView = debugView;
        view.Off = off;
        byte[] pixels = gpu.Renderer.Capture(view, scene.Draws, out int width, out int height);
        Assert.Equal((Size, Size), (width, height));
        return pixels;
    }

    // The colour of the pixel a point lands on, seen from the probe view's camera.
    private static (int R, int G, int B) PixelAt(byte[] pixels, Vector3 point)
    {
        var viewProjection = Matrix4x4.CreateLookAt(ProbeViewCamera, Vector3.Zero, Vector3.UnitY)
            * Projection.PerspectiveInfiniteReversedZ(MathF.PI / 4f, 1f, 0.1f);
        var clip = Vector4.Transform(new Vector4(point, 1f), viewProjection);
        int x = (int)((clip.X / clip.W * 0.5f + 0.5f) * Size);
        // The projection already turns y down, as Vulkan's framebuffer runs.
        int y = (int)((clip.Y / clip.W * 0.5f + 0.5f) * Size);
        int at = (y * Size + x) * 4;
        return (pixels[at], pixels[at + 1], pixels[at + 2]);
    }

    private static int Budget(ProbeQuality quality, bool traced) =>
        traced ? ProbeBudget.For(quality).Traced : ProbeBudget.For(quality).Raster;

    private static float Luminance(Vector3 rgb) => 0.2126f * rgb.X + 0.7152f * rgb.Y + 0.0722f * rgb.Z;

    /// <summary>
    /// A renderer for one path with its probes made anew: the shared one, which traces, or one of its own without ray
    /// queries.
    /// </summary>
    private ProbeRenderer Open(bool traced)
    {
        if (!traced)
        {
            return new ProbeRenderer(new GpuFixture(disableRayTracing: true), owned: true);
        }

        Assert.True(shared.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        shared.Renderer.ResetProbesForTest();
        return new ProbeRenderer(shared, owned: false);
    }

    // Renders the scene on a renderer for the path until it settles, and samples it there. Also returns the most probes
    // any one frame updated.
    private (Vector3 Sample, int MostUpdated) Settled(bool traced, Func<GpuFixture, ProbeScene> make, Vector3 point, Vector3 normal)
    {
        using var probes = Open(traced);
        var scene = probes.Scene(make);
        int mostUpdated = Settle(probes.Gpu, scene);
        var sample = probes.Gpu.Renderer.SampleProbeIrradiance([(point, normal)])[0];
        Assert.Equal(0, probes.NewValidationErrors);
        return (sample, mostUpdated);
    }

    // The white room, with its probes updated again and again until the light bounced between its walls stops growing,
    // sampled at each point.
    private Vector3[] Converged(bool traced, (Vector3 Point, Vector3 Normal)[] points)
    {
        using var probes = Open(traced);
        var gpu = probes.Gpu;
        var scene = probes.Scene(ProbeScenes.WhiteRoom);
        Settle(gpu, scene);
        Bounce(gpu, scene, traced);
        var samples = gpu.Renderer.SampleProbeIrradiance(points);
        Assert.Equal(0, probes.NewValidationErrors);
        return samples;
    }

    // Updates the white room's probes again and again, until the light bounced between its walls has stopped growing.
    // Each update adds one more bounce, part of the way: in this room about three quarters of the light comes back, so
    // what is left to add shrinks by 1 - alpha / 4 an update. That is 0.875 rasterized, where alpha is 0.5, and 0.9375
    // traced, where it is 0.25, so 35 and 80 rounds each leave under 1%.
    private static void Bounce(GpuFixture gpu, ProbeScene scene, bool traced)
    {
        int rounds = traced ? 80 : 35;
        for (int round = 0; round < rounds; round++)
        {
            gpu.Renderer.MarkProbesDirtyForTest(new Vector3(0f, 1.5f, 0f), 2.8f);
            Settle(gpu, scene);
        }
    }

    // Renders until the probes settle, and returns the most probes any one frame updated.
    private static int Settle(GpuFixture gpu, ProbeScene scene)
    {
        int mostUpdated = 0;
        for (int frame = 0; frame < MaxFrames; frame++)
        {
            Render(gpu, scene, Vector3.Zero);
            mostUpdated = Math.Max(mostUpdated, gpu.Renderer.ProbeUpdatesLastFrame);
            if (gpu.Renderer.ProbesSettled)
            {
                return mostUpdated;
            }
        }

        Assert.Fail($"The probes had not settled after {MaxFrames} frames.");
        return mostUpdated;
    }

    private static void Render(GpuFixture gpu, ProbeScene scene, Vector3 camera)
    {
        gpu.Renderer.Render(View(scene, camera), scene.Draws);
    }

    private static FrameView View(ProbeScene scene, Vector3 camera)
    {
        return new FrameView
        {
            Width = Size,
            Height = Size,
            View = Matrix4x4.CreateLookAt(camera, camera - Vector3.UnitZ, Vector3.UnitY),
            Projection = Projection.PerspectiveInfiniteReversedZ(MathF.PI / 4f, 1f, 0.1f),
            CameraPosition = camera,
            LocalLights = scene.Lights,
            BackgroundRadiance = scene.Sky,
        };
    }

    // Writes the eight cascade-0 probes round a point, as the lookup finds them for a normal facing up.
    private static void WriteAround(GpuFixture gpu, Vector3 point, Vector3 irradiance)
    {
        var cascades = gpu.Renderer.LightProbeCascades;
        float spacing = ProbeCascades.Layouts(ProbeQuality.High)[0].Spacing;
        var biased = point + new Vector3(0f, 0.2f * spacing, 0f);
        var cell = new Int3(
            (int)MathF.Floor(biased.X / spacing), (int)MathF.Floor(biased.Y / spacing), (int)MathF.Floor(biased.Z / spacing));
        for (int corner = 0; corner < 8; corner++)
        {
            var at = new Int3(cell.X + (corner & 1), cell.Y + ((corner >> 1) & 1), cell.Z + ((corner >> 2) & 1));
            gpu.Renderer.WriteProbeTileForTest(new ProbeId(0, cascades.Slot(0, at)), irradiance);
        }
    }

    /// <summary>
    /// A renderer lent to one test: its own, disposed after, or the shared one, which gets back its scenes' meshes and
    /// materials, High probes and fresh probe storage, so the tests after it start as they would have.
    /// </summary>
    private sealed class ProbeRenderer(GpuFixture gpu, bool owned) : IDisposable
    {
        private readonly int _errorsAtStart = gpu.ValidationErrors;
        private readonly List<ProbeScene> _scenes = [];

        public GpuFixture Gpu => gpu;

        /// <summary>The validation messages since the renderer was lent.</summary>
        public int NewValidationErrors => gpu.ValidationErrors - _errorsAtStart;

        public ProbeScene Scene(Func<GpuFixture, ProbeScene> make)
        {
            var scene = make(gpu);
            _scenes.Add(scene);
            return scene;
        }

        public void Dispose()
        {
            if (owned)
            {
                gpu.Dispose();
                return;
            }

            var renderer = gpu.Renderer;
            foreach (var mesh in _scenes.SelectMany(scene => scene.Draws).Select(draw => draw.Mesh).Distinct())
            {
                renderer.DestroyMesh(mesh);
            }

            foreach (var material in _scenes.SelectMany(scene => scene.Draws).Select(draw => draw.Material).Distinct())
            {
                renderer.DestroyMaterial(material);
            }

            renderer.ProbeQuality = ProbeQuality.High;
            renderer.ProbeUpdatesPaused = false;
            renderer.ResetProbesForTest();
        }
    }
}
