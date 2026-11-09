using System.Text;

namespace Super.Cook.Tests;

public sealed class ManifestTests
{
    private static readonly string HashA = new('a', 64);
    private static readonly string HashB = new('b', 64);

    [Fact]
    public void Serialize_then_parse_round_trips()
    {
        var manifest = new ContentManifest("desktop", new Dictionary<string, ManifestEntry>
        {
            ["items/axe.json"] = new(ContentKind.Data, HashA, 12),
            ["tex/fox.png"] = new(ContentKind.Texture, HashB, 3456789012),
        });

        var parsed = ContentManifest.Parse(manifest.Serialize(), "content.manifest");

        Assert.Equal("desktop", parsed.Target);
        Assert.Equal(2, parsed.Assets.Count);
        Assert.Equal(new ManifestEntry(ContentKind.Data, HashA, 12), parsed.Assets["items/axe.json"]);
        Assert.Equal(new ManifestEntry(ContentKind.Texture, HashB, 3456789012), parsed.Assets["tex/fox.png"]);
    }

    [Fact]
    public void Serialize_is_sorted_and_byte_identical_regardless_of_insert_order()
    {
        var first = new Dictionary<string, ManifestEntry>
        {
            ["b.json"] = new(ContentKind.Data, HashB, 2),
            ["a.json"] = new(ContentKind.Data, HashA, 1),
            ["B.json"] = new(ContentKind.Sound, HashA, 3),
        };
        var second = new Dictionary<string, ManifestEntry>
        {
            ["B.json"] = first["B.json"],
            ["a.json"] = first["a.json"],
            ["b.json"] = first["b.json"],
        };

        byte[] one = new ContentManifest("desktop", first).Serialize();
        byte[] two = new ContentManifest("desktop", second).Serialize();

        Assert.Equal(one, two);
        string text = Encoding.UTF8.GetString(one);
        Assert.False(one.Length >= 3 && one[0] == 0xEF);
        Assert.DoesNotContain('\r', text);
        int upper = text.IndexOf("\"B.json\"", StringComparison.Ordinal);
        int a = text.IndexOf("\"a.json\"", StringComparison.Ordinal);
        int b = text.IndexOf("\"b.json\"", StringComparison.Ordinal);
        Assert.True(upper >= 0 && a >= 0 && b >= 0);
        Assert.True(upper < a && a < b);
        Assert.Contains("\"kind\": \"sound\"", text);
    }

    [Fact]
    public void Parse_names_the_file_on_bad_json()
    {
        var exception = Assert.Throws<ContentException>(() =>
            ContentManifest.Parse(Encoding.UTF8.GetBytes("{ nope"), "game/content.manifest"));

        Assert.Contains("game/content.manifest", exception.Message);
    }

    [Fact]
    public void Empty_has_no_assets()
    {
        Assert.Empty(ContentManifest.Empty.Assets);
    }

    [Fact]
    public void Shared_objects_round_trip_sorted_after_the_assets()
    {
        var manifest = new ContentManifest("desktop", new Dictionary<string, ManifestEntry>
        {
            ["a.json"] = new(ContentKind.Data, HashA, 1),
        }, [HashB, HashA]);

        byte[] bytes = manifest.Serialize();
        var parsed = ContentManifest.Parse(bytes, "content.manifest");

        Assert.Equal([HashA, HashB], parsed.Objects);
        string text = Encoding.UTF8.GetString(bytes);
        int assets = text.IndexOf("\"assets\"", StringComparison.Ordinal);
        int objects = text.IndexOf("\"objects\"", StringComparison.Ordinal);
        Assert.True(assets >= 0 && objects >= 0);
        Assert.True(assets < objects);
        Assert.Equal([HashA, HashA, HashB], parsed.AllObjects.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Manifest_without_shared_objects_writes_no_objects_list()
    {
        var manifest = new ContentManifest("desktop", new Dictionary<string, ManifestEntry>
        {
            ["a.json"] = new(ContentKind.Data, HashA, 1),
        });

        Assert.DoesNotContain("objects", Encoding.UTF8.GetString(manifest.Serialize()));
        Assert.Empty(ContentManifest.Parse(manifest.Serialize(), "content.manifest").Objects);
    }

    [Fact]
    public void Objects_that_are_not_a_list_name_the_file_and_the_fix()
    {
        string json = $$"""{ "version": 1, "target": "desktop", "assets": {}, "objects": "{{HashA}}" }""";

        var exception = Assert.Throws<ContentException>(() => ContentManifest.Parse(Encoding.UTF8.GetBytes(json), "game/content.manifest"));

        Assert.Equal("game/content.manifest: \"objects\" must be a list of hashes.", Assert.Single(exception.Problems));
    }

    [Fact]
    public void Object_that_is_not_a_hash_is_refused_so_it_cannot_name_a_path()
    {
        string json = """{ "version": 1, "target": "desktop", "assets": {}, "objects": ["../../secret"] }""";

        var exception = Assert.Throws<ContentException>(() => ContentManifest.Parse(Encoding.UTF8.GetBytes(json), "content.manifest"));

        Assert.Equal("content.manifest: \"objects\" must hold only hashes, and \"../../secret\" isn't one.", Assert.Single(exception.Problems));
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", true)]
    [InlineData("0123456789ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef", false)]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde", false)]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeg", false)]
    [InlineData("", false)]
    public void IsHash_takes_only_64_lowercase_hex_digits(string text, bool expected)
    {
        Assert.Equal(expected, ContentManifest.IsHash(text));
    }

    [Fact]
    public void ObjectPath_is_under_the_first_two_hash_characters()
    {
        Assert.Equal("objects/ab/abcdef", ContentManifest.ObjectPath("abcdef"));
    }
}
