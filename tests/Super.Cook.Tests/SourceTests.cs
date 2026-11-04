namespace Super.Cook.Tests;

public sealed class SourceTests : IDisposable
{
    private readonly List<string> _folders = [];

    public void Dispose()
    {
        foreach (string folder in _folders)
        {
            Directory.Delete(folder, true);
        }
    }

    private string Folder(params string[] files)
    {
        string folder = Path.Combine(Path.GetTempPath(), "super-cook-" + Guid.NewGuid().ToString("N"));
        _folders.Add(folder);
        Directory.CreateDirectory(folder);
        foreach (string file in files)
        {
            string path = Path.Combine(folder, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "x");
        }

        return folder;
    }

    [Fact]
    public void Gather_lists_files_ordinally_with_forward_slashes()
    {
        var (files, problems) = Sources.Gather([new FolderRoot(Folder("b/z.png", "a.json", "b/a.wav"))]);

        Assert.Empty(problems);
        Assert.Equal(["a.json", "b/a.wav", "b/z.png"], files.Select(f => f.Name));
    }

    [Fact]
    public void Gather_skips_settings_buffers_notes_and_dot_folders()
    {
        string folder = Folder("cook.json", "m.glb.cook.json", "m.bin", "notes.md", "a.txt", ".schemas/x.json", ".hidden", "m.glb");

        var (files, problems) = Sources.Gather([new FolderRoot(folder)]);

        Assert.Empty(problems);
        Assert.Equal(["m.glb"], files.Select(f => f.Name));
    }

    [Fact]
    public void Gather_reports_a_name_in_two_roots()
    {
        string one = Folder("icons/axe.png");
        string two = Folder("icons/axe.png");

        var (files, problems) = Sources.Gather([new FolderRoot(one), new FolderRoot(two)]);

        var problem = Assert.Single(problems);
        Assert.True(problem.IsError);
        Assert.Contains(Path.Combine(one, "icons", "axe.png"), problem.Message);
        Assert.Contains(Path.Combine(two, "icons", "axe.png"), problem.Message);
        Assert.Contains("icons/axe.png", problem.Message);
        Assert.Single(files);
    }

    [Fact]
    public void Gather_keeps_the_case_on_disk()
    {
        var (files, _) = Sources.Gather([new FolderRoot(Folder("Fox.glb"))]);

        Assert.Equal("Fox.glb", Assert.Single(files).Name);
    }

    [Fact]
    public void A_sidecar_beside_a_missing_file_is_an_error_naming_the_sidecar()
    {
        var root = new MemoryRoot { ["m.glb.cook.json"] = "{}", ["other.png"] = "" };

        var (_, problems) = Sources.Gather([root]);

        var problem = Assert.Single(problems);
        Assert.True(problem.IsError);
        Assert.Equal("memory:m.glb.cook.json", problem.File);
        Assert.Contains("m.glb.cook.json", problem.Message);
        Assert.Contains("m.glb", problem.Message);
    }

    [Fact]
    public void Problem_prints_as_an_msbuild_line()
    {
        Assert.Equal("C:/g/cook.json(3): error SUPER001: bad", new CookProblem("C:/g/cook.json", 3, "bad", true).ToString());
        Assert.Equal("memory:a.png: error SUPER001: bad", new CookProblem("memory:a.png", null, "bad", true).ToString());
    }
}
