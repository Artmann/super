using System.Numerics;
using Rendition;
using Super.Testing;

namespace Super.Tests;

public class ProbeSchedulerTests
{
    private static readonly Vector3 Forward = new(0f, 0f, -1f);

    [Fact]
    public void The_budgets_are_8_16_and_32_raster_probes_and_256_512_and_1024_traced()
    {
        Assert.Equal(new ProbeBudget(8, 256), ProbeBudget.For(ProbeQuality.Low));
        Assert.Equal(new ProbeBudget(16, 512), ProbeBudget.For(ProbeQuality.Medium));
        Assert.Equal(new ProbeBudget(32, 1024), ProbeBudget.For(ProbeQuality.High));
    }

    [Theory, Seeds(15)]
    public void A_frame_never_hands_out_more_probes_than_the_budget(int seed)
    {
        foreach (var quality in new[] { ProbeQuality.Low, ProbeQuality.Medium, ProbeQuality.High })
        {
            var budget = ProbeBudget.For(quality).Raster;
            var random = new Random(seed * 31 + (int)quality);
            var cascades = new ProbeCascades(quality);
            var scheduler = new ProbeScheduler(cascades, 0);
            var camera = new Vector3(random.NextSingle() * 20f, 1.5f, random.NextSingle() * 20f);

            for (int frame = 0; frame < 400; frame++)
            {
                var step = random.Next(40);
                if (step == 0)
                {
                    // A teleport, far enough to replace every cascade.
                    camera = new Vector3(random.NextSingle() * 400f - 200f, random.NextSingle() * 10f, random.NextSingle() * 400f - 200f);
                }
                else
                {
                    camera += new Vector3(random.NextSingle() - 0.5f, 0f, random.NextSingle() - 0.5f) * 0.4f;
                }

                scheduler.Scrolled(cascades.Move(camera));

                if (random.Next(15) == 0)
                {
                    scheduler.LightChanged(camera + new Vector3(random.NextSingle() * 6f - 3f, 0f, random.NextSingle() * 6f - 3f), 1f + random.NextSingle() * 3f);
                }

                if (random.Next(60) == 0)
                {
                    scheduler.EverythingChanged();
                }

                var batch = scheduler.Next(budget, camera, Forward);

                Assert.True(batch.Count <= budget, $"frame {frame} at {quality} handed out {batch.Count} probes for a budget of {budget}");
                Assert.Equal(batch.Count, batch.Distinct().Count());
                scheduler.Updated(batch);
            }
        }
    }

    [Fact]
    public void A_budget_of_zero_hands_out_nothing()
    {
        var cascades = new ProbeCascades(ProbeQuality.Low);
        var scheduler = new ProbeScheduler(cascades, 0);
        scheduler.Scrolled(cascades.Move(Vector3.Zero));

        Assert.Empty(scheduler.Next(0, Vector3.Zero, Forward));
    }

    [Fact]
    public void New_probes_near_the_camera_go_before_stale_far_ones()
    {
        var cascades = new ProbeCascades(ProbeQuality.Low);
        var scheduler = new ProbeScheduler(cascades, 0);
        var camera = new Vector3(0.25f, 1.1f, 0.25f);
        scheduler.Scrolled(cascades.Move(camera));
        CaptureEverything(scheduler, camera);

        for (int frame = 0; frame < 1000; frame++)
        {
            scheduler.Updated(scheduler.Next(64, camera, Forward));
        }

        camera += new Vector3(1f, 0f, 0f);
        var scroll = cascades.Move(camera);
        scheduler.Scrolled(scroll);

        var batch = scheduler.Next(8, camera, Vector3.Zero);

        // Moving 1 m is two cells of cascade 0, so it has new slots, and cascade 1 has some too.
        var newSlots = scroll.NewSlots(0);
        Assert.NotEmpty(newSlots);
        Assert.NotEmpty(scroll.NewSlots(1));
        Assert.Equal(8, batch.Count);
        Assert.All(batch, id =>
        {
            Assert.Equal(0, id.Cascade);
            Assert.Contains(id.Slot, newSlots);
        });

        var nearest = newSlots
            .Select(slot => Vector3.Distance(cascades.Position(0, slot), camera))
            .Order()
            .Take(8)
            .ToArray();
        var handedOut = batch
            .Select(id => Vector3.Distance(cascades.Position(0, id.Slot), camera))
            .Order()
            .ToArray();
        Assert.Equal(nearest, handedOut);
    }

