using System.Text;
using Super.Testing;

namespace Super.Cook.Tests;

public sealed class SoundCookerTests
{
    private static (CookResult Result, MemoryStore Store) Cook(string name, byte[] bytes)
    {
        var root = new MemoryRoot();
        root.Add(name, bytes);
        var store = new MemoryStore();
        var result = new CookPipeline(BuiltInCookers.All, new CookOptions()).Cook([root], store);
        return (result, store);
    }

    [Fact]
    public void Wav_cooks_unchanged()
    {
        byte[] wav = TestWav.Make(8000, 1, [0.1f, 0.2f, 0.3f], asFloat: false);

        var (result, store) = Cook("hit.wav", wav);

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        var entry = store.Manifest.Assets["hit.wav"];
        Assert.Equal(ContentKind.Sound, entry.Kind);
        Assert.Equal(wav, store.Get(entry.Hash));
    }

    [Fact]
    public void Broken_ogg_is_an_error_naming_it()
    {
        var (result, _) = Cook("boom.ogg", Encoding.UTF8.GetBytes("not an ogg file at all"));

        Assert.False(result.Succeeded);
        Assert.Contains("boom.ogg", Assert.Single(result.Problems, p => p.IsError).Message);
    }
}
