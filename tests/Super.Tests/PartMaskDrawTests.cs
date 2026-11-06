using System.Numerics;

namespace Super.Tests;

public class PartMaskDrawTests
{
    private static readonly ModelRef Body = new(3);

    [Fact]
    public void A_mask_made_for_another_model_is_explained()
    {
        var view = new View();
        var mask = new PartMask(5, 4, [0]);
        view.Begin(0f);

        var problem = Assert.Throws<ArgumentException>(() => view.Model(7, Body, Matrix4x4.Identity, mask));

        Assert.Equal("parts", problem.ParamName);
        Assert.StartsWith("The parts mask was made for model 5, not model 3. Make it with Content.Parts for this model.", problem.Message);
        Assert.Empty(view.Drawn.ToArray());
    }

    [Fact]
    public void A_mask_for_a_model_with_another_part_count_is_explained()
    {
        var mask = new PartMask(3, 4, [0]);

        var problem = Assert.Throws<InvalidOperationException>(() => SceneRenderer.KeptCount(5, mask));

        Assert.Equal("The parts mask was made for a model with 4 parts, but the model being drawn has 5. Make it from the same cooked file.", problem.Message);
    }

    [Fact]
    public void Parts_past_the_first_64_are_selected()
    {
        var mask = new PartMask(3, 70, [0, 63, 64, 69]);

        Assert.Equal(4, SceneRenderer.KeptCount(70, mask));
        Assert.True(SceneRenderer.ShowsPart(mask, 64));
        Assert.True(SceneRenderer.ShowsPart(mask, 69));
        Assert.False(SceneRenderer.ShowsPart(mask, 65));
    }
}
