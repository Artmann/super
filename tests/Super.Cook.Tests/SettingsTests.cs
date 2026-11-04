namespace Super.Cook.Tests;

public sealed class SettingsTests
{
    private static readonly HashSet<string> Cookers = ["texture", "model"];

    private static (bool Cook, bool? Linear, List<CookProblem> Problems) Texture(MemoryRoot root, string name)
    {
        var settings = new CookSettings([root], Cookers);
        var (cook, values) = settings.For(new SourceFile(root, name), "texture");
        return (cook, values["linear"]?.GetValue<bool>(), settings.Problems.ToList());
    }

    [Fact]
    public void Later_rule_beats_earlier()
    {
        var root = new MemoryRoot
        {
            ["cook.json"] = """
                { "rules": [
                    { "match": "**/*.png", "texture": { "linear": true } },
                    { "match": "ui/**", "texture": { "linear": false } },
                ] } // comments and trailing commas are fine
                """,
        };

        Assert.Equal(false, Texture(root, "ui/a.png").Linear);
        Assert.Equal(true, Texture(root, "world/a.png").Linear);
    }

    [Fact]
    public void Deeper_cook_json_beats_shallower()
    {
        var root = new MemoryRoot
        {
            ["cook.json"] = """{ "rules": [ { "match": "**/*.png", "texture": { "linear": true } } ] }""",
            ["ui/cook.json"] = """{ "rules": [ { "match": "*.png", "texture": { "linear": false } } ] }""",
        };

        Assert.Equal(false, Texture(root, "ui/a.png").Linear);
    }

    [Fact]
    public void Settings_merge_per_key_across_rules()
    {
        var root = new MemoryRoot
        {
            ["cook.json"] = """
                { "rules": [
                    { "match": "*.png", "texture": { "linear": true, "mipmaps": true } },
                    { "match": "a.png", "texture": { "mipmaps": false } },
                ] }
                """,
        };
        var cooking = new CookSettings([root], Cookers);

        var (_, settings) = cooking.For(new SourceFile(root, "a.png"), "texture");

        Assert.Empty(cooking.Problems);
        Assert.True(settings["linear"]!.GetValue<bool>());
        Assert.False(settings["mipmaps"]!.GetValue<bool>());
    }

    [Fact]
    public void Sidecar_beats_every_rule()
    {
        var root = new MemoryRoot
        {
            ["cook.json"] = """{ "rules": [ { "match": "**", "texture": { "linear": true } } ] }""",
            ["ui/cook.json"] = """{ "rules": [ { "match": "*", "texture": { "linear": true } } ] }""",
            ["ui/a.png.cook.json"] = """{ "linear": false }""",
        };

        var result = Texture(root, "ui/a.png");

        Assert.Empty(result.Problems);
        Assert.Equal(false, result.Linear);
    }

    [Fact]
    public void Cook_false_turns_a_file_off()
    {
        var root = new MemoryRoot
        {
            ["cook.json"] = """{ "rules": [ { "match": "models/**/textures/**", "cook": false } ] }""",
        };

        Assert.False(Texture(root, "models/fox/textures/a.png").Cook);
        Assert.True(Texture(root, "models/fox/a.png").Cook);
    }

    [Fact]
    public void Rules_match_relative_to_their_own_folder()
    {
        var root = new MemoryRoot
        {
            ["ui/cook.json"] = """{ "rules": [ { "match": "*.png", "texture": { "linear": true } } ] }""",
        };

        Assert.Equal(true, Texture(root, "ui/a.png").Linear);
        Assert.Null(Texture(root, "a.png").Linear);
        Assert.Null(Texture(root, "ui/deeper/a.png").Linear);
    }

    [Fact]
    public void A_cook_json_in_one_root_does_not_touch_another()
    {
        var one = new MemoryRoot { ["cook.json"] = """{ "rules": [ { "match": "**", "texture": { "linear": true } } ] }""" };
        var two = new MemoryRoot();

        Assert.Equal(true, Texture(one, "a.png").Linear);
        Assert.Null(Texture(two, "a.png").Linear);
    }

    [Fact]
    public void Unknown_rule_key_is_an_error()
    {
        var root = new MemoryRoot { ["cook.json"] = """{ "rules": [ { "match": "*", "textur": {} } ] }""" };

        var problem = Assert.Single(Texture(root, "a.png").Problems);

        Assert.True(problem.IsError);
        Assert.Contains("textur", problem.Message);
        Assert.Equal("memory:cook.json", problem.File);
    }

    [Fact]
    public void Broken_json_is_an_error_with_a_line()
    {
        var root = new MemoryRoot { ["cook.json"] = "{\n  \"rules\": [ oops ]\n}" };

        var problem = Assert.Single(Texture(root, "a.png").Problems);

        Assert.Equal(2, problem.Line);
    }

    [Fact]
    public void Other_cookers_settings_are_not_returned()
    {
        var root = new MemoryRoot { ["cook.json"] = """{ "rules": [ { "match": "*", "model": { "scale": 2 } } ] }""" };
        var cooking = new CookSettings([root], Cookers);

        var (_, settings) = cooking.For(new SourceFile(root, "a.png"), "texture");

        Assert.Empty(cooking.Problems);
        Assert.Empty(settings);
    }

    [Fact]
    public void A_typo_in_a_rule_that_matches_no_file_is_still_reported()
    {
        var root = new MemoryRoot { ["cook.json"] = """{ "rules": [ { "match": "nothing/**", "textur": {} } ] }""" };

        var problem = Assert.Single(Texture(root, "a.png").Problems);

        Assert.Contains("textur", problem.Message);
    }

    [Fact]
    public void A_broken_cook_json_above_two_files_gives_one_problem()
    {
        var root = new MemoryRoot { ["cook.json"] = "{ nope" };
        var cooking = new CookSettings([root], Cookers);

        cooking.For(new SourceFile(root, "a.png"), "texture");
        cooking.For(new SourceFile(root, "b.png"), "texture");

        var problem = Assert.Single(cooking.Problems);
        Assert.Equal("memory:cook.json", problem.File);
    }

    [Fact]
    public void A_broken_sidecar_gives_one_problem_naming_it()
    {
        var root = new MemoryRoot { ["a.png"] = "", ["a.png.cook.json"] = "[1]" };
        var cooking = new CookSettings([root], Cookers);

        cooking.For(new SourceFile(root, "a.png"), "texture");
        cooking.For(new SourceFile(root, "a.png"), "texture");

        Assert.Equal("memory:a.png.cook.json", Assert.Single(cooking.Problems).File);
    }

    [Theory]
    [InlineData("*.png", "a.png", true)]
    [InlineData("*.png", "ui/a.png", false)]
    [InlineData("**/*.png", "a.png", true)]
    [InlineData("**/*.png", "ui/x/a.png", true)]
    [InlineData("ui/**", "ui/x/a.png", true)]
    [InlineData("ui/**", "a.png", false)]
    [InlineData("ui/*", "ui/x/a.png", false)]
    [InlineData("*.png", "A.PNG", false)]
    [InlineData("a*b", "axxb", true)]
    public void Glob_matches_ordinally(string pattern, string path, bool expected)
    {
        Assert.Equal(expected, Glob.Matches(pattern, path));
    }
}
