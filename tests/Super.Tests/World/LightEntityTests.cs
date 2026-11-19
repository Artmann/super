using System.Numerics;

namespace Super.Tests;

public class LightEntityTests
{
    private const float Tolerance = 0.0001f;

    private readonly World _world = new();
    private readonly View _view = new();

    private static SpotLight Beam(Vector3 direction) =>
        new(Vector3.Zero, direction, Vector3.One, 10f) { InnerAngle = 0.2f, OuterAngle = 0.4f };

    private void Describe()
    {
        _view.Begin(0f);
        WorldFrame.Describe(_view, _world);
    }

    [Fact]
    public void A_point_light_sits_at_its_entity()
    {
        var torch = _world.Spawn(Transform.At(new Vector3(3f, 1f, 0f)), new PointLight(default, new Vector3(2f), 5f));

        Describe();

        var light = Assert.Single(_view.Lights.DrawnLights.ToArray());
        Assert.Equal(new Vector3(3f, 1f, 0f), light.Position);
        Assert.Equal(torch.Id, light.Key);
        Assert.Equal(5f, light.Range);
    }

    [Fact]
    public void A_point_light_offset_from_its_entity_moves_with_it()
    {
        _world.Spawn(Transform.At(new Vector3(3f, 1f, 0f)), new PointLight(new Vector3(0f, 2f, 0f), Vector3.One, 5f));

        Describe();

        Assert.Equal(new Vector3(3f, 3f, 0f), _view.Lights.DrawnLights[0].Position);
    }

    [Fact]
    public void A_light_without_a_transform_stays_in_the_world()
    {
        _world.Spawn(new PointLight(new Vector3(4f, 0f, 0f), Vector3.One, 5f));

        Describe();

        Assert.Equal(new Vector3(4f, 0f, 0f), _view.Lights.DrawnLights[0].Position);
    }

    [Fact]
    public void A_spot_light_points_along_its_entity()
    {
        var turned = new Transform(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f), Vector3.One);
        _world.Spawn(turned, Beam(Vector3.Zero));

        Describe();

        var direction = _view.Lights.DrawnLights[0].Direction;
        Assert.Equal(1f, direction.X, Tolerance);
        Assert.Equal(0f, direction.Y, Tolerance);
        Assert.Equal(0f, direction.Z, Tolerance);
    }

    [Fact]
    public void A_spot_lights_own_direction_turns_with_its_entity()
    {
        var turned = new Transform(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f), new Vector3(3f));
        _world.Spawn(turned, Beam(new Vector3(-1f, 0f, 0f)));

        Describe();

        var direction = _view.Lights.DrawnLights[0].Direction;
        Assert.Equal(0f, direction.X, Tolerance);
        Assert.Equal(1f, direction.Z, Tolerance);
    }

    [Fact]
    public void A_sun_entity_lights_the_frame()
    {
        _world.Spawn(new Sun(new Vector3(0f, 2f, 0f), new Vector3(3f), CastsShadows: true));

        Describe();

        var sun = Assert.Single(_view.Lights.Suns.ToArray());
        Assert.Equal(Vector3.UnitY, sun.Direction);
        Assert.Equal(new Vector3(3f), sun.Color);
        Assert.True(sun.CastsShadows);
    }

    [Fact]
    public void Two_shadow_suns_throw_with_the_view_message()
    {
        _world.Spawn(new Sun(Vector3.UnitY, Vector3.One, CastsShadows: true));
        _world.Spawn(new Sun(Vector3.UnitX, Vector3.One, CastsShadows: true));

        var error = Assert.Throws<InvalidOperationException>(Describe);

        Assert.StartsWith("Only one sun can cast shadows", error.Message);
    }
}
