using Super.Materials;

namespace Courtyard.Tests;

/// <summary>The dead leaf card: a dry, lobed oak leaf on a stalk, cut out by its opacity.</summary>
public sealed class DeadLeafTests
{
    private const int Size = 256;

    private static readonly Lazy<MaterialImages> Cooked = new(() => GraphRunner.Run(new DeadLeaf(), Size));

    [Fact]
    public void The_leaf_and_its_stalk_are_solid_and_the_border_is_clear()
    {
        var leaf = Cooked.Value;

        // The midrib halfway up, and the stalk below the blade, are solid.
        Assert.True(leaf.Opacity[Size / 2 * Size + Size / 2] > 0.99f, "The leaf's middle is not solid.");
        int stalk = (int)((DeadLeaf.Base + DeadLeaf.StalkFoot) / 2f * Size);
        Assert.True(leaf.Opacity[stalk * Size + Size / 2] > 0.99f, "The stalk is not solid.");

        for (int i = 0; i < Size; i++)
        {
            foreach (var (x, y) in new[] { (i, 0), (i, Size - 1), (0, i), (Size - 1, i) })
            {
                Assert.True(leaf.Opacity[y * Size + x] < 0.01f, $"The border at ({x}, {y}) is not clear.");
            }
        }
    }

    [Fact]
    public void Each_side_has_rounded_lobes()
    {
        var leaf = Cooked.Value;

        // Walking up the left side, the leaf's edge goes out to a lobe and back into a sinus again and again. An
        // oak leaf has four or five lobes a side; the sinuses cut a quarter of the leaf's width or more, far more
        // than the crumbled margin's few pixels, so only swings of 6 pixels count.
        int top = (int)(DeadLeaf.Tip * Size);
        int bottom = (int)(DeadLeaf.Base * Size);
        var edge = new List<int>();
        for (int y = bottom; y > top; y--)
        {
            int x = Enumerable.Range(0, Size).FirstOrDefault(x => leaf.Opacity[y * Size + x] > 0.5f, -1);
            if (x >= 0)
            {
                edge.Add(x);
            }
        }

        Assert.InRange(Lobes(edge, 6), 3, 6);
    }

    [Fact]
    public void The_leaf_is_brown()
    {
        var leaf = Cooked.Value;
        double red = 0;
        double green = 0;
        double blue = 0;
        for (int i = 0; i < Size * Size; i++)
        {
            if (leaf.Opacity[i] > 0.5f)
            {
                red += leaf.Color[i * 3];
                green += leaf.Color[i * 3 + 1];
                blue += leaf.Color[i * 3 + 2];
            }
        }

        // Tan and brown, such as #a06c3a: well more red than green, and more green than blue. A green leaf would have
        // its green on top.
        Assert.True(red > green * 1.2, $"Red {red:0} is not well above green {green:0}.");
        Assert.True(green > blue * 1.2, $"Green {green:0} is not well above blue {blue:0}.");
    }

    // How many times a line goes out to a low point, a lobe's tip on the left side, and back by at least a swing.
    private static int Lobes(List<int> edge, int swing)
    {
        int lobes = 0;
        int low = edge[0];
        int high = edge[0];
        bool goingOut = true;
        foreach (int x in edge)
        {
            if (goingOut)
            {
                low = Math.Min(low, x);
                if (x >= low + swing)
                {
                    lobes++;
                    goingOut = false;
                    high = x;
                }
            }
            else
            {
                high = Math.Max(high, x);
                if (x <= high - swing)
                {
                    goingOut = true;
                    low = x;
                }
            }
        }

        return lobes;
    }
}
