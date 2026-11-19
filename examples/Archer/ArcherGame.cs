using System.Numerics;
using Rendition;
using Super;

namespace Archer;

/// <summary>
/// "The Clearing": a short film made in the engine. Erika shoots a straw dummy, dodges,
/// runs and leaps onto a boulder, shoots another in slow motion, dives off and puts two
/// arrows in a third. The whole thing is one <see cref="Cutscene"/>; the game only moves
/// her, the arrows and the dummies. R plays it again.
/// </summary>
public sealed class ArcherGame(World world, Content content, Director director, Input input) : IGame, IArcherStage
{
    private const string Nock = "mixamorig:arrow";

    private readonly List<Arrow> _arrows = [];
    private readonly List<Entity> _arrowEntities = [];
    private Entity[] _dummyEntities = [];
    private AnimationController<Pose, Move> _controller = null!;
    private ModelRef _dummyModel;
    private ModelRef _arrowModel;
    private Entity _camera;
    private Entity _erika;

    public Erika Erika { get; private set; } = null!;

    public Dummy[] Dummies { get; private set; } = [];

    /// <summary>The arrow shot last, which the camera can ride along with.</summary>
    public Arrow? LastArrow => _arrows.Count > 0 ? _arrows[^1] : null;

    public void Load()
    {
        var rig = content.Rig("archer/erika.fbx");
        rig.RootMotion("mixamorig:Hips");
        _controller = new AnimationController<Pose, Move>(rig) { CrossFadeSeconds = 0.2f }
            .State(Pose.Idle, rig.Clip("Idle"))
            .State(Pose.Aim, rig.Clip("Aim"))
            .State(Pose.Run, rig.Clip("Run"))
            .State(Pose.Fall, rig.Clip("Fall"))
            .Action(Move.Look, rig.Clip("Look"), fadeSeconds: 0.3f)
            .Action(Move.Draw, rig.Clip("Draw"), fadeSeconds: 0.15f)
            .Action(Move.Recoil, rig.Clip("Recoil"), fadeSeconds: 0.1f)
            .Action(Move.Dodge, rig.Clip("Dodge"), fadeSeconds: 0.12f)
            .Action(Move.Jump, rig.Clip("Jump"), fadeSeconds: 0.08f)
            .Action(Move.Land, rig.Clip("Land"), fadeSeconds: 0.1f)
            .Action(Move.Dive, rig.Clip("Dive"), fadeSeconds: 0.15f)
            .Action(Move.Cocky, rig.Clip("Cocky"), fadeSeconds: 0.3f);

        new Clearing(world, content);
        _dummyModel = content.Model("archer-props/dummy.glb");
        _arrowModel = content.Model("archer-props/arrow.glb");
        Daylight();
        Begin();
        _camera = world.Spawn(Film.End(Erika.Position));
    }

    public void Update(GameTime time)
    {
        if (input.Pressed(Key.R))
        {
            Begin();
        }

        float seconds = time.DeltaSeconds;
        Erika.Update(seconds);
        foreach (var arrow in _arrows)
        {
            arrow.Update(seconds);
        }

        foreach (var dummy in Dummies)
        {
            dummy.Update(seconds);
        }

        Place();
    }

    /// <summary>Lets an arrow go from her bow at a dummy.</summary>
    public void Shoot(Dummy target)
    {
        var arrow = new Arrow(Erika.JointAt(Nock), target);
        _arrows.Add(arrow);
        _arrowEntities.Add(world.Spawn(Transform.From(arrow.Transform), new Model(_arrowModel)));
    }

    /// <summary>The sun and its warm fill, the sky, the shadows and the grade.</summary>
    private void Daylight()
    {
        world.Spawn(
            // Golden hour: the sun is 14 degrees up, and the air it comes through turns its white light gold.
            new Sun(new Vector3(0.9f, 0.24f, 0.3f), new Vector3(12f, 11f, 10f), CastsShadows: true),
            new Sky(new SkySettings
            {
                Brightness = 0.9f,
                Turbidity = 3f,
                HazeStart = 45f,
                HazeEnd = 150f,
            }),
            new Shadows(14f, 3),
            new Finish(new PostSettings
            {
                Exposure = 0.2f,
                Contrast = 0.18f,
                Saturation = 0.05f,
                Vignette = 0.4f,
                Sharpen = 0.3f,
                Grain = 0.012f,
            }));

        // The live probes see the clearing as it is: the forest ring hides the warm horizon, so
        // the bounce comes from the blue zenith overhead. A warm fill keeps the targets in
        // golden-hour light instead of the cool blue the old fill gave them.
        world.Spawn(new Sun(new Vector3(-0.6f, 0.6f, -0.4f), new Vector3(0.44f, 0.32f, 0.2f)));
    }

    /// <summary>Moves her, the dummies and the arrows' entities to where the game has them, and the camera after her.</summary>
    private void Place()
    {
        world.Get<Transform>(_erika) = Transform.From(Erika.Transform);
        for (int i = 0; i < Dummies.Length; i++)
        {
            world.Get<Transform>(_dummyEntities[i]) = Transform.From(Dummies[i].Transform);
        }

        for (int i = 0; i < _arrows.Count; i++)
        {
            world.Get<Transform>(_arrowEntities[i]) = Transform.From(_arrows[i].Transform);
        }

        // The director holds the camera while the film plays; this is where it leaves off.
        world.Get<Camera>(_camera) = Film.End(Erika.Position);
    }

    // Sets everything where the film starts and rolls it, in place of the last run's.
    private void Begin()
    {
        // The first run has no cast yet, and an unset entity could be another live one.
        if (_dummyEntities.Length > 0)
        {
            foreach (var entity in (ReadOnlySpan<Entity>)[_erika, .. _dummyEntities, .. _arrowEntities])
            {
                world.Destroy(entity);
            }
        }

        _arrows.Clear();
        _arrowEntities.Clear();
        Erika = new Erika(_controller.Start(Pose.Idle), Vector3.Zero, 0f);
        _erika = world.Spawn(Transform.From(Erika.Transform), new Animated(Erika.Animator));
        var rockTop = Clearing.Rock + new Vector3(0f, Film.RockHeight, 0f);
        Dummies =
        [
            new Dummy(new Vector3(1.5f, 0f, 13f), Vector3.Zero),
            new Dummy(new Vector3(5f, 0f, 16.5f), rockTop),
            new Dummy(new Vector3(-8.5f, 0f, 20f), Film.DiveEnd),
        ];
        _dummyEntities = [.. Dummies.Select(dummy => world.Spawn(Transform.From(dummy.Transform), new Model(_dummyModel)))];
        director.Play(Film.Make(this));
    }
}
