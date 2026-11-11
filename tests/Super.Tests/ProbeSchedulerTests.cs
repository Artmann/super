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
