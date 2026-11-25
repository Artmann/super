using System.Text;
using Super.Testing;
using Super.Ui;

namespace Super.Cook.Tests;

public sealed class FontCookerTests
{
    private static (CookResult Result, MemoryStore Store) Cook(string name, byte[] bytes)
    {
        var root = new MemoryRoot();
        root.Add(name, bytes);
        var store = new MemoryStore();
        var result = new CookPipeline(BuiltInCookers.All, new CookOptions()).Cook([root], store);
        return (result, store);
    }

    private static byte[] Inter()
    {
        using var stream = typeof(GameFont).Assembly.GetManifestResourceStream("Inter-Regular.ttf")!;
        byte[] bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    [Theory]
    [InlineData("fonts/inter.ttf")]
    [InlineData("fonts/inter.otf")]
    public void A_font_cooks_unchanged_as_data(string name)
    {
        byte[] font = Inter();

        var (result, store) = Cook(name, font);

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        var entry = store.Manifest.Assets[name];
        Assert.Equal(ContentKind.Data, entry.Kind);
        Assert.Equal(font, store.Get(entry.Hash));
    }

    [Fact]
    public void A_file_that_isnt_a_font_is_an_error_naming_it()
    {
        var (result, _) = Cook("fonts/broken.ttf", Encoding.UTF8.GetBytes("not a font at all"));

        Assert.False(result.Succeeded);
        Assert.Contains("fonts/broken.ttf", Assert.Single(result.Problems, p => p.IsError).Message);
    }

    [Fact]
    public void A_cut_off_font_is_an_error_naming_it()
    {
        var (result, _) = Cook("fonts/cut.ttf", Inter()[..400]);

        Assert.False(result.Succeeded);
        Assert.Contains("fonts/cut.ttf", Assert.Single(result.Problems, p => p.IsError).Message);
    }
}
