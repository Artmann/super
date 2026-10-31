using System.Numerics;

namespace Super.Tests;

public class ViewTests
{
    [Fact]
    public void Begin_forgets_the_last_frame()
    {
        var view = new View();
        view.Begin(0f);
        view.Model(1, ModelRef.Cube, Matrix4x4.Identity);
        view.Sun(Vector3.UnitY, Vector3.One);

        view.Begin(0f);

        Assert.Equal(0, view.Drawn.Length);
        Assert.Equal(0, view.Suns.Length);
    }

    [Fact]
    public void Model_records_what_it_was_given()
    {
        var view = new View();
        var transform = Matrix4x4.CreateTranslation(1f, 2f, 3f);
        var red = new Vector3(1f, 0f, 0f);
        view.Begin(0f);

        view.Model(7, ModelRef.Cube, transform, red);

        Assert.Equal(new ModelDraw(7, ModelRef.Cube, transform, red), view.Drawn[0]);
    }

    [Fact]
    public void Sun_directions_become_one_unit_long()
    {
        var view = new View();
        view.Begin(0f);

        view.Sun(new Vector3(0f, 5f, 0f), Vector3.One);

        Assert.Equal(Vector3.UnitY, view.Suns[0].Direction);
    }

    [Fact]
    public void Smooth_blends_between_ticks()
    {
        var view = new View();
        view.Begin(0.25f);

        var smooth = view.Smooth(Matrix4x4.CreateTranslation(0f, 0f, 0f), Matrix4x4.CreateTranslation(4f, 0f, 0f));

        Assert.Equal(1f, smooth.Translation.X, 0.0001f);
    }
}
