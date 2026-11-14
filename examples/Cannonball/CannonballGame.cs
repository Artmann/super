using System.Numerics;
using Super;
using Super.Ecs;
using Super.Physics;
using Super.Ui;

namespace Cannonball;

/// <summary>What the stuntman is doing between shots.</summary>
public enum Stage
{
    /// <summary>Sitting in the cannon, head out of the muzzle.</summary>
    Loaded,

    /// <summary>Flying, flailing, until the first hard hit.</summary>
    Flying,

    /// <summary>Limp, bouncing and rolling until he comes to rest.</summary>
    Tumbling,

    /// <summary>Getting up, then standing for a moment before the next shot.</summary>
    GettingUp,
}

/// <summary>The stuntman's looping clips.</summary>
public enum Stance
{
    Idle,
    Flail,
}

/// <summary>The clips played once over the loop.</summary>
public enum Move
{
    /// <summary>Getting up from lying on the back.</summary>
    StandUp,

    /// <summary>Getting up from lying on the front.</summary>
    StandUpFromFront,
}

/// <summary>
/// A human cannonball. The cannon fires the stuntman over a field of crates and balls; he flails in the air, goes limp
/// when he hits something, tumbles to a stop and gets up, and then the cannon fires again. R fires at once, and D shows
/// the ragdoll's bodies.
/// </summary>
public sealed class CannonballGame(World world, Content content, Physics physics, Input input) : IGame
{
    private readonly CannonballField _field = new(world);
    private readonly StuntFlight _flight = new(world, physics);
    private readonly RagdollOverlay _overlay = new(physics);
    private AnimationController<Stance, Move> _controller = null!;
    private RagdollBody _body = null!;
    private bool _showBodies;

    public Stage Stage => _flight.Stage;

    /// <summary>The stuntman: a rigged model with a ragdoll.</summary>
    public Entity Stuntman => _flight.Stuntman;

    /// <summary>How hard the hit was that knocked him limp, in metres per second, or 0 before one.</summary>
    public float HitSpeed => _flight.HitSpeed;

    /// <summary>Where the stuntman's hips are drawn in the world, which the camera follows.</summary>
    public Vector3 Hips => JointAt("mixamorig6:Hips");

    /// <summary>The way the stuntman's head is from his hips, in the world.</summary>
    public Vector3 Up => Vector3.Normalize(JointAt("mixamorig6:Head") - Hips);

    private Vector3 JointAt(string joint) => (world.Get<Animated>(Stuntman).Value.Joint(joint) * world.Get<Transform>(Stuntman).Matrix).Translation;

    /// <summary>Follows the stuntman from behind, to the side and above, so the whole flight crosses the screen and the camera looks over the wall at where they land.</summary>
    public FollowCamera Camera { get; } = new() { Offset = new Vector3(-8f, 11f, -8f), Lag = 0.35f, FieldOfView = 0.8f, WideAs = 16f / 9f };

    /// <summary>For <c>--script bodies</c>: shows the ragdoll's bodies, so a screenshot can show them.</summary>
    public IReadOnlyDictionary<string, InputScript> Scripts => new Dictionary<string, InputScript>
    {
        ["bodies"] = new InputScript().Press(Key.D),
    };

    public UiView? Ui => new VStack(HorizontalAlignment.Leading, Spacing: 4f)
    {
        new Text(_flight.Caption()),
        new Text("R fires again   D shows the ragdoll's bodies"),
    }
    .Padding(16f)
    .Frame(maxWidth: UiExtensions.Infinity, maxHeight: UiExtensions.Infinity, alignment: Alignment.TopLeading);

    public void Load()
    {
        var rig = content.Rig("cannonball/stuntman.fbx");
        _body = RagdollBody.Humanoid(rig);        _controller = new AnimationController<Stance, Move>(rig) { CrossFadeSeconds = 0.25f }
            .State(Stance.Idle, rig.Clip("Idle"))
            .State(Stance.Flail, rig.Clip("Flail"))
            // The get-ups start the moment the ragdoll turns off, so the body is placed to match their first frame.
            .Action(Move.StandUp, rig.Clip("StandUp"), fadeSeconds: 0f)
            .Action(Move.StandUpFromFront, rig.Clip("StandUp3"), fadeSeconds: 0f);

        _field.Build();
        Load(fresh: false);
    }

    public void Update(GameTime time)
    {
        float seconds = time.TickSeconds;
        if (input.Pressed(Key.D))
        {
            _showBodies = !_showBodies;
        }

        if (input.Pressed(Key.R) || _flight.Update(seconds))
        {
            Load(fresh: true);
        }

        Camera.Update(Hips, seconds);
    }

    public void Draw(View view)
    {
        view.Camera(Camera.At(view.Blend));
        view.Sun(new Vector3(0.5f, 0.8f, -0.35f), new Vector3(4.2f, 3.8f, 3.2f), shadows: true);
        view.Sky(new SkySettings
        {
            Zenith = new Vector3(0.18f, 0.38f, 0.8f),
            Horizon = new Vector3(0.75f, 0.82f, 0.9f),
            Ground = new Vector3(0.25f, 0.3f, 0.15f),
            Brightness = 1f,
            HazeStart = 60f,
            HazeEnd = 220f,
        });
        view.Shadows(20f, cascades: 3);
        view.Models(world);
        CannonballField.DrawCannon(view);
        if (_showBodies)
        {
            _overlay.Draw(view, Stuntman);
        }
    }

    /// <summary>Sits a new stuntman in the cannon, and with <paramref name="fresh"/> stacks the field up again.</summary>
    private void Load(bool fresh)
    {
        if (fresh)
        {
            world.Destroy(Stuntman);
            _field.Build();
        }

        // Feet down the barrel and head out of the muzzle, so the body lies along the way he'll fly.
        float length = CannonballField.BarrelLength - 1f;
        var placed = Transform.Facing(CannonballField.Breech + (CannonballField.Aim * length), Vector3.Normalize(Vector3.Cross(Vector3.UnitX, CannonballField.Aim)), CannonballField.Aim, Vector3.One);
        var stuntman = world.Create();
        world.Add(stuntman, placed);
        world.Add(stuntman, new Animated(_controller.Start(Stance.Idle)));
        world.Add(stuntman, new Ragdoll(_body) { Mode = RagdollMode.Off, BlendSeconds = 0.5f });
        world.Add(stuntman, new Smoothed());
        _flight.Load(stuntman);
        Camera.Jump();
    }
}
