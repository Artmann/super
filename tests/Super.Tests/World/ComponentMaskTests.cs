namespace Super.Tests;

public class ComponentMaskTests
{
    [Fact]
    public void A_type_past_the_first_word_is_kept_apart_from_its_low_bits()
    {
        var mask = default(ComponentMask).With(70);

        Assert.True(mask.Has(70));
        Assert.False(mask.Has(6));
    }

    [Fact]
    public void Next_walks_the_set_types_in_order_across_words()
    {
        var mask = default(ComponentMask).With(200).With(3);

        Assert.Equal(3, mask.Next(-1));
        Assert.Equal(200, mask.Next(3));
        Assert.Equal(-1, mask.Next(200));
    }

    [Fact]
    public void ContainsAll_needs_every_type_of_the_other()
    {
        var both = default(ComponentMask).With(1).With(130);
        var one = default(ComponentMask).With(130);

        Assert.True(both.ContainsAll(one));
        Assert.False(one.ContainsAll(both));
    }

    [Fact]
    public void Removing_the_only_type_leaves_it_empty()
    {
        var mask = default(ComponentMask).With(5);

        Assert.False(mask.IsEmpty);
        Assert.True(mask.Without(5).IsEmpty);
    }

    [Fact]
    public void And_keeps_only_the_shared_types()
    {
        var left = default(ComponentMask).With(2).With(100).With(255);
        var right = default(ComponentMask).With(100).With(255).With(7);

        var shared = left.And(right);

        Assert.Equal(100, shared.Next(-1));
        Assert.Equal(255, shared.Next(100));
        Assert.Equal(-1, shared.Next(255));
    }

    [Fact]
    public void Masks_built_in_another_order_are_equal_with_the_same_hash()
    {
        var first = default(ComponentMask).With(4).With(190).With(65);
        var second = default(ComponentMask).With(65).With(4).With(190);

        Assert.True(first.Equals(second));
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.False(first.Equals(second.Without(190)));
    }
}
