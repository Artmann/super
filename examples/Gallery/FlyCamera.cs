using System.Numerics;
using Super;

namespace Gallery;

/// <summary>
/// The Gallery's camera. It tours the rooms on its own; F lets you fly it with WASD, Q and E and the mouse, and F again
/// goes back to the tour.
/// </summary>
internal sealed class FlyCamera(Input input)
{
    private const float FlySpeed = 4f;
    private const float TurnPerPixel = 0.0025f;

    private bool _flying;
    private Vector3 _position;
    private Vector3 _previousPosition;
    private float _yaw;
    private float _pitch;
    private float _previousYaw;
    private float _previousPitch;

    /// <summary>Puts the flying camera at <paramref name="position"/>, where it starts if F is pressed straight away.</summary>
    public void Start(Vector3 position)
    {
        _position = position;
        _previousPosition = _position;
    }

    /// <summary>Turns flying on and off with F, and flies one tick of <paramref name="tickSeconds"/>.</summary>
    public void Update(Tour tour, float seconds, float tickSeconds)
    {
        if (input.Pressed(Key.F))
        {
            _flying = !_flying;
            if (_flying)
            {
                // Take over from wherever the tour is, looking the same way.
                var shot = tour.At(seconds);
                var look = Vector3.Normalize(shot.Target - shot.Position);
                _position = shot.Position;
                _yaw = MathF.Atan2(-look.X, -look.Z);
                _pitch = MathF.Asin(look.Y);
            }
        }

        input.Mouse.Locked = _flying;
        _previousPosition = _position;
        _previousYaw = _yaw;
        _previousPitch = _pitch;
        if (!_flying)
        {
            return;
        }

        var turn = input.Mouse.Delta * TurnPerPixel;
        _yaw -= turn.X;
        _pitch = Math.Clamp(_pitch - turn.Y, -1.5f, 1.5f);

        var forward = Forward(_yaw, 0f);
        var right = new Vector3(-forward.Z, 0f, forward.X);
        var step = Vector3.Zero;
        step += forward * (Axis(Key.W, Key.S));
        step += right * (Axis(Key.D, Key.A));
        step.Y += Axis(Key.E, Key.Q);
        _position += step * (FlySpeed * tickSeconds);
    }

    /// <summary>Points the view's camera: where it flies, or the tour's shot at <paramref name="seconds"/>.</summary>
    public void Aim(View view, Tour tour, float seconds)
    {
        if (_flying)
        {
            var position = Vector3.Lerp(_previousPosition, _position, view.Blend);
            float yaw = _previousYaw + (_yaw - _previousYaw) * view.Blend;
            float pitch = _previousPitch + (_pitch - _previousPitch) * view.Blend;
            view.Camera(position, position + Forward(yaw, pitch), 1f);
        }
        else
        {
            view.Camera(tour.At(seconds));
        }
    }

    private float Axis(Key positive, Key negative) =>
        (input.Down(positive) ? 1f : 0f) - (input.Down(negative) ? 1f : 0f);

    private static Vector3 Forward(float yaw, float pitch) =>
        new(-MathF.Sin(yaw) * MathF.Cos(pitch), MathF.Sin(pitch), -MathF.Cos(yaw) * MathF.Cos(pitch));
}
