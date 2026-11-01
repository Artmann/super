using System.Numerics;

namespace Super;

/// <summary>The sums every camera that turns around a point shares.</summary>
internal static class CameraTurn
{
    public const float PitchLimit = 1.5f;
    public const float ZoomStep = 0.9f;

    /// <summary>The way from a target out to a camera turned by these angles.</summary>
    public static Vector3 Behind(float yaw, float pitch) => new(
        MathF.Sin(yaw) * MathF.Cos(pitch),
        MathF.Sin(pitch),
        MathF.Cos(yaw) * MathF.Cos(pitch));

    /// <summary>Level ground to the right of a camera turned this way.</summary>
    public static Vector3 Right(float yaw) => new(MathF.Cos(yaw), 0f, -MathF.Sin(yaw));

    /// <summary>Level ground ahead of a camera turned this way.</summary>
    public static Vector3 Ahead(float yaw) => new(-MathF.Sin(yaw), 0f, -MathF.Cos(yaw));

    /// <summary>Turns a stick into a direction on the ground, so forward goes where the camera faces.</summary>
    public static Vector3 Walk(float yaw, Vector2 stick) => Right(yaw) * stick.X + Ahead(yaw) * stick.Y;

    /// <summary>What's left of a gap after this many seconds, when lag is about how long it takes.</summary>
    public static float Remaining(float seconds, float lag) => lag > 0f ? MathF.Exp(-seconds / lag) : 0f;
}

/// <summary>
/// A third-person camera on an arm behind its target, which shortens when something is in
/// the way. Super doesn't know your walls, so test <see cref="Arm"/> against them and pass
/// how far along it you hit something to <see cref="Update"/>. Keep one in your game.
/// </summary>
public sealed class BoomCamera
{
    private float _shown;
    private bool _placed;

    /// <summary>The angle around the target, in radians.</summary>
    public float Yaw { get; set; }

    /// <summary>How high above the target, in radians.</summary>
    public float Pitch { get; set; } = 0.35f;

    /// <summary>How long the arm is with nothing in the way.</summary>
    public float Length { get; set; } = 6f;

    public float MinLength { get; set; } = 0.5f;

    public float MaxLength { get; set; } = 20f;

    /// <summary>Where the arm starts, from the target. Around head height keeps a character in view.</summary>
    public Vector3 Pivot { get; set; } = new(0f, 1.5f, 0f);

    /// <summary>How far right of the target the view sits, for an over-the-shoulder look. Negative goes left.</summary>
    public float Shoulder { get; set; }

    /// <summary>How far the camera stays from a wall, so it doesn't see through it.</summary>
    public float Margin { get; set; } = 0.2f;

    /// <summary>Roughly how many seconds the arm takes to grow back.</summary>
    public float ReturnLag { get; set; } = 0.3f;

    public float FieldOfView { get; set; } = MathF.PI / 4f;

    /// <summary>The camera on the tick before this one.</summary>
    public Camera Previous { get; private set; }

    public Camera Current { get; private set; }

    /// <summary>Turns by these angles in radians, as in <c>boom.Turn(input.MouseDelta * 0.005f)</c>.</summary>
    public void Turn(Vector2 angles)
    {
        Yaw -= angles.X;
        Pitch = Math.Clamp(Pitch + angles.Y, -CameraTurn.PitchLimit, CameraTurn.PitchLimit);
    }

    /// <summary>Shortens the arm by this many wheel notches, as in <c>boom.Zoom(input.Scroll)</c>.</summary>
    public void Zoom(float notches) => Length = Math.Clamp(Length * MathF.Pow(CameraTurn.ZoomStep, notches), MinLength, MaxLength);

    /// <summary>Turns a stick into a direction on the ground, so forward goes where the camera faces.</summary>
    public Vector3 Walk(Vector2 stick) => CameraTurn.Walk(Yaw, stick);

    /// <summary>The line the arm runs along for a target, to test against your walls.</summary>
    public Ray Arm(Vector3 target) => new(Start(target), CameraTurn.Behind(Yaw, Pitch));

    /// <summary>Call once a tick, after turning. Pass how far along the arm something blocks it, if anything does.</summary>
    public void Update(Vector3 target, float seconds, float? blockedAt = null)
    {
        float room = blockedAt is { } wall ? Math.Clamp(wall - Margin, MinLength, Length) : Length;

        // In at once, since slowly would show the inside of the wall. Out gently.
        _shown = !_placed || room < _shown ? room : float.Lerp(room, _shown, CameraTurn.Remaining(seconds, ReturnLag));

        var arm = Arm(target);
        var next = new Camera(arm.Origin + arm.Direction * _shown, arm.Origin, FieldOfView);
        Previous = _placed ? Current : next;
        Current = next;
        _placed = true;
    }

    /// <summary>Puts the camera straight in place on the next update, for when the target teleports.</summary>
    public void Jump() => _placed = false;

    /// <summary>The camera between the last two ticks, as in <c>view.Camera(boom.At(view.Blend))</c>.</summary>
    public Camera At(float blend) => Camera.Lerp(Previous, Current, blend);

    private Vector3 Start(Vector3 target) => target + Pivot + CameraTurn.Right(Yaw) * Shoulder;
}

/// <summary>Looks out from a character's eyes. Keep one in your game.</summary>
public sealed class FirstPersonCamera
{
    // How far ahead the target sits. Shadows centre on the target, so this keeps the
    // sharpest of them where the player is looking.
    private const float LooksAhead = 4f;

    private bool _placed;

    /// <summary>The angle turned left from facing -z, in radians.</summary>
    public float Yaw { get; set; }

    /// <summary>The angle looked up, in radians.</summary>
    public float Pitch { get; set; }

