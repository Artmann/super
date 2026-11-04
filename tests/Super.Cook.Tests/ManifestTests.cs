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
    public void ObjectPath_is_under_the_first_two_hash_characters()
    {
        Assert.Equal("objects/ab/abcdef", ContentManifest.ObjectPath("abcdef"));
    }
}
