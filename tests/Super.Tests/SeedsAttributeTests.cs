using Super.Testing;

namespace Super.Tests;

public class SeedsAttributeTests
{
    [Fact]
    public void Gives_each_seed_from_one_up_to_the_count()
    {
        var method = typeof(object).GetMethod(nameof(ToString))!;

        var data = new SeedsAttribute(10).GetData(method).ToList();

        Assert.Equal(Enumerable.Range(1, 10), data.Select(row => Assert.IsType<int>(Assert.Single(row))));
    }

    [Fact]
    public void Gives_nothing_for_a_count_of_zero()
    {
        var method = typeof(object).GetMethod(nameof(ToString))!;

        Assert.Empty(new SeedsAttribute(0).GetData(method));
    }
}
