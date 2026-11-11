using System.Numerics;
using Super.Materials;

namespace Courtyard.Tests;

/// <summary>The ivy leaf card: a five-lobed leaf on a stalk, cut out by its opacity.</summary>
public sealed class IvyLeafTests
{
    private const int Size = 256;

    private static readonly Lazy<MaterialImages> Small = new(() => GraphRunner.Run(new IvyLeaf(), Size));
    private static readonly Lazy<MaterialImages> Large = new(() => GraphRunner.Run(new IvyLeaf { Seed = 1 }, Size));

    [Fact]
    public void The_leaf_is_solid_and_everything_round_it_is_clear()
    {
        var leaf = Small.Value;

        // The middle of the leaf, a little above where the stalk joins it, is solid.
        Assert.True(At(leaf, new Vector2(0.5f, 0.6f)) > 0.99f, "The leaf's middle is not solid.");

        // The image's border, its corners included, is clear all round: the leaf and its stalk stay inside it.
        for (int i = 0; i < Size; i++)
        {
            foreach (var (x, y) in new[] { (i, 0), (i, Size - 1), (0, i), (Size - 1, i) })
            {
                Assert.True(leaf.Opacity[y * Size + x] < 0.01f, $"The border at ({x}, {y}) is not clear.");
            }
        }
    }

    [Fact]
    public void The_leaf_has_five_lobes()
    {
        // A circle round the leaf's palm, a little outside it, crosses each of the five lobes once: the long one at
        // the top, two at the sides and two smaller ones low down. The stalk crosses it too, but it is far narrower
        // than a lobe, so runs under a twentieth of the way round don't count.
        Assert.Equal(5, LobesCrossed(Small.Value));
        Assert.Equal(5, LobesCrossed(Large.Value));
    }

    [Fact]
    public void The_two_seeds_give_two_different_leaves()
    {
        var small = Small.Value.Opacity;
        var large = Large.Value.Opacity;
        int different = Enumerable.Range(0, Size * Size).Count(i => (small[i] > 0.5f) != (large[i] > 0.5f));

        // The lobes turn and stretch by a few percent, which moves the outline by a few pixels all round.
        Assert.True(different > Size * Size / 100, $"Only {different} pixels differ between the two seeds.");
    }

    [Fact]
    public void The_leaf_is_darker_at_its_base_than_at_its_tips()
    {
        var leaf = Small.Value;

        // The colour runs from #2f4a1e at the base, a green of 74 of 255, up to #6f9a3a at the tips, 154: twice as
        // bright. Just above where the stalk joins, the leaf is much darker than out at the lobes' ends. The stalk
        // itself, below the notch, is its own olive and is left out. The ramp alone would give the base, a fifth of the
        // way along, about 0.31 and the tips about 0.54; the yellowing, which on this seed lies just above the notch,
        // and the brightness noise narrow that, but a leaf of one green would give 1.
        float base_ = Green(leaf, offset => offset.Length() < 0.06f && offset.Y < -0.01f);
        float tips = Green(leaf, offset => offset.Length() > 0.34f);

        Assert.True(tips > base_ * 1.25f, $"The tips' green is {tips:0.000}, the base's {base_:0.000}.");
    }

    private static float At(MaterialImages images, Vector2 uv) =>
        images.Opacity[(int)(uv.Y * Size) * Size + (int)(uv.X * Size)];

    private static int LobesCrossed(MaterialImages images)
    {
        // The palm's middle is 0.1 above the notch where the stalk joins.
        var centre = new Vector2(IvyLeaf.Notch.X, IvyLeaf.Notch.Y - 0.1f);
        const int Samples = 720;
        var solid = new bool[Samples];
        for (int i = 0; i < Samples; i++)
        {
            float angle = i * 2f * MathF.PI / Samples;
            solid[i] = At(images, centre + 0.26f * new Vector2(MathF.Cos(angle), MathF.Sin(angle))) > 0.5f;
        }

        // Start counting from a clear sample, so a run isn't split in two where the circle closes.
        int start = Array.IndexOf(solid, false);
        Assert.True(start >= 0, "The circle never leaves the leaf.");
        int lobes = 0;
        int run = 0;
        for (int k = 1; k <= Samples; k++)
        {
            if (solid[(start + k) % Samples])
            {
                run++;
            }
            else
            {
                if (run > Samples / 20)
                {
                    lobes++;
                }

                run = 0;
            }
        }

        return lobes;
    }

    // The median green of the solid pixels whose offset from the notch, in fractions of the image with y down, passes
    // a test. The median, not the mean, so the pale veins, which all meet at the notch, don't stand in for the blade
    // between them.
    private static float Green(MaterialImages images, Func<Vector2, bool> where)
    {
        var greens = new List<float>();
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                int i = y * Size + x;
                var offset = new Vector2((x + 0.5f) / Size, (y + 0.5f) / Size) - IvyLeaf.Notch;
                if (images.Opacity[i] > 0.99f && where(offset))
                {
                    greens.Add(images.Color[i * 3 + 1]);
                }
            }
        }

        Assert.True(greens.Count > 0, "No solid pixels are that far from the notch.");
        greens.Sort();
        return greens[greens.Count / 2];
    }
}
