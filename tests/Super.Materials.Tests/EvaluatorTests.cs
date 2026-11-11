namespace Super.Materials.Tests;

public class EvaluatorTests
{
    // A test node that counts how many times it is evaluated.
    sealed class CountingNode(int[] counter) : Node(1)
    {
        public static Gray Make(int[] counter) => new(new CountingNode(counter));

        internal override void Evaluate(EvaluationContext context, float[][] inputs, float[] output)
        {
            counter[0]++;
            Array.Fill(output, 0.5f);
        }
    }

    [Fact]
    public void A_node_used_twice_is_evaluated_once()
    {
        var counter = new int[1];
        GraphRunner.Evaluate(() => { var n = CountingNode.Make(counter); return n.Add(n); }, size: 8);
        Assert.Equal(1, counter[0]);
    }

    [Fact]
    public void The_same_graph_gives_the_same_pixels_on_one_thread_and_many()
    {
        Func<Image> build = () => Gray.Of(0.25f).Add(Gray.Of(0.5f));
        Assert.Equal(GraphRunner.Evaluate(build, 64, maxThreads: 1), GraphRunner.Evaluate(build, 64, maxThreads: 8));
    }

    [Fact]
    public void A_shared_input_is_not_released_early_or_overwritten_in_a_diamond()
    {
        // (a + b) + (a + c), where a is read by two nodes that both finish before the last add.
        var pixels = GraphRunner.Evaluate(() =>
        {
            var a = Gray.FromArray([1f, 2f, 3f, 4f]);
            var b = Gray.FromArray([10f, 20f, 30f, 40f]);
            var c = Gray.Of(100f);
            return a.Add(b).Add(a.Add(c));
        }, size: 2);

        Assert.Equal([112f, 124f, 136f, 148f], pixels);
    }

    [Fact]
    public void An_exception_in_a_row_reaches_the_caller_as_itself()
    {
        var error = Assert.Throws<InvalidOperationException>(() => GraphRunner.Evaluate(() => new ThrowingNode().AsGray(), 16));
        Assert.Equal("row failed", error.Message);
    }

    sealed class ThrowingNode() : Node(1)
    {
        public Gray AsGray() => new(this);

        internal override void Evaluate(EvaluationContext context, float[][] inputs, float[] output)
        {
            context.Rows(y => throw new InvalidOperationException("row failed"));
        }
    }

    [Fact]
    public void Making_a_node_outside_a_graph_explains_where_nodes_are_made()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Gray.Of(1f));
        Assert.Equal("Nodes are made inside MaterialGraph.Build.", error.Message);
    }

    [Fact]
    public void Rgb_of_hex_reads_srgb_values_as_written()
    {
        var pixels = GraphRunner.Evaluate(() => Rgb.Of("#ff8000"), size: 1);
        Assert.Equal([1f, 128f / 255f, 0f], pixels);
    }

    [Fact]
    public void Rgb_of_a_bad_hex_says_how_to_write_one()
    {
        var error = Assert.Throws<ArgumentException>(() => GraphRunner.Evaluate(() => Rgb.Of("orange"), 1));
        Assert.Equal("Colours are written #rrggbb, not orange.", error.Message);
    }

    [Fact]
    public void Add_adds_pixel_by_pixel()
    {
        var pixels = GraphRunner.Evaluate(() => Gray.FromArray([1f, 2f, 3f, 4f]).Add(Gray.Of(0.5f)), size: 2);
        Assert.Equal([1.5f, 2.5f, 3.5f, 4.5f], pixels);
    }

    [Fact]
    public void An_array_of_the_wrong_length_is_refused()
    {
        var error = Assert.Throws<InvalidOperationException>(() => GraphRunner.Evaluate(() => Gray.FromArray([1f, 2f]), size: 2));
        Assert.Equal("Expected 4 values for a 2 x 2 image, got 2.", error.Message);
    }

    [Fact]
    public void Nodes_get_a_seed_from_the_graph_seed_and_their_index()
    {
        var seeds = new List<ulong>();
        GraphRunner.Evaluate(() =>
        {
            var a = new SeedNode();
            var b = new SeedNode();
            seeds.Add(a.Seed);
            seeds.Add(b.Seed);
            return Gray.Of(0f);
        }, 1, seed: 7);

        var again = new List<ulong>();
        GraphRunner.Evaluate(() =>
        {
            again.Add(new SeedNode().Seed);
            again.Add(new SeedNode().Seed);
            return Gray.Of(0f);
        }, 1, seed: 7);

        Assert.Equal(seeds, again);
        Assert.NotEqual(seeds[0], seeds[1]);
    }

    sealed class SeedNode() : Node(1)
    {
        internal override void Evaluate(EvaluationContext context, float[][] inputs, float[] output)
        {
        }
    }
}
