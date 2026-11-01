using System.Text.Json.Nodes;
using Super.Testing;

namespace Super.Tests;

public class SchemaTests
{
    public enum Rarity
    {
        Common,
        Rare,
    }

    public record Stats(int Power, float Weight = 1f);

    public record Item(string Id, string Name, ModelRef Model, Ref<Item> Upgrade, Rarity Rarity, Stats Stats, string? Note = null);

    private static JsonObject Schema()
    {
        var content = new Content(new MemoryContent());
        return JsonNode.Parse(content.SchemaText(typeof(Item)))!.AsObject();
    }

    private static string[] Required(JsonNode schema) =>
        schema["required"]!.AsArray().Select(name => name!.GetValue<string>()).ToArray();

    [Fact]
    public void Fields_are_named_as_they_are_in_the_files()
    {
        Assert.NotNull(Schema()["properties"]!["name"]);
        Assert.Null(Schema()["properties"]!["Name"]);
    }

    [Fact]
    public void Only_fields_with_no_default_are_required()
    {
        string[] required = Required(Schema());
        Assert.Contains("name", required);
        Assert.DoesNotContain("note", required);
        Assert.DoesNotContain("weight", Required(Schema()["properties"]!["stats"]!));
    }

    [Fact]
    public void Models_and_references_are_written_as_text()
    {
        Assert.Equal("string", Schema()["properties"]!["model"]!["type"]!.GetValue<string>());
        Assert.Equal("string", Schema()["properties"]!["upgrade"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void Choices_are_listed_as_the_files_write_them()
    {
        var choices = Schema()["properties"]!["rarity"]!["enum"]!.AsArray().Select(name => name!.GetValue<string>());
        Assert.Equal(["common", "rare"], choices);
    }

    [Fact]
    public void Unknown_fields_are_marked_at_every_level()
    {
        Assert.False(Schema()["additionalProperties"]!.GetValue<bool>());
        Assert.False(Schema()["properties"]!["stats"]!["additionalProperties"]!.GetValue<bool>());
    }

    [Fact]
    public void The_editor_is_told_what_the_loader_accepts()
    {
        Assert.True(Schema()["allowComments"]!.GetValue<bool>());
        Assert.True(Schema()["allowTrailingCommas"]!.GetValue<bool>());
        Assert.NotNull(Schema()["properties"]!["$schema"]);
    }

    [Fact]
    public void A_file_can_name_its_schema()
    {
        const string Text = "{ \"$schema\": \"../.schemas/plain.schema.json\", \"id\": \"a\" }";
        var content = new Content(new MemoryContent { ["plain/a.json"] = Text });
        Assert.True(content.LoadAll<Plain>("plain").Contains("a"));
    }

    public record Plain(string Id);

    [Fact]
    public void A_schema_is_written_for_each_catalog_and_only_when_it_changed()
    {
        string root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(root, "plain"));
        try
        {
            File.WriteAllText(Path.Combine(root, "plain", "a.json"), "{ \"id\": \"a\" }");
            var content = new Content(new ContentFolder(root));
            content.LoadAll<Plain>("plain");

            content.WriteSchemas(root);
            string written = Path.Combine(root, ".schemas", "plain.schema.json");
            Assert.True(File.Exists(written));

            var before = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(written, before);
            content.WriteSchemas(root);
            Assert.Equal(before, File.GetLastWriteTimeUtc(written));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
