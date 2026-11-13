using System.Numerics;
using Super;

namespace Archer;

/// <summary>Erika's base states. Each loops until the film picks another.</summary>
public enum Pose
{
    Idle,
    Aim,
    Run,
    Fall,
}

/// <summary>What Erika does over her state. Later ones go on top.</summary>
public enum Move
{
    Look,
    Draw,
    Recoil,
    Dodge,
    Jump,
    Land,
    Dive,
    Cocky,
}

/// <summary>
/// The archer. Her clips carry her across the ground through root motion; the film only
/// says which way she faces. Jumps are in place in Mixamo's clips, so a leap carries her
/// through the air itself.
/// </summary>
public sealed class Erika
{
    private const float TurnRate = 9f;

    private Leap? _leap;

    public Erika(Animator animator, Vector3 position, float yaw)
    {
        Animator = animator;
        Position = position;
        Yaw = yaw;
        TargetYaw = yaw;
        Transform = Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateTranslation(position);
        Previous = Transform;
    }

    public Animator Animator { get; }

    public Vector3 Position { get; private set; }

    public float Yaw { get; private set; }

    /// <summary>The way she turns to face, over a few ticks.</summary>
    public float TargetYaw { get; set; }

    public Matrix4x4 Transform { get; private set; }

    public Matrix4x4 Previous { get; private set; }

    /// <summary>Turns her to face a point on the ground.</summary>
    public void Face(Vector3 point)
    {
        var away = point - Position;
        TargetYaw = MathF.Atan2(away.X, away.Z);
    }

    /// <summary>Flies her in an arc to a point, which takes the given seconds of game time.</summary>
    public void LeapTo(Vector3 to, float apex, float seconds, Action? landed = null) =>
        _leap = new Leap(Position, to, apex, seconds, FollowClip: false, landed);

    /// <summary>Drops her to a height while her clip still carries her along the ground.</summary>
    public void DropTo(float height, float seconds) =>
        _leap = new Leap(Position, Position with { Y = height }, 0f, seconds, FollowClip: true, null);

    public void Update(float seconds)
    {
        Previous = Transform;
        float turn = Angle(TargetYaw - Yaw);
        Yaw += turn * (1f - MathF.Exp(-TurnRate * seconds));

        var travel = Vector3.Transform(Animator.RootMotion, Matrix4x4.CreateRotationY(Yaw));
        if (_leap is { } leap)
        {
            leap.Seconds += seconds;
            float amount = MathF.Min(leap.Seconds / leap.Length, 1f);
            if (leap.FollowClip)
            {
                // Falling: slow at first, then faster, as things do.
                float height = float.Lerp(leap.From.Y, leap.To.Y, amount * amount);
                Position = (Position + travel) with { Y = height };
            }
            else
            {
                var along = Vector3.Lerp(leap.From, leap.To, amount);
                Position = along + new Vector3(0f, 4f * leap.Apex * amount * (1f - amount), 0f);
            }

            if (amount >= 1f)
            {
                _leap = null;
                leap.Landed?.Invoke();
            }
        }
        else
        {
            Position += travel;
        }

        Transform = Matrix4x4.CreateRotationY(Yaw) * Matrix4x4.CreateTranslation(Position);
    }

    /// <summary>Where a joint is in the world now, such as the arrow on her bow.</summary>
    public Vector3 JointAt(string joint) => Vector3.Transform(Animator.Joint(joint).Translation, Transform);

    // The shortest way round, between -pi and pi.
    private static float Angle(float radians) => MathF.IEEERemainder(radians, MathF.Tau);

    private sealed record Leap(Vector3 From, Vector3 To, float Apex, float Length, bool FollowClip, Action? Landed)
    {
        public float Seconds { get; set; }
    }
}
