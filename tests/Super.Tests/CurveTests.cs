using System.Globalization;
using Super.Testing;

namespace Super.Tests;

public class CurveTests
{
    private const float Tolerance = 0.00001f;

    public record Motion(string Id, Curve Rise);

    private static CurveKey Key(float time, float value, CurveTangent tangent = CurveTangent.Auto) => new(time, value, tangent);

    [Theory]
    // Hermite from 0 (leaving at slope 2) to 4 at two seconds (arriving flat), with s the share of the stretch:
    // s = 0.5: 0.125 × 2 s × 2 + 0.5 × 4 = 2.5
    [InlineData(1f, 2.5f)]
    // s = 0.25: h10 = 0.140625, h01 = 0.15625, so 0.140625 × 2 × 2 + 0.15625 × 4 = 1.1875
    [InlineData(0.5f, 1.1875f)]
    public void Custom_slopes_shape_the_curve_between_keys(float time, float expected)
    {
        var curve = new Curve(new CurveKey(0f, 0f, CurveTangent.Custom, Out: 2f), new CurveKey(2f, 4f, CurveTangent.Custom, In: 0f));

        Assert.Equal(expected, curve.Evaluate(time), Tolerance);
    }

    [Fact]
    public void Auto_keys_head_from_the_key_before_to_the_key_after()
    {
        // The middle key's slope is (4 - 0) / (2 - 0) = 2, and the first key is flat.
        // At s = 0.5 of the first second: 0.5 × 1 + (-0.125) × 1 × 2 = 0.25.
        var curve = new Curve(Key(0f, 0f), Key(1f, 1f), Key(2f, 4f));

        Assert.Equal(0.25f, curve.Evaluate(0.5f), Tolerance);
        Assert.Equal(1f, curve.Evaluate(1f), Tolerance);
    }

    [Fact]
    public void Linear_keys_are_joined_by_a_straight_line()
    {
        var curve = new Curve(Key(0f, 0f, CurveTangent.Linear), Key(2f, 4f, CurveTangent.Linear));

        Assert.Equal(1f, curve.Evaluate(0.5f), Tolerance);
        Assert.Equal(3f, curve.Evaluate(1.5f), Tolerance);
    }

    [Fact]
    public void A_constant_key_holds_until_the_next_key()
    {
        var curve = new Curve(Key(0f, 1f, CurveTangent.Constant), Key(1f, 5f));

        Assert.Equal(1f, curve.Evaluate(0.99f), Tolerance);
        Assert.Equal(5f, curve.Evaluate(1f), Tolerance);
    }

    [Fact]
    public void A_curve_holds_its_first_and_last_values_outside_its_keys()
    {
        var curve = new Curve(Key(1f, 3f), Key(2f, 7f));

        Assert.Equal(3f, curve.Evaluate(-5f));
        Assert.Equal(7f, curve.Evaluate(9f));
    }

    [Fact]
    public void Each_stretch_is_found_among_many_keys()
    {
        var curve = new Curve(
            Key(0f, 0f, CurveTangent.Linear),
            Key(1f, 10f, CurveTangent.Linear),
            Key(2f, 20f, CurveTangent.Linear),
            Key(3f, 0f, CurveTangent.Linear),
            Key(4f, 40f, CurveTangent.Linear));

        Assert.Equal(5f, curve.Evaluate(0.5f), Tolerance);
        Assert.Equal(15f, curve.Evaluate(1.5f), Tolerance);
        Assert.Equal(10f, curve.Evaluate(2.5f), Tolerance);
        Assert.Equal(20f, curve.Evaluate(3.5f), Tolerance);
    }

    [Fact]
    public void Sample_reads_from_the_first_key_to_the_last()
    {
        var curve = new Curve(Key(10f, 0f, CurveTangent.Linear), Key(20f, 1f, CurveTangent.Linear));

        Assert.Equal(10f, curve.Duration);
        Assert.Equal(0.25f, curve.Sample(0.25f), Tolerance);
    }

    [Fact]
    public void A_curve_from_an_easing_follows_it_over_a_second()
    {
        var curve = Curve.From(Easing.QuadIn);

        Assert.Equal(0.25f, curve.Evaluate(0.5f), Tolerance);
        Assert.Equal(1f, curve.Duration);
        Assert.Empty(curve.Keys);
        Assert.Equal(Easing.QuadIn, curve.Easing);
    }