    [Fact]
    public void Every_probe_is_updated_again_within_its_refresh_period()
    {
        var cascades = new ProbeCascades(ProbeQuality.Low);
        var scheduler = new ProbeScheduler(cascades, 0);
        var camera = new Vector3(0.25f, 1.1f, 0.25f);
        scheduler.Scrolled(cascades.Move(camera));

        var layouts = ProbeCascades.Layouts(ProbeQuality.Low);
        var counts = layouts.Select(layout => new int[layout.Total]).ToArray();
        int total = layouts.Sum(layout => layout.Total);
        int frames = (int)Math.Ceiling(total / 8.0) * 2;

        for (int frame = 0; frame < frames; frame++)
        {
            var batch = scheduler.Next(8, camera, Forward);
            foreach (var id in batch)
            {
                counts[id.Cascade][id.Slot]++;
            }

            scheduler.Updated(batch);
        }

        var starved = new List<ProbeId>();
        for (int c = 0; c < counts.Length; c++)
        {
            for (int slot = 0; slot < counts[c].Length; slot++)
            {
                if (counts[c][slot] < 2)
                {
                    starved.Add(new ProbeId(c, slot));
                }
            }
        }

        Assert.True(starved.Count == 0, $"{starved.Count} of {total} probes were updated fewer than twice in {frames} frames, such as {(starved.Count > 0 ? starved[0] : default)}");
    }

    [Fact]
    public void A_moved_light_marks_its_old_and_new_range_dirty_first()
    {
        var cascades = new ProbeCascades(ProbeQuality.Low);
        var scheduler = new ProbeScheduler(cascades, 0);
        var camera = new Vector3(0.25f, 1.1f, 0.25f);
        scheduler.Scrolled(cascades.Move(camera));
        CaptureEverything(scheduler, camera);
        Assert.True(scheduler.Settled);

        var a = new Vector3(1f, 1f, 1f);
        var b = new Vector3(-3f, 1f, 2f);
        scheduler.LightChanged(a, 2f);
        scheduler.LightChanged(b, 2f);

        var expected = new HashSet<ProbeId>();
        foreach (var layout in ProbeCascades.Layouts(ProbeQuality.Low).Select((l, c) => (l, c)))
        {
            for (int slot = 0; slot < layout.l.Total; slot++)
            {
                var position = cascades.Position(layout.c, slot);
                if (Vector3.Distance(position, a) <= 2f || Vector3.Distance(position, b) <= 2f)
                {
                    expected.Add(new ProbeId(layout.c, slot));
                }
            }
        }

        Assert.True(expected.Count > 100, "the test needs a good number of probes in range");

        var handedOut = new List<ProbeId>();
        while (handedOut.Count < expected.Count)
        {
            var batch = scheduler.Next(64, camera, Forward);
            handedOut.AddRange(batch);
            scheduler.Updated(batch);
        }

        var firstOnes = handedOut.Take(expected.Count).ToList();
        Assert.Equal(expected.Count, firstOnes.Distinct().Count());
        Assert.All(firstOnes, id => Assert.Contains(id, expected));
    }

    [Theory]
    [InlineData(38f, 5f, -54f, 3f)] // Near the camera: every cascade and the fixed grid.
    [InlineData(31f, 0.5f, -59f, 2.5f)] // At the fixed grid's corner, part of the sphere outside it.
    [InlineData(60f, 5f, -54f, 4f)] // Far: outside every box but the coarsest.
    [InlineData(37.3f, 4.2f, -55.1f, 0f)] // A range of 0 covers no probe unless one sits on the centre.
    public void A_light_dirties_exactly_the_captured_probes_within_its_range(float x, float y, float z, float range)
    {
        // The camera moves off the origin first, so the boxes wrap and a cell's slot isn't its index in the box.
        var cascades = new ProbeCascades(ProbeQuality.Low);
        var scheduler = new ProbeScheduler(cascades, 0);
        scheduler.Scrolled(cascades.Move(Vector3.Zero));
        var camera = new Vector3(37.3f, 4.2f, -55.1f);
        scheduler.Scrolled(cascades.Move(camera));
        var gridMin = new Vector3(30f, 0f, -60f);
        scheduler.SetFixedGrid(gridMin, 10, 4, 10, 1.5f);
        scheduler.Updated(AllProbes(scheduler.FixedGridProbes).ToArray());

        var center = new Vector3(x, y, z);
        scheduler.LightChanged(center, range);

        var expected = new HashSet<ProbeId>();
        var layouts = ProbeCascades.Layouts(ProbeQuality.Low);
        for (int c = 0; c < layouts.Length; c++)
        {
            for (int slot = 0; slot < layouts[c].Total; slot++)
            {
                if (Vector3.Distance(cascades.Position(c, slot), center) <= range)
                {
                    expected.Add(new ProbeId(c, slot));
                }
            }
        }

        for (int slot = 0; slot < scheduler.FixedGridProbes; slot++)
        {
            var position = gridMin + new Vector3(slot % 10, slot / 10 % 4, slot / 40) * 1.5f;
            if (Vector3.Distance(position, center) <= range)
            {
                expected.Add(new ProbeId(ProbeScheduler.FixedGrid, slot));
            }
        }

        var dirty = AllProbes(scheduler.FixedGridProbes).Where(scheduler.IsDirty).ToHashSet();
        Assert.True(range == 0f || expected.Count > 0, "the light should cover some probes");
        Assert.True(expected.SetEquals(dirty),
            $"{expected.Except(dirty).Count()} probes in range stayed clean and {dirty.Except(expected).Count()} out of range went dirty");
    }

