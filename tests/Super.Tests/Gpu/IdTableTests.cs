using Rendition.Gpu.Vulkan;

namespace Super.Tests.GpuLayer;

public class IdTableTests
{
    [Fact]
    public void Never_hands_out_zero()
    {
        var table = new IdTable<string>();

        int first = table.Add("a");
        int second = table.Add("b");

        Assert.Equal(1, first);
        Assert.Equal(2, second);
        Assert.Equal("a", table[first]);
        Assert.Equal("b", table[second]);
    }

    [Fact]
    public void Reuses_an_id_after_it_is_removed()
    {
        var table = new IdTable<string>();
        table.Add("a");
        int second = table.Add("b");
        table.Add("c");

        table.Remove(second);
        int reused = table.Add("d");

        Assert.Equal(second, reused);
        Assert.Equal("d", table[reused]);
    }

    [Fact]
    public void Reading_a_removed_id_throws()
    {
        var table = new IdTable<string>();
        table.Add("a");
        table.Add("b");
        int third = table.Add("c");
        table.Remove(third);

        var error = Assert.Throws<KeyNotFoundException>(() => table[third]);

        Assert.Equal("Id 3 is not in use.", error.Message);
    }

    [Fact]
    public void Reading_zero_throws()
    {
        var table = new IdTable<string>();
        table.Add("a");

        var error = Assert.Throws<KeyNotFoundException>(() => table[0]);

        Assert.Equal("Id 0 is not in use.", error.Message);
    }
}