    /// <summary>How far above the feet the eyes are.</summary>
    public float EyeHeight { get; set; } = 1.7f;

    public float FieldOfView { get; set; } = MathF.PI / 3f;

    /// <summary>The camera on the tick before this one.</summary>
    public Camera Previous { get; private set; }

    public Camera Current { get; private set; }

    /// <summary>Turns by these angles in radians, as in <c>eyes.Turn(input.MouseDelta * 0.003f)</c>.</summary>
    public void Turn(Vector2 angles)
    {
        Yaw -= angles.X;
        Pitch = Math.Clamp(Pitch - angles.Y, -CameraTurn.PitchLimit, CameraTurn.PitchLimit);
    }

    /// <summary>Turns a stick into a direction on the ground, which stays level wherever you look.</summary>
    public Vector3 Walk(Vector2 stick) => CameraTurn.Walk(Yaw, stick);

    /// <summary>Call once a tick, after turning, with where the feet are.</summary>
    public void Update(Vector3 feet)
    {
        var eye = feet + Vector3.UnitY * EyeHeight;
        var next = new Camera(eye, eye - CameraTurn.Behind(Yaw, -Pitch) * LooksAhead, FieldOfView);
        Previous = _placed ? Current : next;
        Current = next;
        _placed = true;
    }

    /// <summary>Puts the camera straight in place on the next update, for when the character teleports.</summary>
    public void Jump() => _placed = false;

    /// <summary>The camera between the last two ticks, as in <c>view.Camera(eyes.At(view.Blend))</c>.</summary>
    public Camera At(float blend) => Camera.Lerp(Previous, Current, blend);
}

/// <summary>
/// An orthographic camera looking down at the ground, which pans and zooms by screen size
/// rather than by moving closer. <see cref="TopDownCamera"/> and <see cref="IsometricCamera"/>
/// are the two angles of it.
/// </summary>
public abstract class OverheadCamera
{
    // Far enough that everything in a game's world sits in front of the camera.
    private const float Standoff = 500f;

    private Vector3 _shownCenter;
    private float _shownSize;
    private float _shownYaw;
    private bool _placed;

    private protected OverheadCamera(float yaw, float pitch)
    {
        Angle = yaw;
        Tilt = pitch;
    }

    /// <summary>The spot on the ground in the middle of the screen.</summary>
    public Vector3 Center { get; set; }

    /// <summary>How many world units the screen shows from bottom to top. Larger shows more.</summary>
    public float Size { get; set; } = 20f;

    public float MinSize { get; set; } = 2f;

    public float MaxSize { get; set; } = 500f;

    /// <summary>Roughly how many seconds the view takes to catch up. Zero means none.</summary>
    public float Lag { get; set; } = 0.15f;

    /// <summary>The camera on the tick before this one.</summary>
    public Camera Previous { get; private set; }

    public Camera Current { get; private set; }

    private protected float Angle { get; set; }

    private protected float Tilt { get; set; }

    /// <summary>Moves the center over the ground, x to the right of the screen and y up it.</summary>
    public void Pan(Vector2 distance) => Center += CameraTurn.Walk(Angle, distance);

    /// <summary>Shows less by this many wheel notches, as in <c>camera.Zoom(input.Scroll)</c>.</summary>
    public void Zoom(float notches) => Size = Math.Clamp(Size * MathF.Pow(CameraTurn.ZoomStep, notches), MinSize, MaxSize);

    /// <summary>Call once a tick to follow something, keeping it in the middle.</summary>
    public void Update(Vector3 center, float seconds)
    {
        Center = center;
        Update(seconds);
    }

    /// <summary>Call once a tick when the player moves the view themselves with <see cref="Pan"/>.</summary>
    public void Update(float seconds)
    {
        float kept = _placed ? CameraTurn.Remaining(seconds, Lag) : 0f;
        _shownCenter = Vector3.Lerp(Center, _shownCenter, kept);
        _shownSize = float.Lerp(Size, _shownSize, kept);
        _shownYaw = float.Lerp(Angle, _shownYaw, kept);

        var position = _shownCenter + CameraTurn.Behind(_shownYaw, Tilt) * Standoff;
        var next = Camera.Orthographic(position, _shownCenter, _shownSize);
        Previous = _placed ? Current : next;
        Current = next;
        _placed = true;
    }

    /// <summary>Puts the camera straight in place on the next update, for when the view teleports.</summary>
    public void Jump() => _placed = false;

    /// <summary>The camera between the last two ticks, as in <c>view.Camera(camera.At(view.Blend))</c>.</summary>
    public Camera At(float blend) => Camera.Lerp(Previous, Current, blend);
}

/// <summary>Looks straight down, with the top of the screen facing -z. Keep one in your game.</summary>
public sealed class TopDownCamera() : OverheadCamera(0f, MathF.PI / 2f);

/// <summary>Looks down at the true isometric angle, where the three axes look equally long. Keep one in your game.</summary>
public sealed class IsometricCamera() : OverheadCamera(MathF.PI / 4f, TrueAngle)
{
    private const float QuarterTurn = MathF.PI / 2f;

    // atan(1 / sqrt 2), which a compile-time constant can't spell.
    private const float TrueAngle = 0.6154797f;

    /// <summary>The angle around the center, in radians.</summary>
    public float Yaw
    {
        get => Angle;
        set => Angle = value;
    }

    /// <summary>How high above the ground, in radians.</summary>
    public float Pitch
    {
        get => Tilt;
        set => Tilt = value;
    }

    /// <summary>Walks the camera around the center by this many quarter turns.</summary>
    public void Turn(int quarters) => Angle += quarters * QuarterTurn;
}