    [Fact]
    public void A_sun_or_sky_change_keeps_every_probe_captured()
    {
        var cascades = new ProbeCascades(ProbeQuality.Low);
        var scheduler = new ProbeScheduler(cascades, 0);
        var camera = new Vector3(0.25f, 1.1f, 0.25f);
        scheduler.Scrolled(cascades.Move(camera));
        CaptureEverything(scheduler, camera);
        Assert.True(scheduler.Settled);

        scheduler.EverythingChanged();

        Assert.False(scheduler.Settled);
        foreach (var id in AllProbes(0))
        {
            Assert.True(scheduler.Captured(id), $"{id} lost its capture");
        }

        int frames = 0;
        while (!scheduler.Settled)
        {
            scheduler.Updated(scheduler.Next(64, camera, Forward));
            Assert.True(++frames < 1000, "it never settled");
        }

        Assert.True(scheduler.Settled);
        foreach (var id in AllProbes(0))
        {
            Assert.True(scheduler.Captured(id), $"{id} lost its capture");
        }
    }

    [Fact]
    public void Settled_waits_for_the_two_finest_cascades_and_the_fixed_grid()
    {
        var cascades = new ProbeCascades(ProbeQuality.Low);
        var scheduler = new ProbeScheduler(cascades, 27);
        scheduler.SetFixedGrid(new Vector3(-1f, 0f, -1f), 3, 3, 3, 1f);
        scheduler.Scrolled(cascades.Move(Vector3.Zero));
        var layouts = ProbeCascades.Layouts(ProbeQuality.Low);

        Assert.False(scheduler.Settled);

        scheduler.Updated(Probes(0, layouts[0].Total));
        scheduler.Updated(Probes(4, 27));
        Assert.False(scheduler.Settled);

        scheduler.Updated(Probes(1, layouts[1].Total - 1));
        Assert.False(scheduler.Settled);

        scheduler.Updated(new[] { new ProbeId(1, layouts[1].Total - 1) });
        Assert.True(scheduler.Settled);
        Assert.False(scheduler.Captured(new ProbeId(2, 0)));
        Assert.False(scheduler.Captured(new ProbeId(3, 0)));
    }

    [Fact]
    public void Settled_is_false_while_only_the_fixed_grid_is_missing()
    {
        var cascades = new ProbeCascades(ProbeQuality.Low);
        var scheduler = new ProbeScheduler(cascades, 8);
        scheduler.SetFixedGrid(Vector3.Zero, 2, 2, 2, 1f);
        scheduler.Scrolled(cascades.Move(Vector3.Zero));
        var layouts = ProbeCascades.Layouts(ProbeQuality.Low);

        scheduler.Updated(Probes(0, layouts[0].Total));
        scheduler.Updated(Probes(1, layouts[1].Total));
        scheduler.Updated(Probes(4, 7));

        Assert.False(scheduler.Settled);
    }

