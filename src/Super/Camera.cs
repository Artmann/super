using System.Numerics;
using Rendition;

namespace Super;

/// <summary>
/// Where the picture is taken from. Keep one in your game so Update can pick with it and
/// Draw can show it. Field of view is the vertical angle in radians. It's a perspective
/// camera unless <see cref="Size"/> is set, which <see cref="Orthographic"/> does for you.
/// </summary>
public readonly record struct Camera(Vector3 Position, Vector3 Target, float FieldOfView = MathF.PI / 4f)
{
    /// <summary>How far past the camera an orthographic view reaches.</summary>
    public const float OrthographicReach = 10000f;

    private const float NearPlane = 0.1f;
    private const float NearlyVertical = 0.9999f;
    private const float EyeScreensBack = 1000f;

    /// <summary>
    /// How many world units the screen shows from bottom to top. Above zero this camera is
    /// orthographic: things keep their size however far away they are.
    /// </summary>
    public float Size { get; init; }

    /// <summary>
    /// Slides the picture without turning the camera, which makes the view asymmetric. It's
    /// in half screens with y up, so (0.5, 0) puts the target a quarter of the screen right
    /// of the middle. Use it to keep a character clear of a side panel.
    /// </summary>
    public Vector2 Shift { get; init; }

    public bool IsOrthographic => Size > 0f;

    /// <summary>A camera with no perspective, showing this many world units from bottom to top.</summary>
    public static Camera Orthographic(Vector3 position, Vector3 target, float size) =>
        new(position, target) { Size = size };

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

    /// <summary>The line through a pixel. The top left of the screen is zero.</summary>
    public Ray Ray(Vector2 pixel, Vector2 screen)
    {
        var forward = Forward;
        var right = Vector3.Normalize(Vector3.Cross(forward, Up));
        var up = Vector3.Cross(right, forward);

        float height = IsOrthographic ? Size / 2f : MathF.Tan(FieldOfView / 2f);
        float across = (pixel.X / screen.X * 2f - 1f - Shift.X) * height * (screen.X / screen.Y);
        float above = (1f - pixel.Y / screen.Y * 2f - Shift.Y) * height;
        var sideways = right * across + up * above;
        return IsOrthographic
            ? new Ray(Position + sideways, forward)
            : new Ray(Position, Vector3.Normalize(forward + sideways));
    }

    /// <summary>A camera part way between two others. Between kinds, it's whichever is nearer.</summary>
    public static Camera Lerp(in Camera from, in Camera to, float amount)
    {
        var moved = new Camera(
            Vector3.Lerp(from.Position, to.Position, amount),
            Vector3.Lerp(from.Target, to.Target, amount),
            float.Lerp(from.FieldOfView, to.FieldOfView, amount))
        {
            Shift = Vector2.Lerp(from.Shift, to.Shift, amount),
        };

        if (from.IsOrthographic != to.IsOrthographic)
        {
            return moved with { Size = amount < 0.5f ? from.Size : to.Size };
        }

        return moved with { Size = float.Lerp(from.Size, to.Size, amount) };
    }

    /// <summary>The renderer's projection for a screen of this shape.</summary>
    internal Matrix4x4 Matrix(float aspect) => IsOrthographic
        ? Projection.OrthographicReversedZ(Size, aspect, NearPlane, OrthographicReach, Shift)
        : Projection.PerspectiveInfiniteReversedZ(FieldOfView, aspect, NearPlane, Shift);

    /// <summary>
    /// Where shading looks from. An orthographic view has no eye, so this one sits so far
    /// behind that its rays are as good as parallel, and no shader has to know the difference.
    /// </summary>
    internal Vector3 Eye => IsOrthographic
        ? Position - Forward * (MathF.Max(Size, 1f) * EyeScreensBack)
        : Position;

    /// <summary>How far shadows should reach. An orthographic view shows everything at once, so it can't be less than the view.</summary>
    internal float ShadowRadius(float asked, float aspect) => MathF.Max(asked, Size * MathF.Max(aspect, 1f));

    private Vector3 Forward => Vector3.Normalize(Target - Position);
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
        Pitch = Math.Clamp(Pitch + angles.Y, -CameraTurn.PitchLimit, CameraTurn.PitchLimit);
    }

    /// <summary>Moves in by this many wheel notches, as in <c>orbit.Zoom(input.Scroll)</c>.</summary>
    public void Zoom(float notches) => Distance = Math.Clamp(Distance * MathF.Pow(CameraTurn.ZoomStep, notches), MinDistance, MaxDistance);

    /// <summary>Call once a tick, after turning and zooming.</summary>
    public void Update(Vector3 target)
    {
        var next = new Camera(target + CameraTurn.Behind(Yaw, Pitch) * Distance, target, FieldOfView);
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
