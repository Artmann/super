using System.Numerics;

namespace Super.Tests;

public class RandomStreamTests
{
    private const int Samples = 100_000;

    private readonly RandomStream _rolls = new GameRandom(7).Stream("test");

    [Fact]
    public void Float_stays_between_zero_and_one_and_averages_a_half()
    {
        double total = 0;
        for (int i = 0; i < Samples; i++)
        {
            float value = _rolls.Float();
            Assert.InRange(value, 0f, 0.99999994f);
            total += value;
        }

        Assert.Equal(0.5, total / Samples, 0.01);
    }

    [Fact]
    public void Whole_number_Between_reaches_both_ends_evenly()
    {
        var counts = new int[6];
        for (int i = 0; i < Samples; i++)
        {
            counts[_rolls.Between(1, 6) - 1]++;
        }

        foreach (int count in counts)
        {
            Assert.InRange(count, Samples / 6 - 800, Samples / 6 + 800);
        }
    }

    [Fact]
    public void Whole_number_Between_handles_the_extremes()
    {
        Assert.Equal(5, _rolls.Between(5, 5));
        Assert.Equal(-3, _rolls.Between(-3, -3));

        for (int i = 0; i < 1000; i++)
        {
            _rolls.Between(int.MinValue, int.MaxValue);
            Assert.InRange(_rolls.Between(-2, 2), -2, 2);
        }
    }

    [Fact]
    public void Decimal_Between_stays_in_range()
    {
        for (int i = 0; i < Samples; i++)
        {
            Assert.InRange(_rolls.Between(-2.5f, 4f), -2.5f, 4f);
        }

        Assert.Equal(3f, _rolls.Between(3f, 3f));
    }

    [Fact]
    public void Between_rejects_a_backwards_range()
    {
        Assert.Throws<ArgumentException>(() => _rolls.Between(2, 1));
        Assert.Throws<ArgumentException>(() => _rolls.Between(2f, 1f));
    }

    [Fact]
    public void Chance_matches_the_probability()
    {
        int hits = 0;
        for (int i = 0; i < Samples; i++)
        {
            if (_rolls.Chance(0.25f))
            {
                hits++;
            }

            Assert.False(_rolls.Chance(0f));
            Assert.True(_rolls.Chance(1f));
        }

        Assert.InRange(hits, Samples * 0.24, Samples * 0.26);
    }

    [Fact]
    public void Bool_and_Sign_are_even()
    {
        int trues = 0;
        int sum = 0;
        for (int i = 0; i < Samples; i++)
        {
            trues += _rolls.Bool() ? 1 : 0;

            int sign = _rolls.Sign();
            Assert.True(sign is -1 or 1);
            sum += sign;
        }

        Assert.InRange(trues, Samples * 0.48, Samples * 0.52);
        Assert.InRange(sum, -2000, 2000);
    }

    [Fact]
    public void Angle_stays_within_one_turn()
    {
        for (int i = 0; i < Samples; i++)
        {
            Assert.InRange(_rolls.Angle(), 0f, MathF.Tau);
        }
    }

    [Fact]
    public void Normal_has_the_requested_mean_and_deviation()
    {
        double total = 0;
        double squares = 0;
        for (int i = 0; i < Samples; i++)
        {
            float value = _rolls.Normal(10f, 2f);
            Assert.True(float.IsFinite(value));
            total += value;
            squares += (value - 10f) * (value - 10f);
        }

        Assert.Equal(10, total / Samples, 0.05);
        Assert.Equal(2, Math.Sqrt(squares / Samples), 0.05);
    }

    [Fact]
    public void Pick_reaches_every_item_and_rejects_an_empty_list()
    {
        string[] items = ["axe", "bow", "sword"];
        var seen = new HashSet<string>();
        for (int i = 0; i < 1000; i++)
        {
            seen.Add(_rolls.Pick(items));
        }

        Assert.Equal(items.Length, seen.Count);
        Assert.Throws<ArgumentException>(() => _rolls.Pick(Array.Empty<string>()));
    }

    [Fact]
    public void PickWeighted_follows_the_weights()
    {
        (string Name, float Weight)[] items = [("common", 6f), ("never", 0f), ("rare", 2f)];
        var counts = new Dictionary<string, int> { ["common"] = 0, ["never"] = 0, ["rare"] = 0 };
        for (int i = 0; i < Samples; i++)
        {
            counts[_rolls.PickWeighted(items, static item => item.Weight).Name]++;
        }

        Assert.Equal(0, counts["never"]);
        Assert.InRange(counts["common"] / (double)counts["rare"], 2.9, 3.1);
    }

    [Fact]
    public void PickWeighted_rejects_bad_weights()
    {
        float[] none = [0f, 0f];
        float[] negative = [1f, -1f];
        float[] broken = [1f, float.NaN];

        Assert.Throws<ArgumentException>(() => _rolls.PickWeighted(none, static weight => weight));
        Assert.Throws<ArgumentException>(() => _rolls.PickWeighted(negative, static weight => weight));
        Assert.Throws<ArgumentException>(() => _rolls.PickWeighted(broken, static weight => weight));
        Assert.Throws<ArgumentException>(() => _rolls.PickWeighted(Array.Empty<float>(), static weight => weight));
    }

    [Fact]
    public void Shuffle_keeps_every_item_and_treats_positions_evenly()
    {
        var firstPlace = new int[4];
        for (int i = 0; i < Samples; i++)
        {
            int[] items = [0, 1, 2, 3];
            _rolls.Shuffle(items);
            firstPlace[items[0]]++;

            if (i == 0)
            {
                Assert.Equal([0, 1, 2, 3], items.Order());
            }
        }

        foreach (int count in firstPlace)
        {
            Assert.InRange(count, Samples / 4 - 1000, Samples / 4 + 1000);
        }
    }

    [Fact]
    public void Shuffle_works_on_a_list_and_on_nothing()
    {
        var items = new List<int> { 1, 2, 3 };
        _rolls.Shuffle(items);
        Assert.Equal([1, 2, 3], items.Order());

        _rolls.Shuffle(new List<int>());
    }

    [Fact]
    public void Directions_are_one_unit_long()
    {
        for (int i = 0; i < 1000; i++)
        {
            Assert.Equal(1f, _rolls.Direction2D().Length(), 0.001f);
            Assert.Equal(1f, _rolls.Direction3D().Length(), 0.001f);
        }
    }

    [Fact]
    public void Direction3D_points_every_way_equally()
    {
        var total = Vector3.Zero;
        for (int i = 0; i < Samples; i++)
        {
            total += _rolls.Direction3D();
        }

        Assert.InRange((total / Samples).Length(), 0f, 0.02f);
    }

    [Fact]
    public void Points_stay_inside_their_shape()
    {
        for (int i = 0; i < 1000; i++)
        {
            Assert.InRange(_rolls.InsideCircle(3f).Length(), 0f, 3.001f);
            Assert.InRange(_rolls.InsideSphere(3f).Length(), 0f, 3.001f);
        }
    }

    [Fact]
    public void InsideCircle_spreads_over_the_area()
    {
        // The inner half of the radius covers a quarter of the area.
        int inner = 0;
        for (int i = 0; i < Samples; i++)
        {
            inner += _rolls.InsideCircle().Length() < 0.5f ? 1 : 0;
        }

        Assert.InRange(inner, Samples * 0.24, Samples * 0.26);
    }
}