    [Fact]
    public void Clearing_the_fixed_grid_lets_the_cascades_settle_without_it()
    {
        var cascades = new ProbeCascades(ProbeQuality.Low);
        var scheduler = new ProbeScheduler(cascades, 8);
        scheduler.SetFixedGrid(Vector3.Zero, 2, 2, 2, 1f);
        scheduler.Scrolled(cascades.Move(Vector3.Zero));
        var layouts = ProbeCascades.Layouts(ProbeQuality.Low);
        scheduler.Updated(Probes(0, layouts[0].Total));
        scheduler.Updated(Probes(1, layouts[1].Total));
        Assert.False(scheduler.Settled);

        scheduler.ClearFixedGrid();

        Assert.Equal(0, scheduler.FixedGridProbes);
        Assert.True(scheduler.Settled);
        Assert.DoesNotContain(scheduler.Next(64, Vector3.Zero, Forward), id => id.Cascade == ProbeScheduler.FixedGrid);
    }

    [Fact]
    public void A_light_changing_inside_a_fixed_grid_dirties_its_probes()
    {
        var cascades = new ProbeCascades(ProbeQuality.Low);
        var scheduler = new ProbeScheduler(cascades, 27);
        scheduler.SetFixedGrid(new Vector3(100f, 0f, 100f), 3, 3, 3, 1f);
        scheduler.Scrolled(cascades.Move(Vector3.Zero));
        CaptureEverything(scheduler, Vector3.Zero);
        Assert.True(scheduler.Settled);

        // The grid probe at (101, 1, 101) is the middle one and its neighbours are 1 m away.
        scheduler.LightChanged(new Vector3(101f, 1f, 101f), 0.5f);

        Assert.False(scheduler.Settled);
        var batch = scheduler.Next(8, Vector3.Zero, Forward);
        Assert.Equal(new[] { new ProbeId(4, 13) }, batch.Take(1));
    }

    [Fact]
    public void Replacing_the_fixed_grid_makes_its_probes_never_captured()
    {
        var cascades = new ProbeCascades(ProbeQuality.Low);
        var scheduler = new ProbeScheduler(cascades, 8);
        scheduler.SetFixedGrid(Vector3.Zero, 2, 2, 2, 1f);
        scheduler.Scrolled(cascades.Move(Vector3.Zero));
        CaptureEverything(scheduler, Vector3.Zero);
        Assert.True(scheduler.Captured(new ProbeId(4, 3)));

        scheduler.SetFixedGrid(new Vector3(5f, 0f, 0f), 2, 2, 2, 1f);

        Assert.False(scheduler.Captured(new ProbeId(4, 3)));
        Assert.False(scheduler.Settled);
        Assert.True(scheduler.Captured(new ProbeId(0, 0)));
    }

    [Fact]
    public void The_same_frames_give_the_same_batches()
    {
        var first = Run(ProbeQuality.Medium);
        var second = Run(ProbeQuality.Medium);

        Assert.Equal(200, first.Count);
        Assert.Contains(first, batch => batch.Length > 0);
        for (int frame = 0; frame < first.Count; frame++)
        {
            Assert.True(first[frame].SequenceEqual(second[frame]), $"frame {frame} differs");
        }

        static List<ProbeId[]> Run(ProbeQuality quality)
        {
            var cascades = new ProbeCascades(quality);
            var scheduler = new ProbeScheduler(cascades, 0);
            var batches = new List<ProbeId[]>();

            for (int frame = 0; frame < 200; frame++)
            {
                var camera = new Vector3(frame * 0.07f, 1.5f + MathF.Sin(frame * 0.1f), MathF.Cos(frame * 0.05f) * 6f);
                var forward = Vector3.Normalize(new Vector3(MathF.Cos(frame * 0.03f), 0f, MathF.Sin(frame * 0.03f)));
                scheduler.Scrolled(cascades.Move(camera));

                if (frame % 37 == 0)
                {
                    scheduler.LightChanged(camera, 3f);
                }

                if (frame == 120)
                {
                    scheduler.EverythingChanged();
                }

                var batch = scheduler.Next(ProbeBudget.For(quality).Raster, camera, forward);
                batches.Add(batch.ToArray());
                scheduler.Updated(batch);
            }

            return batches;
        }
    }

    [Fact]
    public void Frame_counts_the_calls_to_Next()
    {
        var cascades = new ProbeCascades(ProbeQuality.Low);
        var scheduler = new ProbeScheduler(cascades, 0);
        scheduler.Scrolled(cascades.Move(Vector3.Zero));

        Assert.Equal(0, scheduler.Frame);
        scheduler.Next(8, Vector3.Zero, Forward);
        scheduler.Next(8, Vector3.Zero, Forward);

        Assert.Equal(2, scheduler.Frame);
    }

