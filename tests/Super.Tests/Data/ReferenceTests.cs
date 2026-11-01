using Super.Testing;

namespace Super.Tests;

public class ReferenceTests
{
    public record Item(string Id, string Name);

    public record Recipe(string Id, Ref<Item> Makes, Ref<Item>[] Needs, Ref<Item>? Bonus = null);

    private const string Axe = "{ \"id\": \"axe\", \"name\": \"Iron Axe\" }";

    private const string Chop =
        "{\n" +
        "  \"id\": \"chop\",\n" +
        "  \"makes\": \"axe\",\n" +
        "  \"needs\": [\"axe\"],\n" +
        "}\n";

    private class LoadsOne(Content content) : IGame
    {
        public void Load() => content.LoadAll<Recipe>("recipes");

        public void Update(GameTime time)
        {
        }

        public void Draw(View view)
        {
        }
    }

    private class LoadsLate(Content content) : IGame
    {
        public void Load()
        {
        }

        public void Update(GameTime time) => content.LoadAll<Recipe>("recipes");

        public void Draw(View view)
        {
        }
    }

    [Fact]
    public void A_reference_finds_its_record_through_the_catalog()
    {
        var files = new MemoryContent { ["items/axe.json"] = Axe, ["recipes/chop.json"] = Chop };
        var content = new Content(files);
        var items = content.LoadAll<Item>("items");
        var recipes = content.LoadAll<Recipe>("recipes");
        Assert.Equal("Iron Axe", items[recipes["chop"].Makes].Name);
        Assert.Equal("axe", recipes["chop"].Needs[0].Id);
        Assert.Null(recipes["chop"].Bonus);
    }

    [Fact]
    public void A_broken_reference_names_the_place()
    {
        var files = new MemoryContent { ["items/axe.json"] = Axe, ["recipes/chop.json"] = Chop.Replace("\"makes\": \"axe\"", "\"makes\": \"sword\"") };
        var content = new Content(files);
        content.LoadAll<Item>("items");
        var error = Assert.Throws<ContentException>(() => content.LoadAll<Recipe>("recipes"));
        Assert.Equal(["recipes/chop.json:3: makes points at sword, but there is no Item called that"], error.Problems);
    }

    [Fact]
    public void Either_side_can_load_first()
    {
        var files = new MemoryContent { ["items/axe.json"] = Axe.Replace("axe", "hammer"), ["recipes/chop.json"] = Chop };
        var content = new Content(files);
        content.LoadAll<Recipe>("recipes");
        var error = Assert.Throws<ContentException>(() => content.LoadAll<Item>("items"));
        Assert.Equal(
            ["recipes/chop.json:3: makes points at axe, but there is no Item called that", "recipes/chop.json:4: needs[0] points at axe, but there is no Item called that"],
            error.Problems);
    }

    [Fact]
    public void A_reference_into_two_folders_of_one_type_finds_either()
    {
        var files = new MemoryContent { ["items/saw.json"] = Axe.Replace("axe", "saw"), ["tools/axe.json"] = Axe, ["recipes/chop.json"] = Chop };
        var content = new Content(files);
        content.LoadAll<Item>("items");
        content.LoadAll<Item>("tools");
        Assert.Equal("axe", content.LoadAll<Recipe>("recipes")["chop"].Makes.Id);
    }

    [Fact]
    public void Reaching_the_end_of_load_with_nothing_to_point_at_is_reported()
    {
        var files = new MemoryContent { ["recipes/chop.json"] = Chop };
        var error = Assert.Throws<ContentException>(() => HeadlessGame.Create<LoadsOne>(1, services => services.AddContentFiles(files)));
        Assert.Contains("recipes/chop.json:3: makes points at axe, but no Item was loaded", error.Problems);
    }

    [Fact]
    public void A_load_between_levels_is_held_to_the_same_rule()
    {
        var files = new MemoryContent { ["recipes/chop.json"] = Chop };
        using var run = HeadlessGame.Create<LoadsLate>(1, services => services.AddContentFiles(files));
        Assert.Throws<ContentException>(() => run.Ticks(1));
    }
}
