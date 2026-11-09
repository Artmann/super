using System.Text;

namespace Super.Cook.Tests;

/// <summary>A root's Only picks what's cooked from it, for a folder of sources several games share.</summary>
public sealed class OnlyTests
{
    /// <summary>Cooks a <c>.probe</c> into the bytes of the file it names, read from beside it.</summary>
    private sealed class CopyCooker : ICooker
    {
        public string Id => "copy";

        public int Version => 1;

        public IReadOnlyList<string> Extensions { get; } = [".probe"];

        public void Cook(CookInput input, CookOutput output)
        {
            output.Add(ContentKind.Data, input.Read(Encoding.UTF8.GetString(input.Bytes)));
        }
    }

    private static (CookResult Result, MemoryStore Store) Cook(params ISourceRoot[] roots)
    {
        var store = new MemoryStore();
        var result = new CookPipeline([.. BuiltInCookers.All, new CopyCooker()], new CookOptions()).Cook(roots, store);
        return (result, store);
    }

    [Fact]
    public void Only_cooks_just_the_files_its_globs_match()
    {
        var root = new MemoryRoot { Only = ["props/**", "music/theme.json"] };
        root["props/chest.json"] = "{}";
        root["props/barrel/barrel.json"] = "{}";
        root["music/theme.json"] = "{}";
        root["music/other.json"] = "{}";
        root["fish/tuna.json"] = "{}";

        var (files, problems) = Sources.Gather([root]);

        Assert.Empty(problems);
        Assert.Equal(["music/theme.json", "props/barrel/barrel.json", "props/chest.json"], files.Select(file => file.Name));
    }

    [Fact]
    public void A_glob_that_matches_nothing_is_a_warning_naming_it()
    {
        var root = new MemoryRoot { Only = ["props/**", "prosp/**"] };
        root["props/chest.json"] = "{}";

        var (files, problems) = Sources.Gather([root]);

        var problem = Assert.Single(problems);
        Assert.False(problem.IsError);
        Assert.Equal("Only \"prosp/**\" matches no source in this root.", problem.Message);
        Assert.Equal("props/chest.json", Assert.Single(files).Name);
    }

    [Fact]
    public void A_name_left_out_of_one_root_does_not_clash_with_the_same_name_in_another()
    {
        var shared = new MemoryRoot { Only = ["props/**"] };
        shared["props/chest.json"] = "{}";
        shared["items/axe.json"] = "{ \"shared\": true }";
        var game = new MemoryRoot();
        game["items/axe.json"] = "{ \"game\": true }";

        var (files, problems) = Sources.Gather([shared, game]);

        Assert.Empty(problems);
        Assert.Same(game, files.Single(file => file.Name == "items/axe.json").Root);
    }

    [Fact]
    public void A_cooker_still_reads_a_file_that_Only_leaves_out()
    {
        var root = new MemoryRoot { Only = ["models/**"] };
        root["models/fox.probe"] = "../skins/fox.json";
        root["skins/fox.json"] = "{ \"fur\": \"red\" }";

        var (result, store) = Cook(root);

        Assert.True(result.Succeeded, string.Join("\n", result.Problems));
        Assert.Equal(["models/fox.probe"], store.Manifest.Assets.Keys);
        Assert.Equal("{ \"fur\": \"red\" }", Encoding.UTF8.GetString(store.Get(store.Manifest.Assets["models/fox.probe"].Hash)));
    }
}