    [Fact]
    public void Keys_out_of_order_are_explained_with_their_times()
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
        try
        {
            var error = Assert.Throws<ArgumentException>(() => new Curve(Key(0f, 0f), Key(0.5f, 1f), Key(0.25f, 2f)));

            Assert.Equal("Curve keys must be in order of time, but key 2 at 0.25 comes after 0.5.", error.Message);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void A_curve_with_no_keys_is_refused()
    {
        var error = Assert.Throws<ArgumentException>(() => new Curve(Array.Empty<CurveKey>()));

        Assert.Equal("A curve needs at least one key.", error.Message);
    }

    [Fact]
    public void Slopes_on_a_key_that_isnt_custom_are_refused()
    {
        var error = Assert.Throws<ArgumentException>(() => new Curve(new CurveKey(0f, 0f, CurveTangent.Auto, Out: 1f)));

        Assert.Equal("Curve key 0 sets in or out slopes, which only a custom tangent uses.", error.Message);
    }

    [Fact]
    public void A_key_that_isnt_a_number_is_refused()
    {
        var error = Assert.Throws<ArgumentException>(() => new Curve(Key(0f, float.NaN)));

        Assert.Equal("Curve key 0 must have a finite time, value and slopes.", error.Message);
    }

    [Fact]
    public void Content_loads_a_curve_from_its_keys()
    {
        var content = Content(("motions/rise.json", """
            {
              "id": "rise",
              "rise": { "keys": [ { "time": 0, "value": 0, "tangent": "linear" }, { "time": 2, "value": 4, "tangent": "linear" } ] }
            }
            """));

        var rise = content.LoadAll<Motion>("motions")["rise"].Rise;

        Assert.Equal(1f, rise.Evaluate(0.5f), Tolerance);
    }

    [Fact]
    public void Content_loads_a_curve_from_an_easing()
    {
        var content = Content(("motions/rise.json", """{ "id": "rise", "rise": { "easing": "quadIn" } }"""));

        var rise = content.LoadAll<Motion>("motions")["rise"].Rise;

        Assert.Equal(0.25f, rise.Evaluate(0.5f), Tolerance);
    }

    [Theory]
    [InlineData("""{ "id": "rise", "rise": { } }""", "motions/rise.json: A curve needs keys or an easing.")]
    [InlineData("""{ "id": "rise", "rise": { "easing": "quadIn", "keys": [ { "time": 0, "value": 0 } ] } }""", "motions/rise.json: A curve takes keys or an easing, not both.")]
    [InlineData("""{ "id": "rise", "rise": { "keys": [ { "time": 1, "value": 0 }, { "time": 0, "value": 1 } ] } }""", "motions/rise.json: Curve keys must be in order of time, but key 1 at 0 comes after 1.")]
    public void Content_explains_a_curve_that_cant_be_made(string json, string expected)
    {
        var content = Content(("motions/rise.json", json));

        var problem = Assert.Throws<ContentException>(() => content.LoadAll<Motion>("motions"));

        Assert.Equal([expected], problem.Problems);
    }

    [Fact]
    public void Content_names_a_misspelt_curve_field()
    {
        var content = Content(("motions/rise.json", """{ "id": "rise", "rise": { "kees": [] } }"""));

        var problem = Assert.Throws<ContentException>(() => content.LoadAll<Motion>("motions"));

        Assert.Contains(problem.Problems, line => line.Contains("rise.kees is not a field of Curve", StringComparison.Ordinal));
    }

    [Fact]
    public void A_curve_schema_offers_keys_and_an_easing_but_not_its_working_out()
    {
        var content = Content();

        string schema = content.SchemaText(typeof(Motion));

        Assert.Contains("\"keys\"", schema, StringComparison.Ordinal);
        Assert.Contains("\"easing\"", schema, StringComparison.Ordinal);
        Assert.Contains("\"tangent\"", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("\"duration\"", schema, StringComparison.Ordinal);
    }

    private static Content Content(params (string Path, string Text)[] files)
    {
        var memory = new MemoryContent();
        foreach (var (path, text) in files)
        {
            memory[path] = text;
        }

        return new Content(memory);
    }
}
