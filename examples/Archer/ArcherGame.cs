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
public sealed class ArcherGame(Content content, Director director, Input input) : IGame
{
    private const int ErikaKey = 1;
    private const int DummyKey = 10;
    private const int ArrowKey = 100;
    private const string Nock = "mixamorig:arrow";

    private readonly List<Arrow> _arrows = [];
    private AnimationController<Pose, Move> _controller = null!;
    private Clearing _clearing = null!;
    private ModelRef _dummyModel;
    private ModelRef _arrowModel;

    public Erika Erika { get; private set; } = null!;

    public Dummy[] Dummies { get; private set; } = [];

    /// <summary>The arrow shot last, which the camera can ride along with.</summary>
    public Arrow? LastArrow => _arrows.Count > 0 ? _arrows[^1] : null;

    public void Load()
    {
        var rig = content.Rig("archer.rmodel");
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

        _clearing = new Clearing(content);
        _dummyModel = content.Model("props/dummy.rmodel");
        _arrowModel = content.Model("props/arrow.rmodel");
        Begin();
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
    }

    public void Draw(View view)
    {
        // The director holds the camera while the film plays; this is where it leaves off.
        view.Camera(Film.End(Erika.Position));
        view.Sun(new Vector3(0.8f, 0.3f, -0.45f), new Vector3(6.2f, 4.1f, 2.4f));
        view.Sun(new Vector3(-0.5f, 0.7f, 0.5f), new Vector3(0.22f, 0.3f, 0.45f));
        view.Sky(new SkySettings
        {
            Zenith = new Vector3(0.18f, 0.36f, 0.85f),
            Horizon = new Vector3(1f, 0.62f, 0.32f),
            Ground = new Vector3(0.22f, 0.2f, 0.13f),
            Brightness = 0.9f,
            HazeStart = 25f,
            HazeEnd = 110f,
        });
        view.Shadows(14f, cascades: 3);
        view.Finish(new PostSettings
        {
            Contrast = 0.12f,
            Saturation = 0.08f,
            Vignette = 0.35f,
            Sharpen = 0.3f,
            Grain = 0.012f,
        });

        _clearing.Draw(view);
        view.Model(ErikaKey, Erika.Animator, view.Smooth(Erika.Previous, Erika.Transform));
        for (int i = 0; i < Dummies.Length; i++)
        {
            view.Model(DummyKey + i, _dummyModel, view.Smooth(Dummies[i].Previous, Dummies[i].Transform));
        }

        for (int i = 0; i < _arrows.Count; i++)
        {
            view.Model(ArrowKey + i, _arrowModel, view.Smooth(_arrows[i].Previous, _arrows[i].Transform));
        }
    }

    /// <summary>Lets an arrow go from her bow at a dummy.</summary>
    public void Shoot(Dummy target) => _arrows.Add(new Arrow(Erika.JointAt(Nock), target));

    // Sets everything where the film starts and rolls it.
    private void Begin()
    {
        _arrows.Clear();
        Erika = new Erika(_controller.Start(Pose.Idle), Vector3.Zero, 0f);
        var rockTop = Clearing.Rock + new Vector3(0f, Film.RockHeight, 0f);
        Dummies =
        [
            new Dummy(new Vector3(1.5f, 0f, 13f), Vector3.Zero),
            new Dummy(new Vector3(5f, 0f, 16.5f), rockTop),
            new Dummy(new Vector3(-8.5f, 0f, 20f), Film.DiveEnd),
        ];
        director.Play(Film.Make(this));
    }
}
