using Super.Testing;

namespace Super.Tests;

public class LoadAllTests
{
    public enum Rarity
    {
        Common,
        Rare,
    }

    public record Item(string Id, string Name, int ChopPower, ModelRef Model);

    public record Stats(int Power, float Weight = 1f);

    public record Gear(string Id, Stats Stats, Rarity Rarity, string[] Tags, string? Note = null);

    public record NoId(string Name);

    public record Plain(string Id, string[] Tags);

    private const string Axe =
        "{\n" +
        "  \"id\": \"axe\",\n" +
        "  \"name\": \"Iron Axe\",\n" +
        "  \"chopPower\": 3,\n" +
        "  \"model\": \"models/axe\",\n" +
        "}\n";

    private static (Content Content, MemoryContent Files) Make(params (string Path, string Text)[] files)
    {
        var memory = new MemoryContent();
        foreach (var (path, text) in files)
        {
            memory[path] = text;
        }

        return (new Content(memory), memory);
    }

    private static string[] Problems<T>(string folder, params (string Path, string Text)[] files)
        where T : notnull
    {
        var (content, _) = Make(files);
        return Assert.Throws<ContentException>(() => content.LoadAll<T>(folder)).Problems.ToArray();
    }

    [Fact]
    public void A_file_becomes_a_record_found_by_its_id()
    {
        var (content, _) = Make(("items/axe.json", Axe));
        var items = content.LoadAll<Item>("items");
        Assert.Equal("Iron Axe", items["axe"].Name);
        Assert.Equal(3, items["axe"].ChopPower);
        Assert.Single(items);
    }

    [Fact]
    public void Comments_and_trailing_commas_are_allowed()
    {
        var (content, _) = Make(("items/axe.json", "// A tool.\n" + Axe));
        Assert.True(content.LoadAll<Item>("items").Contains("axe"));
    }

    [Fact]
    public void A_byte_order_mark_is_not_a_mistake()
    {
        var (content, _) = Make(("items/axe.json", "﻿" + Axe));
        Assert.True(content.LoadAll<Item>("items").Contains("axe"));
    }

    [Fact]
    public void Models_are_named_once_however_many_records_share_them()
    {
        var (content, _) = Make(("items/axe.json", Axe), ("items/hatchet.json", Axe.Replace("\"axe\"", "\"hatchet\"")));
        var items = content.LoadAll<Item>("items");
        Assert.Equal(items["axe"].Model, items["hatchet"].Model);
        Assert.Single(content.Items);
    }

    [Fact]
    public void Records_come_in_file_path_order()
    {
        var (content, _) = Make(
            ("items/b.json", Axe.Replace("\"axe\"", "\"second\"")),
            ("items/a.json", Axe.Replace("\"axe\"", "\"first\"")),
            ("items/sub/c.json", Axe.Replace("\"axe\"", "\"third\"")));
        var ids = new List<string>();
        foreach (var item in content.LoadAll<Item>("items"))
        {
            ids.Add(item.Id);
        }

        Assert.Equal(["first", "second", "third"], ids);
    }

    [Fact]
    public void Asking_twice_hands_back_the_same_catalog()
    {
        var (content, _) = Make(("items/axe.json", Axe));
        Assert.Same(content.LoadAll<Item>("items"), content.LoadAll<Item>("items"));
    }

    [Fact]
    public void An_unknown_id_says_which_one()
    {
        var (content, _) = Make(("items/axe.json", Axe));
        var items = content.LoadAll<Item>("items");
        var error = Assert.Throws<KeyNotFoundException>(() => items["sword"]);
        Assert.Contains("sword", error.Message);
        Assert.False(items.TryGet("sword", out _));
    }

    [Fact]
    public void A_wrong_type_names_the_file_line_and_field()
    {
        string[] problems = Problems<Item>("items", ("items/axe.json", Axe.Replace("3,", "\"lots\",")));
        Assert.Equal(["items/axe.json:4: chopPower must be a number"], problems);
    }

    [Fact]
    public void A_fraction_in_a_whole_number_field_is_reported()
    {
        string[] problems = Problems<Item>("items", ("items/axe.json", Axe.Replace("3,", "2.5,")));
        Assert.Equal(["items/axe.json:4: chopPower must be a whole number"], problems);
    }

    [Fact]
    public void An_unknown_field_is_reported()
    {
        string[] problems = Problems<Item>("items", ("items/axe.json", Axe.Replace("\"chopPower\": 3,", "\"chopPower\": 3,\n  \"chopPowr\": 4,")));
        Assert.Equal(["items/axe.json:5: chopPowr is not a field of Item"], problems);
    }

    [Fact]
    public void Field_names_are_exact()
    {
        string[] problems = Problems<Item>("items", ("items/axe.json", Axe.Replace("\"chopPower\"", "\"ChopPower\"")));
        Assert.Contains("items/axe.json:4: ChopPower is not a field of Item", problems);
    }

    [Fact]
    public void A_missing_field_is_reported()
    {
        string[] problems = Problems<Item>("items", ("items/axe.json", Axe.Replace("  \"chopPower\": 3,\n", string.Empty)));
        Assert.Equal(["items/axe.json:1: chopPower is missing"], problems);
    }

