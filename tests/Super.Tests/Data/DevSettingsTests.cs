using System.Text.Json;

namespace Super.Tests;

public sealed class DevSettingsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "super-dev-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, true);
        }
    }

    [Fact]
    public void Write_uses_the_roots_tool_and_target_keys_and_reads_back()
    {
        string path = Path.Combine(_folder, "bin", DevSettings.FileName);
        var settings = new DevSettings([@"C:\game\content", "/art/content"], @"C:\tools\super-cook.dll", "desktop");

        settings.Write(path);

        using (var json = JsonDocument.Parse(File.ReadAllBytes(path)))
        {
            var root = json.RootElement;
            Assert.Equal([@"C:\game\content", "/art/content"], root.GetProperty("roots").EnumerateArray().Select(e => e.GetString()!).ToArray());
            Assert.Equal(@"C:\tools\super-cook.dll", root.GetProperty("tool").GetString());
            Assert.Equal("desktop", root.GetProperty("target").GetString());
        }

        var read = DevSettings.Read(path)!;
        Assert.Equal(settings.Roots, read.Roots);
        Assert.Equal(settings.Tool, read.Tool);
        Assert.Equal(settings.Target, read.Target);
    }

    [Fact]
    public void Only_is_written_by_root_and_reads_back()
    {
        string path = Path.Combine(_folder, DevSettings.FileName);
        var only = new Dictionary<string, string[]> { [@"C:\art\content"] = ["props/**", "fox/**"] };
        new DevSettings([@"C:\game\content", @"C:\art\content"], @"C:\tools\super-cook.dll", "desktop", only).Write(path);

        using (var json = JsonDocument.Parse(File.ReadAllBytes(path)))
        {
            var globs = json.RootElement.GetProperty("only").GetProperty(@"C:\art\content");
            Assert.Equal(["props/**", "fox/**"], globs.EnumerateArray().Select(e => e.GetString()!).ToArray());
        }

        var read = DevSettings.Read(path)!;
        Assert.Equal(["props/**", "fox/**"], read.Only![@"C:\art\content"]);
    }

    [Fact]
    public void Read_returns_null_when_there_is_no_file()
    {
        Assert.Null(DevSettings.Read(Path.Combine(_folder, DevSettings.FileName)));
    }

    [Fact]
    public void Read_names_the_file_when_a_key_is_missing()
    {
        Directory.CreateDirectory(_folder);
        string path = Path.Combine(_folder, DevSettings.FileName);
        File.WriteAllText(path, """{ "roots": [] }""");

        var exception = Assert.Throws<ContentException>(() => DevSettings.Read(path));

        Assert.Equal($"{path}: needs \"roots\", \"tool\" and \"target\". Building the game writes it again.", exception.Message);
    }
}
