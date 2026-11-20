using System.Numerics;
using Super;

namespace SkylarkExample;

/// <summary>The camera, on an arm behind the bird, turned with the mouse or the right stick.</summary>
public sealed class Chase(World world, Actions<Control> actions)
{
    private readonly BoomCamera _boom = new() { Length = 10f, Pitch = 0.45f, Pivot = new Vector3(0f, 1f, 0f) };
    private Entity _camera;

    /// <summary>Spawns the camera into a scene, behind <paramref name="target"/>.</summary>
    public void Build(Scene scene, Vector3 target)
    {
        _boom.Yaw = 0f;
        _boom.Pitch = 0.45f;
        _boom.Jump();
        _boom.Update(target, 0f);
        _camera = scene.Spawn(_boom.Current);
    }

    /// <summary>Turns the move stick into a direction on the ground, so up on the stick is away from the camera.</summary>
    public Vector3 Walk(Vector2 stick) => _boom.Walk(stick);

    /// <summary>Stops the camera sliding after the bird when it's put back home.</summary>
    public void Jump() => _boom.Jump();

    /// <summary>Turns the arm and follows the target. Call it once a tick, after the bird has moved.</summary>
    public void Update(Vector3 target, GameTime time)
    {
        _boom.Turn(actions.Vector(Control.Look));
        _boom.Update(target, time.TickSeconds);
        world.Get<Camera>(_camera) = _boom.Current;
    }
}
