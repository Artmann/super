using System.Numerics;
using Super;

namespace Fox;

/// <summary>What the player can ask of the fox.</summary>
public enum Control
{
    /// <summary>Faster or slower: W and S, or up and down.</summary>
    Pace,

    /// <summary>Look around without stopping.</summary>
    Look,
}

/// <summary>The fox's base states. Each loops until the game picks another.</summary>
public enum FoxState
{
    Idle,
    Moving,
}

/// <summary>What the fox can do over its base state.</summary>
public enum FoxAction
{
    Look,
}

/// <summary>
/// A fox trotting round a meadow. Its pace blends walk into run, it stands and looks
/// about when it stops, and Look turns its head without breaking stride.
/// </summary>
public sealed class FoxGame(Content content, Actions<Control> actions) : IGame
{
    public const float WalkSpeed = 0.6f;
    public const float RunSpeed = 2.4f;
    private const float StartSpeed = 1.2f;
    private const float Acceleration = 1.5f; // Metres per second, per second.
    private const float PathRadius = 4f;
    private const float StopSpeed = 0.05f;
    private const int FoxKey = 1;
    private const int GroundKey = 2;

    private Animator _animator = null!;
    private float _angle;
    private float _previousAngle;

    /// <summary>How fast the fox wants to go, in metres per second.</summary>
    public float TargetSpeed { get; private set; } = StartSpeed;

    public float Speed { get; private set; } = StartSpeed;

    public Animator Animator => _animator;

    public IReadOnlyDictionary<string, InputScript> Scripts => new Dictionary<string, InputScript>
    {
        ["look"] = new InputScript().Click(new Vector2(800f, 450f)).Wait(25),
    };

    public void Load()
    {
        actions.Bind(Control.Pace, Binding.Axis(Key.S, Key.W), Binding.Axis(Key.Down, Key.Up));
        actions.Bind(Control.Look, Key.Space, MouseButton.Left);

        var rig = content.Rig("fox.rmodel");
        var controller = new AnimationController<FoxState, FoxAction>(rig) { CrossFadeSeconds = 0.3f }
            .State(FoxState.Idle, rig.Clip("Survey"))
            .State(FoxState.Moving, rig.Blend((rig.Clip("Walk"), WalkSpeed), (rig.Clip("Run"), RunSpeed)))
            .Action(FoxAction.Look, rig.Clip("Survey"), rig.Mask("b_Neck_04"), fadeSeconds: 0.4f);
        _animator = controller.Start(FoxState.Moving);
        _animator.Parameter = Speed;
    }

    public void Update(GameTime time)
    {
        float dt = time.TickSeconds;
        TargetSpeed = Math.Clamp(TargetSpeed + actions.Value(Control.Pace) * Acceleration * dt, 0f, RunSpeed);
        Speed = Speed < TargetSpeed
            ? MathF.Min(Speed + Acceleration * dt, TargetSpeed)
            : MathF.Max(Speed - Acceleration * dt, TargetSpeed);

        _previousAngle = _angle;
        _angle += Speed / PathRadius * dt;

        _animator.Set(Speed < StopSpeed ? FoxState.Idle : FoxState.Moving);
        _animator.Parameter = Speed;
        if (actions.Pressed(Control.Look))
        {
            _animator.Play(FoxAction.Look);
        }
    }

    public void Draw(View view)
    {
        float angle = float.Lerp(_previousAngle, _angle, view.Blend);
        var position = new Vector3(MathF.Cos(angle), 0f, MathF.Sin(angle)) * PathRadius;
        var heading = new Vector3(-MathF.Sin(angle), 0f, MathF.Cos(angle));
        var fox = Matrix4x4.CreateRotationY(MathF.Atan2(heading.X, heading.Z)) * Matrix4x4.CreateTranslation(position);

        var side = Vector3.Normalize(position) * 2.6f;
        view.Camera(position + side + new Vector3(0f, 1.1f, 0f) - heading * 0.8f, position + new Vector3(0f, 0.35f, 0f), MathF.PI / 5f);
        view.Sun(new Vector3(0.4f, 1f, 0.3f), new Vector3(4f, 3.8f, 3.4f));
        view.Shadows(4f);

        view.Model(GroundKey, ModelRef.Cube, Matrix4x4.CreateScale(40f, 0.1f, 40f) * Matrix4x4.CreateTranslation(0f, -0.05f, 0f), new Vector3(0.33f, 0.45f, 0.22f));
        view.Model(FoxKey, _animator, fox);
    }
}
