using System.Numerics;
using Super;

namespace ColorCube;

/// <summary>What a player can do to the cube.</summary>
public enum Control
{
    /// <summary>Jump to the next random color now.</summary>
    Cycle,

    /// <summary>Add some spin of your own, in a direction.</summary>
    Spin,
}

/// <summary>A spinning cube that changes to a random color at random times, and listens to the player.</summary>
public class SpinningCube(GameRandom random, Actions<Control> actions) : IGame
{
    private const int CubeKey = 1;
    private const float TurnsPerSecond = 0.1f;
    private const float SteerTurnsPerSecond = 0.5f;

    private readonly RandomStream _rolls = random.Stream("colors");
    private float _angle;
    private float _previousAngle;
    private Vector2 _steered;
    private Vector2 _previousSteered;
    private int _ticksUntilChange;

    public Vector3 Color { get; private set; }

    /// <summary>The spin the player asked for on the last tick.</summary>
    public Vector2 Steer { get; private set; }

    public void Load()
    {
        actions.Bind(Control.Cycle, Key.Space, GamepadButton.South, MouseButton.Left);
        actions.Bind(Control.Spin, Keys.WASD, Keys.Arrows, GamepadStick.Left);
        ChangeColor();
    }

    public void Update(GameTime time)
    {
        _previousAngle = _angle;
        _previousSteered = _steered;
        _angle += MathF.Tau * TurnsPerSecond * time.TickSeconds;

        // X steers around the upright axis and Y tips the cube over, on top of the steady spin.
        Steer = actions.Vector(Control.Spin);
        _steered += Steer * (MathF.Tau * SteerTurnsPerSecond * time.TickSeconds);

        if (actions.Pressed(Control.Cycle))
        {
            ChangeColor();
        }

        _ticksUntilChange--;
        if (_ticksUntilChange <= 0)
        {
            ChangeColor();
        }
    }

    public void Draw(View view)
    {
        view.Camera(position: new Vector3(0f, 1.5f, 3.5f), lookAt: Vector3.Zero);
        view.Sun(direction: new Vector3(1f, 2f, 1f), color: new Vector3(3f));

        var transform = view.Smooth(Spin(_previousAngle, _previousSteered), Spin(_angle, _steered));
        view.Model(CubeKey, ModelRef.Cube, transform, Color);
    }

    private static Matrix4x4 Spin(float angle, Vector2 steered) =>
        Matrix4x4.CreateRotationY(angle + steered.X) * Matrix4x4.CreateRotationX(angle * 0.6f + steered.Y);

    /// <summary>A vivid color at full saturation, chosen by its place around the color wheel.</summary>
    private static Vector3 ColorFromHue(float hue)
    {
        static float Channel(float hue, float offset) =>
            Math.Clamp(MathF.Abs(((hue * 6f + offset) % 6f) - 3f) - 1f, 0f, 1f);

        return new Vector3(Channel(hue, 0f), Channel(hue, 4f), Channel(hue, 2f));
    }

    private void ChangeColor()
    {
        Color = ColorFromHue(_rolls.Float());

        // Somewhere between half a second and three seconds, at 30 ticks per second.
        _ticksUntilChange = _rolls.Between(15, 90);
    }
}
