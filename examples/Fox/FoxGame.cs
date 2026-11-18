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
/// about when it stops, and Look turns its head without breaking stride. Each paw kicks up
/// a little dust as it lands, from the clips' <c>step</c> events.
/// </summary>
public sealed class FoxGame(Content content, Actions<Control> actions) : IGame
{
    public const float WalkSpeed = 0.6f;
    public const float RunSpeed = 2.4f;
    private const float StartSpeed = 1.2f;
    private const float Acceleration = 1.5f; // Metres per second, per second.
    private const float PathRadius = 4f;
    private const float StopSpeed = 0.05f;
    private const float DustSeconds = 1f;
    private const int FoxKey = 1;
    private const int GroundKey = 2;
    private const int DustKey = 1000;

    /// <summary>A small puff of dry earth where a paw lands, settling as it drifts out.</summary>
    public static readonly ParticleEffect StepDust = new ParticleEffect().Add(new ParticleEmitter(count: 8, lifetime: DustSeconds)
        .FromDisc(0.05f)
        .Outward(0.45f)
        .Velocity(new Vector3(0f, 0.12f, 0f))
        .Drag(3f)
        .Gravity(new Vector3(0f, 0.08f, 0f))
        .Color(new Vector3(0.62f, 0.52f, 0.38f), new Vector3(0.55f, 0.5f, 0.42f))
        .Opacity(0.4f)
        .Fade(0.1f, 0.6f)
        .Size(0.06f, 0.26f));

    private readonly List<Puff> _puffs = [];
    private Animator _animator = null!;
    private float _angle;
    private float _previousAngle;
    private int _steps;

    /// <summary>How fast the fox wants to go, in metres per second.</summary>
    public float TargetSpeed { get; private set; } = StartSpeed;

    public float Speed { get; private set; } = StartSpeed;

    public Animator Animator => _animator;

    /// <summary>How many paws have landed so far.</summary>
    public int Steps => _steps;

    /// <summary>The dust still settling: where each paw landed, on the ground, and the tick it did.</summary>
    public IReadOnlyList<Puff> Puffs => _puffs;

    public IReadOnlyDictionary<string, InputScript> Scripts => new Dictionary<string, InputScript>
    {
        ["look"] = new InputScript().Click(new Vector2(800f, 450f)).Wait(25),
    };

    public void Load()
    {
        actions.Bind(Control.Pace, Binding.Axis(Key.S, Key.W), Binding.Axis(Key.Down, Key.Up));
        actions.Bind(Control.Look, Key.Space, MouseButton.Left);

        var rig = content.Rig("fox/fox.glb");
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
        KickUpDust(time);

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
            _animator.Moves.Play(FoxAction.Look);
        }
    }

    /// <summary>
    /// Puts dust where each paw landed on the last tick. The events come from the tick after
    /// the last Update, so the fox still stands where that Update left it.
    /// </summary>
    private void KickUpDust(GameTime time)
    {
        _puffs.RemoveAll(puff => (time.Tick - puff.Tick) * time.TickSeconds > DustSeconds);
        foreach (var step in _animator.Events)
        {
            if (step is { Name: "step", Kind: AnimationEventKind.Moment, Joint: { } paw })
            {
                var landed = (paw * FoxTransform(_angle)).Translation;
                _puffs.Add(new Puff(new Vector3(landed.X, 0f, landed.Z), time.Tick, _steps++));
            }
        }
    }

    private static Matrix4x4 FoxTransform(float angle)
    {
        var position = new Vector3(MathF.Cos(angle), 0f, MathF.Sin(angle)) * PathRadius;
        var heading = new Vector3(-MathF.Sin(angle), 0f, MathF.Cos(angle));
        return Matrix4x4.CreateRotationY(MathF.Atan2(heading.X, heading.Z)) * Matrix4x4.CreateTranslation(position);
    }

    public void Draw(View view)
    {
        float angle = float.Lerp(_previousAngle, _angle, view.Blend);
        var fox = FoxTransform(angle);
        var position = fox.Translation;
        var heading = new Vector3(-MathF.Sin(angle), 0f, MathF.Cos(angle));

        var side = Vector3.Normalize(position) * 2.6f;
        view.Camera(position + side + new Vector3(0f, 1.1f, 0f) - heading * 0.8f, position + new Vector3(0f, 0.35f, 0f), MathF.PI / 5f);
        view.Sun(new Vector3(0.4f, 1f, 0.3f), new Vector3(4f, 3.8f, 3.4f));
        view.Shadows(4f);

        view.Model(GroundKey, ModelRef.Cube, Matrix4x4.CreateScale(40f, 0.1f, 40f) * Matrix4x4.CreateTranslation(0f, -0.05f, 0f), new Vector3(0.33f, 0.45f, 0.22f));
        view.Model(FoxKey, _animator, fox);
        foreach (var puff in _puffs)
        {
            view.Particles(DustKey + puff.Number, StepDust, puff.At, view.SecondsSince(puff.Tick));
        }
    }

    /// <summary>Dust from one paw landing: where, the tick it landed, and how many landed before it, which names its particles.</summary>
    public readonly record struct Puff(Vector3 At, long Tick, int Number);
}
