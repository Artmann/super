using System.Numerics;

namespace Super.Tests;

public class CameraEntityTests
{
    private const float Tolerance = 0.0001f;

    private readonly World _world = new();
    private readonly View _view = new();

    private static Camera At(float x) => new(new Vector3(x, 2f, -5f), new Vector3(x, 0f, 0f));

    private Camera Shot(float blend = 0f)
    {
        _view.Begin(blend);
        WorldFrame.Describe(_view, _world);
        return _view.Shot;
    }

    [Fact]
    public void A_camera_entity_is_the_frame_camera()
    {
        _world.Spawn(new Camera(new Vector3(1f, 2f, 3f), new Vector3(0f, 1f, 0f), 0.5f));

        var shot = Shot();

        Assert.Equal(new Vector3(1f, 2f, 3f), shot.Position);
        Assert.Equal(new Vector3(0f, 1f, 0f), shot.Target);
        Assert.Equal(0.5f, shot.FieldOfView);
    }

    [Fact]
    public void A_camera_written_each_tick_is_blended()
    {
        var camera = _world.Spawn(At(0f));
        LastTicks.Remember(_world);
        _world.Get<Camera>(camera) = At(4f);

        Assert.Equal(1f, Shot(0.25f).Position.X, Tolerance);
    }

    [Fact]
    public void A_new_camera_shows_where_it_is_on_its_first_frame()
    {
        _world.Spawn(At(4f));

        Assert.Equal(4f, Shot(0.25f).Position.X, Tolerance);
    }

    [Fact]
    public void A_teleported_camera_cuts()
    {
        var camera = _world.Spawn(At(0f));
        LastTicks.Remember(_world);
        _world.Teleport(camera, At(4f));

        Assert.Equal(4f, Shot(0.25f).Position.X, Tolerance);
    }

    [Fact]
    public void Teleporting_a_camera_entity_by_its_transform_cuts_its_camera_too()
    {
        var camera = _world.Spawn(At(0f), Transform.Identity);
        LastTicks.Remember(_world);
        _world.Get<Camera>(camera) = At(4f);
        _world.Teleport(camera, Transform.At(new Vector3(10f, 0f, 0f)));

        Assert.Equal(14f, Shot(0.25f).Position.X, Tolerance);
    }

    [Fact]
    public void The_newest_camera_wins()
    {
        _world.Spawn(At(1f));
        _world.Spawn(At(2f));

        Assert.Equal(2f, Shot().Position.X, Tolerance);
    }

    [Fact]
    public void Removing_the_newest_camera_falls_back_to_the_one_before()
    {
        _world.Spawn(At(1f));
        var newest = _world.Spawn(At(2f));
        var other = _world.Spawn(At(3f));
        _world.Destroy(other);
        _world.Remove<Camera>(newest);

        Assert.Equal(1f, Shot().Position.X, Tolerance);
    }

    [Fact]
    public void A_camera_given_back_is_the_newest_again()
    {
        var first = _world.Spawn(At(1f));
        _world.Spawn(At(2f));
        _world.Remove<Camera>(first);
        _world.Add(first, At(1f));

        Assert.Equal(1f, Shot().Position.X, Tolerance);
    }

    [Fact]
    public void A_camera_with_a_parent_follows_it()
    {
        var car = _world.Spawn(Transform.At(new Vector3(10f, 0f, 0f)));
        _world.Spawn(At(0f), new Parent(car));

        var shot = Shot();

        Assert.Equal(new Vector3(10f, 2f, -5f), shot.Position);
        Assert.Equal(new Vector3(10f, 0f, 0f), shot.Target);
    }

    [Fact]
    public void Draw_overrides_the_world_camera()
    {
        _world.Spawn(At(1f));

        _view.Begin(0f);
        WorldFrame.Describe(_view, _world);
        _view.Camera(At(7f));

        Assert.Equal(7f, _view.Shot.Position.X, Tolerance);
    }

    [Fact]
    public void A_world_with_no_camera_leaves_the_default()
    {
        _view.Begin(0f);
        var fallback = _view.Shot;

        Assert.Equal(fallback, Shot());
    }

    [Fact]
    public void ActiveCamera_reads_the_camera_in_Update()
    {
        Assert.Null(_world.ActiveCamera());

        var car = _world.Spawn(Transform.At(new Vector3(10f, 0f, 0f)));
        var camera = _world.Spawn(At(0f), new Parent(car));
        LastTicks.Remember(_world);
        _world.Get<Camera>(camera) = At(4f);

        var active = _world.ActiveCamera();

        Assert.NotNull(active);
        Assert.Equal(new Vector3(14f, 2f, -5f), active.Value.Position);
    }
}