    [Fact]
    public void A_scroll_makes_only_the_new_slots_never_captured()
    {
        var cascades = new ProbeCascades(ProbeQuality.Low);
        var scheduler = new ProbeScheduler(cascades, 0);
        scheduler.Scrolled(cascades.Move(Vector3.Zero));
        CaptureEverything(scheduler, Vector3.Zero);

        var scroll = cascades.Move(new Vector3(0.5f, 0f, 0f));
        scheduler.Scrolled(scroll);

        var fresh = scroll.NewSlots(0).ToHashSet();
        Assert.NotEmpty(fresh);
        for (int slot = 0; slot < ProbeCascades.Layouts(ProbeQuality.Low)[0].Total; slot++)
        {
            Assert.Equal(!fresh.Contains(slot), scheduler.Captured(new ProbeId(0, slot)));
        }
    }

    [Fact]
    public void A_light_changing_near_the_camera_goes_before_the_coarse_cascades_fill_in()
    {
        var cascades = new ProbeCascades(ProbeQuality.Low);
        var scheduler = new ProbeScheduler(cascades, 0);
        var camera = new Vector3(0.25f, 1.1f, 0.25f);
        scheduler.Scrolled(cascades.Move(camera));
        var layouts = ProbeCascades.Layouts(ProbeQuality.Low);
        scheduler.Updated(Probes(0, layouts[0].Total));
        scheduler.Updated(Probes(1, layouts[1].Total));
        Assert.False(scheduler.Captured(new ProbeId(2, 0)));
        Assert.False(scheduler.Captured(new ProbeId(3, 0)));

        scheduler.LightChanged(camera, 2f);
        var batch = scheduler.Next(32, camera, Forward);

        // 32 probes, of which a quarter (8) stay kept for the never-captured ones.
        Assert.Equal(32, batch.Count);
        Assert.All(batch.Take(24), id =>
        {
            Assert.InRange(id.Cascade, 0, 1);
            Assert.InRange(Vector3.Distance(cascades.Position(id.Cascade, id.Slot), camera), 0f, 2f);
        });
        Assert.All(batch.Skip(24), id =>
        {
            Assert.InRange(id.Cascade, 2, 3);
            Assert.False(scheduler.Captured(id));
        });
    }

    [Fact]
    public void A_light_changing_every_frame_still_lets_the_coarse_cascades_fill_in()
    {
        var cascades = new ProbeCascades(ProbeQuality.Low);
        var scheduler = new ProbeScheduler(cascades, 0);
        var camera = new Vector3(0.25f, 1.1f, 0.25f);
        scheduler.Scrolled(cascades.Move(camera));
        var layouts = ProbeCascades.Layouts(ProbeQuality.Low);
        scheduler.Updated(Probes(0, layouts[0].Total));
        scheduler.Updated(Probes(1, layouts[1].Total));

        // A flickering lantern 6 m wide keeps far more fine probes dirty than a frame can update. The coarse
        // cascades hold 2 x 2048 probes and get 8 of every 32, so 512 frames fill them.
        for (int frame = 0; frame < 520; frame++)
        {
            scheduler.LightChanged(camera, 6f);
            scheduler.Updated(scheduler.Next(32, camera, Forward));
        }

        for (int c = 2; c <= 3; c++)
        {
            for (int slot = 0; slot < layouts[c].Total; slot++)
            {
                Assert.True(scheduler.Captured(new ProbeId(c, slot)), $"cascade {c} slot {slot} was never captured");
            }
        }
    }

    [Fact]
    public void A_frame_with_a_still_camera_allocates_nothing()
    {
        var cascades = new ProbeCascades(ProbeQuality.High);
        var scheduler = new ProbeScheduler(cascades, 27);
        scheduler.SetFixedGrid(new Vector3(-1f, 0f, -1f), 3, 3, 3, 1f);
        var camera = new Vector3(0.25f, 1.1f, 0.25f);
        int budget = ProbeBudget.For(ProbeQuality.High).Raster;
        bool settled = false;
        int smallest = int.MaxValue;

        // Warm up: the first frames grow the scheduler's buffers and make the cascades' still result.
        for (int frame = 0; frame < 10; frame++)
        {
            RunFrame();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int frame = 0; frame < 200; frame++)
        {
            RunFrame();
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0L, allocated);
        Assert.Equal(210, scheduler.Frame);
        Assert.Equal(budget, smallest);
        Assert.False(settled, "a light changing every frame keeps the probes near it dirty");

        void RunFrame()
        {
            scheduler.Scrolled(cascades.Move(camera));
            scheduler.LightChanged(camera, 2f);
            // No asserts in here: xUnit's own asserts allocate.
            var batch = scheduler.Next(budget, camera, Forward);
            smallest = Math.Min(smallest, batch.Count);
            scheduler.Updated(batch);
            settled |= scheduler.Settled;
        }
    }

