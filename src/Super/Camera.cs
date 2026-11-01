using System.Numerics;

namespace Super;

/// <summary>
/// Where the picture is taken from. Keep one in your game so Update can pick with it and
/// Draw can show it. Field of view is the vertical angle in radians.
/// </summary>
public readonly record struct Camera(Vector3 Position, Vector3 Target, float FieldOfView = MathF.PI / 4f)
{
    private const float NearlyVertical = 0.9999f;

    /// <summary>Which way is up in the picture. Looking straight down, the top of the screen faces -z.</summary>
    public Vector3 Up
    {
        get
        {
            float vertical = Vector3.Normalize(Target - Position).Y;
            if (MathF.Abs(vertical) < NearlyVertical)
            {
                return Vector3.UnitY;
            }

            return vertical < 0f ? -Vector3.UnitZ : Vector3.UnitZ;
        }
    }

    /// <summary>The line through a pixel, starting at the camera. The top left of the screen is zero.</summary>
    public Ray Ray(Vector2 pixel, Vector2 screen)
    {
        var forward = Vector3.Normalize(Target - Position);
        var right = Vector3.Normalize(Vector3.Cross(forward, Up));
        var up = Vector3.Cross(right, forward);

        float height = MathF.Tan(FieldOfView / 2f);
        float across = (pixel.X / screen.X * 2f - 1f) * height * (screen.X / screen.Y);
        float above = (1f - pixel.Y / screen.Y * 2f) * height;
        return new Ray(Position, Vector3.Normalize(forward + right * across + up * above));
    }

    /// <summary>A camera part way between two others.</summary>
    public static Camera Lerp(in Camera from, in Camera to, float amount) => new(
        Vector3.Lerp(from.Position, to.Position, amount),
        Vector3.Lerp(from.Target, to.Target, amount),
        float.Lerp(from.FieldOfView, to.FieldOfView, amount));
}

/// <summary>A line starting at a point. Direction must be one unit long, as a camera's rays are.</summary>
public readonly record struct Ray(Vector3 Origin, Vector3 Direction)
{
    /// <summary>Where the line meets level ground at a height, or nothing if it never reaches it.</summary>
    public Vector3? Ground(float height = 0f)
    {
        float along = (height - Origin.Y) / Direction.Y;
        if (Direction.Y == 0f || along < 0f)
        {
            return null;
        }

        return Origin + Direction * along;
    }

    /// <summary>How far along the line a ball starts, or nothing if the line misses it.</summary>
    public float? HitsBall(Vector3 center, float radius)
    {
        var toCenter = center - Origin;
        float closest = Vector3.Dot(toCenter, Direction);
        float missBy = toCenter.LengthSquared() - closest * closest;
        if (missBy > radius * radius)
        {
            return null;
        }

        float inside = MathF.Sqrt(radius * radius - missBy);
        if (closest + inside < 0f)
        {
            return null;
        }

        return MathF.Max(closest - inside, 0f);
    }

    /// <summary>How far along the line a box lined up with the axes starts, or nothing if the line misses it.</summary>
    public float? HitsBox(Vector3 min, Vector3 max)
    {
        var first = (min - Origin) / Direction;
        var second = (max - Origin) / Direction;
        var near = Vector3.Min(first, second);
        var far = Vector3.Max(first, second);
        float enter = MathF.Max(MathF.Max(near.X, near.Y), near.Z);
        float leave = MathF.Min(MathF.Min(far.X, far.Y), far.Z);

        // Written this way round so a line lying exactly in a face, which divides zero by
        // zero, counts as a miss.
        if (!(enter <= leave) || leave < 0f)
        {
            return null;
        }

        return MathF.Max(enter, 0f);
    }
}

/// <summary>Circles a target at a distance, for looking around something. Keep one in your game.</summary>
public sealed class OrbitCamera
{
    private const float PitchLimit = 1.5f;
    private const float ZoomStep = 0.9f;

    private bool _placed;

    /// <summary>The angle around the target, in radians.</summary>
    public float Yaw { get; set; }

    /// <summary>How high above the target, in radians.</summary>
    public float Pitch { get; set; } = 0.6f;

    public float Distance { get; set; } = 10f;

    public float MinDistance { get; set; } = 1f;

    public float MaxDistance { get; set; } = 1000f;

    public float FieldOfView { get; set; } = MathF.PI / 4f;

    /// <summary>The camera on the tick before this one.</summary>
    public Camera Previous { get; private set; }

    public Camera Current { get; private set; }

    /// <summary>Turns by these angles in radians, as in <c>orbit.Turn(input.MouseDelta * 0.005f)</c>.</summary>
    public void Turn(Vector2 angles)
    {
        Yaw -= angles.X;
        Pitch = Math.Clamp(Pitch + angles.Y, -PitchLimit, PitchLimit);
    }

    /// <summary>Moves in by this many wheel notches, as in <c>orbit.Zoom(input.Scroll)</c>.</summary>
    public void Zoom(float notches) => Distance = Math.Clamp(Distance * MathF.Pow(ZoomStep, notches), MinDistance, MaxDistance);

    /// <summary>Call once a tick, after turning and zooming.</summary>
    public void Update(Vector3 target)
    {
        var position = target + Distance * new Vector3(
            MathF.Sin(Yaw) * MathF.Cos(Pitch),
            MathF.Sin(Pitch),
            MathF.Cos(Yaw) * MathF.Cos(Pitch));
        var next = new Camera(position, target, FieldOfView);
        Previous = _placed ? Current : next;
        Current = next;
        _placed = true;
    }

    /// <summary>The camera between the last two ticks, as in <c>view.Camera(orbit.At(view.Blend))</c>.</summary>
    public Camera At(float blend) => Camera.Lerp(Previous, Current, blend);
}

/// <summary>Trails behind a target at a fixed offset, for following a character. Keep one in your game.</summary>
public sealed class FollowCamera
{
    private Vector3 _target;
    private bool _placed;

    /// <summary>Where the camera sits, from the target.</summary>
    public Vector3 Offset { get; set; } = new(0f, 5f, 8f);

    /// <summary>Roughly how many seconds the camera trails by. Zero means none.</summary>
    public float Lag { get; set; } = 0.15f;

    public float FieldOfView { get; set; } = MathF.PI / 4f;

    /// <summary>The camera on the tick before this one.</summary>
    public Camera Previous { get; private set; }

    public Camera Current { get; private set; }

    /// <summary>Call once a tick with where the target is now.</summary>
    public void Update(Vector3 target, float seconds)
    {
        _target = _placed && Lag > 0f ? Vector3.Lerp(target, _target, MathF.Exp(-seconds / Lag)) : target;
        var next = new Camera(_target + Offset, _target, FieldOfView);
        Previous = _placed ? Current : next;
        Current = next;
        _placed = true;
    }

    /// <summary>Skips the trailing on the next update, for when the target teleports.</summary>
    public void Jump() => _placed = false;

    /// <summary>The camera between the last two ticks, as in <c>view.Camera(follow.At(view.Blend))</c>.</summary>
    public Camera At(float blend) => Camera.Lerp(Previous, Current, blend);
}