    [Fact]
    public void Null_in_a_required_field_is_reported()
    {
        string[] problems = Problems<Item>("items", ("items/axe.json", Axe.Replace("\"Iron Axe\"", "null")));
        Assert.Equal(["items/axe.json:3: name must be text"], problems);
    }

    [Fact]
    public void Every_problem_in_every_file_is_reported_at_once()
    {
        string[] problems = Problems<Item>(
            "items",
            ("items/a.json", Axe.Replace("3,", "\"x\",").Replace("\"Iron Axe\"", "7")),
            ("items/b.json", Axe.Replace("3,", "true,")));
        Assert.Equal(
            ["items/a.json:3: name must be text", "items/a.json:4: chopPower must be a number", "items/b.json:4: chopPower must be a number"],
            problems);
    }

    [Fact]
    public void Broken_json_names_the_place()
    {
        string[] problems = Problems<Item>("items", ("items/axe.json", "{\n  \"id\": \"axe\"\n  \"name\": \"x\"\n}"));
        string problem = Assert.Single(problems);
        Assert.StartsWith("items/axe.json:3: ", problem);
    }

    [Fact]
    public void Nested_fields_name_their_whole_path()
    {
        const string Text =
            "{\n" +
            "  \"id\": \"axe\",\n" +
            "  \"stats\": { \"power\": \"strong\" },\n" +
            "  \"rarity\": \"common\",\n" +
            "  \"tags\": [\"a\", 2]\n" +
            "}\n";
        string[] problems = Problems<Gear>("gear", ("gear/axe.json", Text));
        Assert.Equal(["gear/axe.json:3: stats.power must be a number", "gear/axe.json:5: tags[1] must be text"], problems);
    }

    [Fact]
    public void Optional_fields_can_be_left_out()
    {
        const string Text = "{ \"id\": \"axe\", \"stats\": { \"power\": 1 }, \"rarity\": \"rare\", \"tags\": [] }";
        var (content, _) = Make(("gear/axe.json", Text));
        var axe = content.LoadAll<Gear>("gear")["axe"];
        Assert.Equal(1f, axe.Stats.Weight);
        Assert.Equal(Rarity.Rare, axe.Rarity);
        Assert.Null(axe.Note);
    }

    [Fact]
    public void A_wrong_choice_lists_the_right_ones()
    {
        const string Text = "{ \"id\": \"axe\", \"stats\": { \"power\": 1 }, \"rarity\": \"epic\", \"tags\": [] }";
        string[] problems = Problems<Gear>("gear", ("gear/axe.json", Text));
        Assert.Equal(["gear/axe.json:1: rarity must be one of common, rare"], problems);
    }

    [Fact]
    public void Two_records_with_one_id_are_reported()
    {
        string[] problems = Problems<Item>("items", ("items/a.json", Axe), ("items/b.json", Axe));
        Assert.Equal(["items/b.json:2: id axe is already used by items/a.json"], problems);
    }

    [Fact]
    public void An_empty_id_is_reported()
    {
        string[] problems = Problems<Item>("items", ("items/a.json", Axe.Replace("\"axe\"", "\"\"")));
        Assert.Equal(["items/a.json:2: id must not be empty"], problems);
    }

    [Fact]
    public void A_record_without_an_id_is_a_programming_mistake()
    {
        var (content, _) = Make(("things/a.json", "{ \"name\": \"x\" }"));
        var error = Assert.Throws<InvalidOperationException>(() => content.LoadAll<NoId>("things"));
        Assert.Contains("NoId", error.Message);
    }

    [Fact]
    public void A_missing_folder_is_reported()
    {
        string[] problems = Problems<Item>("nothing");
        Assert.Equal(["content/nothing: there is no such folder"], problems);
    }

    [Fact]
    public void A_model_that_does_not_exist_is_a_broken_reference()
    {
        var (content, files) = Make(("items/axe.json", Axe));
        files.AddModel("models/other");
        var error = Assert.Throws<ContentException>(() => content.LoadAll<Item>("items"));
        Assert.Equal(["items/axe.json:5: model models/axe does not exist"], error.Problems);
    }

    [Fact]
    public void Bad_data_names_nothing_for_the_renderer()
    {
        var (content, _) = Make(("items/axe.json", Axe.Replace("3,", "\"x\",")));
        Assert.Throws<ContentException>(() => content.LoadAll<Item>("items"));
        Assert.Empty(content.Items);
    }

    [Fact]
    public void A_folder_on_disk_loads_the_same_way()
    {
        string root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(root, "items", "tools"));
        try
        {
            File.WriteAllText(Path.Combine(root, "items", "tools", "axe.json"), "{ \"id\": \"axe\", \"tags\": [] }");
            File.WriteAllText(Path.Combine(root, "items", "notes.txt"), "not data");
            var items = new Content(new ContentFolder(root)).LoadAll<Plain>("items");
            Assert.Single(items);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private class Armory(Content content) : IGame
    {
        public Catalog<Item> Items { get; } = content.LoadAll<Item>("items");

        public void Load()
        {
        }

        public void Update(GameTime time)
        {
        }

        public void Draw(View view)
        {
        }
    }

    [Fact]
    public void A_headless_game_gets_its_data_from_the_test()
    {
        var files = new MemoryContent { ["items/axe.json"] = Axe };
        using var run = HeadlessGame.Create<Armory>(1, services => services.AddContentFiles(files));
        Assert.Equal(3, run.Game.Items["axe"].ChopPower);
    }
}
