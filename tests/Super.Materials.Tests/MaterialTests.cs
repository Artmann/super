namespace Super.Materials.Tests;

sealed class TestGraph(Func<Material> build) : MaterialGraph
{
    public override Material Build() => build();
}

public class MaterialTests
{
    static Gray PitHeight()
    {
        var size = 16;
        var values = new float[size * size];

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var dx = (x + 0.5f) / size - 0.5f;
                var dy = (y + 0.5f) / size - 0.5f;

                values[y * size + x] = MathF.Min(1f, MathF.Sqrt(dx * dx + dy * dy) / 0.25f);
            }
        }

        return Gray.FromArray(values);
    }

    [Fact]
    public void Running_a_material_fills_in_the_defaults()
    {
        var images = GraphRunner.Run(new TestGraph(() => new Material()), 8);

        Assert.Equal(8, images.Size);
        Assert.All(images.Roughness, v => Assert.Equal(0.6f, v));
        Assert.All(images.Metallic, v => Assert.Equal(0f, v));
        Assert.All(images.Opacity, v => Assert.Equal(1f, v));
        Assert.All(images.Height, v => Assert.Equal(0.5f, v));
        Assert.Equal(0.5f, images.Color[0]);
        Assert.Equal(8 * 8 * 3, images.Color.Length);
        Assert.Equal(8 * 8 * 3, images.Normal.Length);
        Assert.Equal(1f, images.Normal[2]);
    }

    [Fact]
    public void Occlusion_zero_leaves_the_colour_alone()
    {
        var images = GraphRunner.Run(new TestGraph(() => new Material { Height = PitHeight(), Depth = 1f, Occlusion = 0f }), 16);

        Assert.All(images.Color, v => Assert.Equal(0.5f, v));
    }

    [Fact]
    public void Occlusion_one_darkens_the_colour_at_the_bottom_of_a_pit()
    {
        var images = GraphRunner.Run(new TestGraph(() => new Material { Height = PitHeight(), Depth = 1f, Occlusion = 1f }), 16);

        Assert.True(images.Color[(8 * 16 + 8) * 3] < 0.4f);
    }

    [Fact]
    public void The_colour_the_graph_gives_is_used()
    {
        var images = GraphRunner.Run(new TestGraph(() => new Material { Color = Rgb.Of(0.2f, 0.4f, 0.6f) }), 8);

        Assert.Equal(0.2f, images.Color[0]);
        Assert.Equal(0.4f, images.Color[1]);
        Assert.Equal(0.6f, images.Color[2]);
    }

    [Fact]
    public void A_given_normal_is_laid_over_the_one_the_height_makes()
    {
        // A flat height makes a straight normal, so reorienting leaves the given one as it is.
        var images = GraphRunner.Run(new TestGraph(() => new Material { Normal = new Normals(new ConstantNode([0.6f, 0f, 0.8f])) }), 8);

        Assert.Equal(0.6f, images.Normal[0], 4);
        Assert.Equal(0f, images.Normal[1], 4);
        Assert.Equal(0.8f, images.Normal[2], 4);
    }

    [Fact]
    public void A_height_used_twice_is_worked_out_once()
    {
        var calls = 0;
        var counting = new TestGraph(() =>
        {
            var height = new Gray(new CountingNode(() => calls++));

            return new Material { Height = height, Color = Rgb.Of(1f, 1f, 1f), Roughness = height };
        });

        GraphRunner.Run(counting, 8);

        Assert.Equal(1, calls);
    }

    [Fact]
    public void A_negative_width_names_the_property()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => GraphRunner.Run(new TestGraph(() => new Material { Width = -1f }), 8));

        Assert.StartsWith("Width must be more than 0, not -1.", error.Message);
        Assert.Equal("Width", error.ParamName);
    }

    [Fact]
    public void A_negative_depth_names_the_property()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => GraphRunner.Run(new TestGraph(() => new Material { Depth = -0.5f }), 8));

        Assert.StartsWith("Depth must be at least 0, not -0.5.", error.Message);
    }

    [Theory]
    [InlineData(float.PositiveInfinity, "Width must be a finite number, not Infinity. (Parameter 'Width')")]
    [InlineData(float.NaN, "Width must be a finite number, not NaN. (Parameter 'Width')")]
    public void A_width_that_is_not_finite_names_the_property(float width, string message)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => GraphRunner.Run(new TestGraph(() => new Material { Width = width }), 8));

        Assert.Equal(message, error.Message);
    }

    [Fact]
    public void A_depth_that_is_not_finite_names_the_property()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => GraphRunner.Run(new TestGraph(() => new Material { Depth = float.PositiveInfinity }), 8));

        Assert.Equal("Depth must be a finite number, not Infinity. (Parameter 'Depth')", error.Message);
    }

    [Fact]
    public void An_occlusion_above_one_names_the_property()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => GraphRunner.Run(new TestGraph(() => new Material { Occlusion = 2f }), 8));

        Assert.StartsWith("Occlusion must be from 0 to 1, not 2.", error.Message);
    }

    [Fact]
    public void The_graph_seed_reaches_the_nodes()
    {
        Gray Make() => Noise.Perlin(4);

        var one = GraphRunner.Run(new TestGraph(() => new Material { Height = Make() }) { Seed = 1 }, 16);
        var two = GraphRunner.Run(new TestGraph(() => new Material { Height = Make() }) { Seed = 2 }, 16);
        var again = GraphRunner.Run(new TestGraph(() => new Material { Height = Make() }) { Seed = 1 }, 16);

        Assert.NotEqual(one.Height, two.Height);
        Assert.Equal(one.Height, again.Height);
    }

    [Fact]
    public void An_image_without_a_way_to_read_images_is_an_error()
    {
        var graph = new ImageGraph();

        var error = Assert.Throws<InvalidOperationException>(() => GraphRunner.Run(graph, 8));

        Assert.Equal("This graph was run without a way to read images.", error.Message);
    }

    sealed class ImageGraph : MaterialGraph
    {
        public override Material Build() => new() { Color = Image("a.png") };
    }

    sealed class CountingNode(Action onEvaluate) : Node(1)
    {
        internal override void Evaluate(EvaluationContext context, float[][] inputs, float[] output)
        {
            onEvaluate();
            Array.Fill(output, 0.5f);
        }
    }
}
