using Rendition;

namespace Super.Tests;

// These run the real glslc on real files in a temp folder, the way a game run from source does.
public sealed class ShaderWatchTests : IDisposable
{
    private const string White = "#version 460\nlayout(location = 0) out vec4 color;\nvoid main() { color = vec4(1.0); }\n";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "super-shader-watch-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = [];

    public ShaderWatchTests()
    {
        Directory.CreateDirectory(Sources);
        Directory.CreateDirectory(Includes);
        Directory.CreateDirectory(Output);

        // The compiler and its flags are the ones this build's own shaders were compiled with.
        string[] real = File.ReadAllLines(Path.Combine(ShaderWatch.DefaultFolder, "Rendition.sources"));
        File.WriteAllLines(Path.Combine(Output, "Test.sources"), [real[0], real[1], Includes, Sources]);
    }

    private string Sources => Path.Combine(_root, "src");

    private string Includes => Path.Combine(_root, "include");

    private string Output => Path.Combine(_root, "out");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void A_saved_shader_is_compiled_over_the_old_one_and_named_once()
    {
        string source = Write(Sources, "plain.frag", White);
        string compiled = Path.Combine(Output, "plain.frag.spv");
        File.WriteAllBytes(compiled, [1, 2, 3]);
        using var watch = ShaderWatch.Open(Output, Log)!;

        watch.Compile([source]);

        // SPIR-V starts with its magic number, 0x07230203, low byte first.
        Assert.Equal([0x03, 0x02, 0x23, 0x07], File.ReadAllBytes(compiled)[..4]);
        Assert.Equal(["plain.frag"], watch.TakeChanged());
        Assert.Empty(watch.TakeChanged());
        string line = Assert.Single(_log);
        Assert.StartsWith("Compiled plain.frag in ", line);
    }

    [Fact]
    public void A_shader_that_does_not_compile_keeps_the_old_one_and_says_what_is_wrong()
    {
        string source = Write(Sources, "broken.frag", White.Replace("vec4(1.0)", "missing"));
        string compiled = Path.Combine(Output, "broken.frag.spv");
        File.WriteAllBytes(compiled, [1, 2, 3]);
        using var watch = ShaderWatch.Open(Output, Log)!;

        watch.Compile([source]);

        Assert.Equal([1, 2, 3], File.ReadAllBytes(compiled));
        Assert.Empty(watch.TakeChanged());
        string line = Assert.Single(_log);
        Assert.StartsWith("broken.frag didn't compile, so the game keeps the old one:", line);
        Assert.Contains("'missing' : undeclared identifier", line);
        Assert.Equal(["broken.frag.spv"], Directory.GetFiles(Output, "broken*").Select(Path.GetFileName));
    }

    [Fact]
    public void A_saved_include_compiles_the_shaders_that_reach_it_and_no_others()
    {
        // through.frag reaches deep.glsl by way of middle.glsl, beside.frag includes another file, plain.frag none.
        string deep = Write(Includes, "deep.glsl", "const float DEEP = 1.0;\n");
        Write(Includes, "middle.glsl", "#include \"deep.glsl\"\n");
        Write(Includes, "other.glsl", "const float OTHER = 1.0;\n");
        string including = "#version 460\n#extension GL_GOOGLE_include_directive : require\n#include \"{0}\"\n"
            + "layout(location = 0) out vec4 color;\nvoid main() {{ color = vec4({1}); }}\n";
        Write(Sources, "through.frag", string.Format(including, "middle.glsl", "DEEP"));
        Write(Sources, "beside.frag", string.Format(including, "other.glsl", "OTHER"));
        Write(Sources, "plain.frag", White);
        using var watch = ShaderWatch.Open(Output, Log)!;

        watch.Compile([deep]);

        Assert.Equal(["through.frag"], watch.TakeChanged());
        Assert.Equal(["through.frag.spv"], Directory.GetFiles(Output, "*.spv").Select(Path.GetFileName));
    }

    [Fact]
    public void A_folder_whose_sources_are_gone_cannot_be_watched()
    {
        Assert.True(ShaderWatch.CanWatch(Output));

        Directory.Delete(Sources, recursive: true);

        Assert.False(ShaderWatch.CanWatch(Output));
        Assert.Null(ShaderWatch.Start(Output, Log));
        Assert.False(ShaderWatch.CanWatch(Path.Combine(_root, "nowhere")));
    }

    private void Log(string line)
    {
        lock (_log)
        {
            _log.Add(line);
        }
    }

    private static string Write(string folder, string name, string text)
    {
        string path = Path.Combine(folder, name);
        File.WriteAllText(path, text);
        return path;
    }
}

/// <summary>Watching waits on the file system and a real timer, so it runs alone.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ShaderWatchCollection
{
    public const string Name = "Shader watch";
}

[Collection(ShaderWatchCollection.Name)]
[Trait("Speed", "Slow")]
public sealed class ShaderWatchLiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "super-shader-watch-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Saving_a_shader_while_watching_compiles_it_without_being_asked()
    {
        string sources = Path.Combine(_root, "src");
        string output = Path.Combine(_root, "out");
        Directory.CreateDirectory(sources);
        Directory.CreateDirectory(output);
        string[] real = File.ReadAllLines(Path.Combine(ShaderWatch.DefaultFolder, "Rendition.sources"));
        File.WriteAllLines(Path.Combine(output, "Test.sources"), [real[0], real[1], sources, sources]);
        using var watch = ShaderWatch.Start(output, _ => { })!;

        File.WriteAllText(
            Path.Combine(sources, "saved.frag"),
            "#version 460\nlayout(location = 0) out vec4 color;\nvoid main() { color = vec4(1.0); }\n");

        IReadOnlySet<string> changed = watch.TakeChanged();
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (changed.Count == 0 && waited.Elapsed < TimeSpan.FromSeconds(30))
        {
            Thread.Sleep(20);
            changed = watch.TakeChanged();
        }

        Assert.Equal(["saved.frag"], changed);
        Assert.True(File.Exists(Path.Combine(output, "saved.frag.spv")));
    }
}