    [Fact]
    public void Once_settled_only_the_refresh_budget_of_clean_probes_is_handed_out()
    {
        var cascades = new ProbeCascades(ProbeQuality.Low);
        var scheduler = new ProbeScheduler(cascades, 0);
        var camera = new Vector3(0.25f, 1.1f, 0.25f);
        scheduler.Scrolled(cascades.Move(camera));

        // Before anything is captured the whole budget goes, refresh budget or not.
        var first = scheduler.Next(256, camera, Forward, refreshBudget: 64);
        Assert.Equal(256, first.Count);
        scheduler.Updated(first);
        CaptureEverything(scheduler, camera);
        Assert.True(scheduler.Settled);

        var settled = scheduler.Next(256, camera, Forward, refreshBudget: 64);
        Assert.Equal(64, settled.Count);
        scheduler.Updated(settled);

        // A light near the camera dirties about 130 probes. Until they're done, the whole budget goes again.
        scheduler.LightChanged(camera, 1.5f);
        Assert.False(scheduler.Settled);
        var relit = scheduler.Next(256, camera, Forward, refreshBudget: 64);
        int near = relit.Count(id => Vector3.Distance(cascades.Position(id.Cascade, id.Slot), camera) <= 1.5f);
        Assert.Equal(256, relit.Count);
        Assert.InRange(near, 100, 200);
    }

    [Fact]
    public void A_frame_whose_camera_crosses_cells_allocates_nothing_once_warm()
    {
        var cascades = new ProbeCascades(ProbeQuality.High);
        var scheduler = new ProbeScheduler(cascades, 0);
        int budget = ProbeBudget.For(ProbeQuality.High).Traced;
        var here = new Vector3(0.25f, 1.1f, 0.25f);
        var there = here + new Vector3(4f, 0f, 4f);
        int newSlots = 0;

        // Warm up: the first move makes every probe new, and the next ones grow the lists of new slots to the
        // size of these crossings.
        for (int frame = 0; frame < 10; frame++)
        {
            RunFrame(frame);
        }

        newSlots = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int frame = 0; frame < 40; frame++)
        {
            RunFrame(frame);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0L, allocated);
        // Every frame moved every cascade by a cell or more along x and z, so every frame had new probes.
        Assert.True(newSlots >= 40 * ProbeCascades.Count, $"only {newSlots} new slots in 40 crossings");

        void RunFrame(int frame)
        {
            var camera = frame % 2 == 0 ? there : here;
            var scroll = cascades.Move(camera);
            for (int c = 0; c < ProbeCascades.Count; c++)
            {
                newSlots += scroll.NewSlots(c).Count;
            }

            scheduler.Scrolled(scroll);
            // No asserts in here: xUnit's own asserts allocate.
            var batch = scheduler.Next(budget, camera, Forward);
            scheduler.Updated(batch);
        }
    }

    private static ProbeId[] Probes(int cascade, int count)
    {
        return Enumerable.Range(0, count).Select(slot => new ProbeId(cascade, slot)).ToArray();
    }

    private static IEnumerable<ProbeId> AllProbes(int fixedGrid)
    {
        var layouts = ProbeCascades.Layouts(ProbeQuality.Low);
        for (int c = 0; c < layouts.Length; c++)
        {
            for (int slot = 0; slot < layouts[c].Total; slot++)
            {
                yield return new ProbeId(c, slot);
            }
        }

        for (int slot = 0; slot < fixedGrid; slot++)
        {
            yield return new ProbeId(4, slot);
        }
    }

    // Updates every probe once, by handing out batches until nothing is left uncaptured.
    private static void CaptureEverything(ProbeScheduler scheduler, Vector3 camera)
    {
        for (int guard = 0; guard < 1000 && !AllCaptured(scheduler); guard++)
        {
            scheduler.Updated(scheduler.Next(1024, camera, Forward));
        }

        Assert.True(AllCaptured(scheduler));
    }

    private static bool AllCaptured(ProbeScheduler scheduler)
    {
        return AllProbes(scheduler.FixedGridProbes).All(scheduler.Captured);
    }
}
