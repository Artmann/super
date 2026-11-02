using System.Numerics;

namespace Archer;

/// <summary>
/// A straw dummy on a post. A hit pushes it over and a spring stands it back up, so it
/// rocks a few times and settles.
/// </summary>
public sealed class Dummy
{
    private const float Stiffness = 70f;
    private const float Damping = 3.2f;
    private const float Push = 2.4f; // Radians a second of lean from one arrow.

    // The middle of the target painted on its chest, in the model's own space.
    private static readonly Vector3 Bullseye = new(0f, 1.12f, 0.2f);

    private Vector3 _lean;
    private Vector3 _leanSpeed;

    public Dummy(Vector3 position, Vector3 facing)
    {
        Position = position;
        var toward = facing - position;
        Yaw = MathF.Atan2(toward.X, toward.Z);
        Transform = Place();
        Previous = Transform;
    }

    public Vector3 Position { get; }

    public float Yaw { get; }

    public Matrix4x4 Transform { get; private set; }

    public Matrix4x4 Previous { get; private set; }

    /// <summary>The bullseye in the world, while the dummy stands upright.</summary>
    public Vector3 Target => Vector3.Transform(Bullseye, Matrix4x4.CreateRotationY(Yaw) * Matrix4x4.CreateTranslation(Position));

    public void Hit(Vector3 direction)
    {
        var flat = new Vector3(direction.X, 0f, direction.Z);
        _leanSpeed += Vector3.Normalize(flat) * Push;
    }

    public void Update(float seconds)
    {
        Previous = Transform;
        var pull = -Stiffness * _lean - Damping * _leanSpeed;
        _leanSpeed += pull * seconds;
        _lean += _leanSpeed * seconds;
        Transform = Place();
    }

    // Leans the post over at its foot, towards where the lean points.
    private Matrix4x4 Place()
    {
        var turn = Matrix4x4.CreateRotationY(Yaw);
        float angle = _lean.Length();
        if (angle > 1e-5f)
        {
            var axis = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, _lean));
            turn *= Matrix4x4.CreateFromAxisAngle(axis, angle);
        }

        return turn * Matrix4x4.CreateTranslation(Position);
    }
}

/// <summary>An arrow in flight, or stuck in a dummy and rocking with it.</summary>
public sealed class Arrow
{
    public const float Speed = 34f;
    private const float HalfLength = 0.4f;
    private const float Sink = 0.07f;

    private Matrix4x4 _onDummy;
    private float _left;

    public Arrow(Vector3 nock, Dummy target)
    {
        Target = target;
        var aim = target.Target - nock;
        Direction = Vector3.Normalize(aim);
        Center = nock + Direction * HalfLength;
        _left = aim.Length() - 2f * HalfLength + Sink;
        Transform = Place();
        Previous = Transform;
    }

    public Dummy Target { get; }

    public Vector3 Direction { get; }

    /// <summary>The middle of the shaft. It stays where the arrow hit once it's stuck.</summary>
    public Vector3 Center { get; private set; }

    public bool Stuck { get; private set; }

    public Matrix4x4 Transform { get; private set; }

    public Matrix4x4 Previous { get; private set; }

    public void Update(float seconds)
    {
        Previous = Transform;
        if (Stuck)
        {
            Transform = _onDummy * Target.Transform;
            return;
        }

        float step = MathF.Min(Speed * seconds, _left);
        Center += Direction * step;
        _left -= step;
        Transform = Place();
        if (_left <= 0f)
        {
            Stuck = true;
            Matrix4x4.Invert(Target.Transform, out var fromDummy);
            _onDummy = Transform * fromDummy;
            Target.Hit(Direction);
        }
    }

    // The model points down +z, so it's turned to fly along its direction.
    private Matrix4x4 Place() => Matrix4x4.CreateWorld(Center, -Direction, Vector3.UnitY);
}
