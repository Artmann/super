using Super.Materials;

namespace Courtyard.Tests;

/// <summary>The barrel's hoop iron: rough rust over smooth bare metal, and rust running down it in streaks.</summary>
public sealed class HoopIronTests
{
    private const int Size = 512;

    private static readonly Lazy<MaterialImages> Cooked = new(() => GraphRunner.Run(new HoopIron(), Size));

    [Fact]
    public void Rust_is_rough_and_bare_iron_is_smooth()
    {
        var iron = Cooked.Value;
        var bare = Enumerable.Range(0, Size * Size).Where(i => iron.Metallic[i] > 0.99f).ToList();
        var rust = Enumerable.Range(0, Size * Size).Where(i => iron.Metallic[i] < 0.01f).ToList();

        Assert.NotEmpty(bare);
        Assert.NotEmpty(rust);

        var bareRough = bare.Average(i => iron.Roughness[i]);
        var rustRough = rust.Average(i => iron.Roughness[i]);

        // Bare iron about 0.35, rust about 0.9.
        Assert.True(bareRough < 0.45f, $"Bare iron is {bareRough:0.000} rough.");
        Assert.True(rustRough > 0.8f, $"Rust is {rustRough:0.000} rough.");
    }

    [Fact]
    public void Rust_runs_down_the_hoop_in_streaks()
    {
        var red = Enumerable.Range(0, Size * Size).Select(i => Cooked.Value.Color[i * 3]).ToArray();

        // A streak runs down the image, so a pixel is more like the one a centimetre below it than the one a
        // centimetre beside it. The graph is 0.5 m wide, so a centimetre is 10 pixels here.
        var down = Correlation(red, 0, 10);
        var across = Correlation(red, 10, 0);

        Assert.True(down > across + 0.1f, $"The red a centimetre down correlates {down:0.000}, a centimetre across {across:0.000}.");
    }

    private static float Correlation(float[] image, int dx, int dy)
    {
        var mean = image.Average();
        double together = 0;
        double spread = 0;

        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var a = image[y * Size + x] - mean;
                var b = image[(y + dy) % Size * Size + (x + dx) % Size] - mean;

                together += a * b;
                spread += a * a;
            }
        }

        return (float)(together / spread);
    }
}
