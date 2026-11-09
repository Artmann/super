using System.Globalization;
using System.Text;

namespace Super.Cook.Tests;

public sealed class ReportTests : IDisposable
{
    private readonly List<string> _folders = [];

    public void Dispose()
    {
        foreach (string folder in _folders)
        {
            Directory.Delete(folder, true);
        }
    }

    private string Temp()
    {
        string folder = Path.Combine(Path.GetTempPath(), "super-cook-" + Guid.NewGuid().ToString("N"));
        _folders.Add(folder);
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>Cooks a .probe file into the bytes of extra.txt, so extra.txt is an input besides the source.</summary>
    private sealed class ReadsExtra : ICooker
    {
        public string Id => "probe";

        public int Version => 1;

        public IReadOnlyList<string> Extensions { get; } = [".probe"];

        public void Cook(CookInput input, CookOutput output)
        {
            using (input.Time("probe work"))
            {
                output.Add(ContentKind.Data, input.Read("extra.txt"));
            }
        }
    }

    private static CookResult Cook(string cache, ISourceRoot root)
    {
        var cookers = BuiltInCookers.All.Append(new ReadsExtra()).ToList();
        return new CookPipeline(cookers, new CookOptions(CacheFolder: cache)).Cook([root], new MemoryStore());
    }

    [Fact]
    public void Each_source_says_whether_it_cooked_and_why_the_cache_missed()
    {
        string cache = Temp();
        var root = new MemoryRoot { ["a.probe"] = "x", ["extra.txt"] = "one", ["b.json"] = "{}" };

        var first = Cook(cache, root);
        root["extra.txt"] = "two";
        var second = Cook(cache, root);

        var a = first.Report.Files.Single(file => file.Name == "a.probe");
        Assert.Equal((CookOutcome.Cooked, "probe"), (a.Outcome, a.Cooker));
        Assert.Equal("no record: new source, or its bytes, settings or cooker version changed", a.Miss);
        Assert.Contains(a.Phases, phase => phase.Phase == "probe work");
        Assert.Equal(3, a.OutputBytes);

        var again = second.Report.Files.Single(file => file.Name == "a.probe");
        Assert.Equal((CookOutcome.Cooked, "input changed: extra.txt"), (again.Outcome, again.Miss));

        var json = second.Report.Files.Single(file => file.Name == "b.json");
        Assert.Equal((CookOutcome.FromCache, null), (json.Outcome, json.Miss));
    }

    [Fact]
    public void A_failed_source_is_reported_as_failed()
    {
        var result = new CookPipeline(BuiltInCookers.All, new CookOptions()).Cook([new MemoryRoot { ["a.json"] = "{ nope" }], new MemoryStore());

        var file = Assert.Single(result.Report.Files);
        Assert.Equal((CookOutcome.Failed, null), (file.Outcome, file.Miss));
    }

    [Fact]
    public void The_report_lists_every_step_in_order_and_the_workers_asked_for()
    {
        var result = new CookPipeline(BuiltInCookers.All, new CookOptions(Workers: 3)).Cook([new MemoryRoot { ["a.json"] = "{}", ["b.json"] = "{}", ["c.json"] = "{}", ["d.json"] = "{}" }], new MemoryStore());

        Assert.Equal(["list sources", "read settings", "cook", "save cache index", "write manifest"], result.Report.Steps.Select(step => step.Step));
        Assert.Equal(3, result.Report.Workers);
    }

    [Fact]
    public void Summary_uses_points_and_groups_misses_of_one_kind_whatever_the_culture()
    {
        var phases = new List<(string, TimeSpan)> { ("decode images", TimeSpan.FromMilliseconds(250)) };
        var report = new CookReport(
            TimeSpan.FromSeconds(1.5),
            [("cook", TimeSpan.FromSeconds(1.25))],
            [
                new CookedFile("a.glb", "model", CookOutcome.Cooked, TimeSpan.FromMilliseconds(250), phases, "input changed: a.png", 3 * 512 * 1024),
                new CookedFile("b.glb", "model", CookOutcome.Cooked, TimeSpan.FromMilliseconds(125), phases, "input changed: b.png", 0),
                new CookedFile("c.json", "json", CookOutcome.FromCache, TimeSpan.FromMilliseconds(5), [], null, 2),
            ],
            4,
            false);

        var before = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
        List<string> lines;
        try
        {
            lines = report.Summary().ToList();
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }

        Assert.Equal(
            [
                "Cook took 1.500 s with 4 workers.",
                "  cook                           1.250 s",
                "Time in each phase, added up over every source (more than the wall clock when cooking in parallel):",
                "  decode images                  0.500 s",
                "Slowest 2 cooked:",
                "    0.250 s     1.5 MB  a.glb",
                "    0.125 s     0.0 MB  b.glb",
                "Why sources weren't taken from the cache:",
                "      2  input changed",
            ],
            lines);
    }

    [Fact]
    public void Details_give_one_line_per_source_with_its_phases()
    {
        var report = new CookReport(TimeSpan.Zero, [],
            [new CookedFile("a.json", "json", CookOutcome.Cooked, TimeSpan.FromMilliseconds(2), [("read source", TimeSpan.FromMilliseconds(1))], "no record", 2)],
            1, false);

        Assert.Equal(["  cooked       0.002 s  a.json (no record): read source 0.001 s"], report.Details());
    }

    [Fact]
    public void Cooking_in_parallel_gives_the_manifest_and_problems_of_cooking_one_at_a_time()
    {
        var good = new MemoryRoot();
        var broken = new MemoryRoot();
        for (int i = 0; i < 40; i++)
        {
            good[$"items/{i:00}.json"] = $"{{ \"n\": {i} }}";
            broken[$"items/{i:00}.json"] = i % 7 == 3 ? "{ broken" : $"{{ \"n\": {i} }}";
        }

        CookResult Cook(MemoryRoot root, int workers, MemoryStore store) =>
            new CookPipeline(BuiltInCookers.All, new CookOptions(Workers: workers)).Cook([root], store);

        var oneStore = new MemoryStore();
        var manyStore = new MemoryStore();
        var one = Cook(good, 1, oneStore);
        var many = Cook(good, 8, manyStore);
        Assert.Equal(40, many.Cooked);
        Assert.Equal(one.Report.Files.Select(file => file.Name), many.Report.Files.Select(file => file.Name));
        Assert.Equal(Encoding.UTF8.GetString(oneStore.Manifest.Serialize()), Encoding.UTF8.GetString(manyStore.Manifest.Serialize()));

        // Problems come in the order of the sources, whichever finished first.
        var expected = new[] { "03", "10", "17", "24", "31", "38" }.Select(n => $"memory:items/{n}.json");
        Assert.Equal(expected, Cook(broken, 1, new MemoryStore()).Problems.Select(problem => problem.File));
        Assert.Equal(expected, Cook(broken, 8, new MemoryStore()).Problems.Select(problem => problem.File));
    }
}
