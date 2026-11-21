using System.Numerics;
using Examples;
using Rendition;
using Super;
using Super.Ui;

namespace Hamlet;

/// <summary>
/// A village on a hill through a two-minute day: the sun and the moon, a lamp or two in
/// every building, a forge's fire, and the glow from lit windows. At dusk the lamps come on
/// one by one and flicker. Every building is a baked chunk, so the light bounced round its
/// rooms is baked once per kind and shared by every copy. The camera circles the village,
/// then walks into a cottage to see the window light by day and the lamp by night. R makes
/// a new village from the next seed, P shows the light probes, L eases into the next look, and 1, 2 and 3 switch
/// between live lighting, the bakes and direct light only.
/// </summary>
public sealed class HamletGame(World world, GameRandom random, Input input, Content content, Graphics graphics, ArtStyle style) : IGame
{
    private readonly LightingSwitch _lighting = new(input, graphics);
    private Kinds _kinds = null!;
    private CameraTour _tour = null!;
    private VillageScene _scene = null!;
    private VillagePieces _pieces = null!;
    private Entity _camera;
    private Entity _daylight;
    private Entity _moon;
    private int _villages;

    /// <summary>The village now shown.</summary>
    internal Village Village => _scene.Village;

    /// <summary>The village's entities.</summary>
    internal VillageScene Scene => _scene;

    /// <summary>The lighting now on.</summary>
    internal LightingMode CurrentLighting => _lighting.Current;

    /// <summary>
    /// Scripts for <c>--script</c>: <c>live</c> presses key 1, so a shot shows live lighting; <c>probes</c> presses P,
    /// so a shot shows the light probes.
    /// </summary>
    public IReadOnlyDictionary<string, InputScript> Scripts => new Dictionary<string, InputScript>
    {
        ["live"] = new InputScript().Press(Key.D1).Wait(1),
        ["probes"] = new InputScript().Press(Key.P).Wait(1),
    };

    public void Load()
    {
        var palette = new Palette(content);
        _kinds = new Kinds(content, palette);
        _pieces = new VillagePieces(new Cart(content, palette), content.Mesh(Shapes.Quad(Vector2.One, palette.Pane)));

        // The sun, the sky, the shadows and the finish, which follow the time of day; and the moon, while the sun is down.
        _daylight = world.Spawn(DayCycle.Sun(0f), DayCycle.Sky(0f), new Shadows(90f, 3), DayCycle.Expose(0f, 0f));
        _moon = world.Spawn(DayCycle.Moon(0f) ?? default, new Hidden());
        Build(random.Seed, 0f);
        _camera = world.Spawn(Camera(_tour.Pose(0f)));
        Light(0f);
        _lighting.Use(_lighting.Current);
    }

    public void Update(GameTime time)
    {
        _lighting.Update();

        // The live light probes drawn over the scene, to watch the cart's light move between chunks.
        if (input.Pressed(Key.P))
        {
            graphics.DebugView = graphics.DebugView == DebugView.Probes ? DebugView.None : DebugView.Probes;
        }

        // The next of the looks Super comes with, eased in over a second.
        if (input.Pressed(Key.L))
        {
            int next = (Looks.All.ToList().FindIndex(look => look.Name == style.Current.Name) + 1) % Looks.All.Count;
            style.Use(Looks.All[next], seconds: 1f);
        }

        float seconds = time.Tick * time.TickSeconds;
        if (input.Pressed(Key.R))
        {
            // The old village's ground models are unloaded, so its entities go in the same tick.
            _villages++;
            _scene.Destroy();
            Build(random.Seed + _villages, seconds);
            world.Teleport(_camera, Camera(_tour.Pose(seconds)));
        }

        Light(seconds);
    }

    /// <summary>The lighting modes in the bottom-left corner, with the current one in bold, whether P shows the probes and the look L steps through.</summary>
    public UiView? Ui => _lighting.Panel(modes =>
    {
        var probes = new Text("P  Light probes");
        modes.Add(graphics.DebugView == DebugView.Probes ? probes.Bold() : probes.Foreground(LightingSwitch.Muted));
        modes.Add(new Text($"L  Look: {style.Current.Name ?? Looks.Natural.Name}").Foreground(LightingSwitch.Muted));
    });

    /// <summary>Moves the camera along its tour, and sets the day's light, the lamps and the cart for <paramref name="seconds"/>.</summary>
    private void Light(float seconds)
    {
        var pose = _tour.Pose(seconds);
        world.Get<Camera>(_camera) = Camera(pose);
        bool indoors = pose.Indoors > 0.5f;

        world.Get<Sun>(_daylight) = DayCycle.Sun(seconds);
        world.Get<Sky>(_daylight) = DayCycle.Sky(seconds);
        world.Get<Finish>(_daylight) = DayCycle.Expose(seconds, pose.Indoors);

        // Close up indoors: sharper shadows, and the room's corners hide more of the sky.
        world.Get<Shadows>(_daylight) = indoors ? new Shadows(18f, 2) : new Shadows(90f, 3);
        if (indoors && !world.Has<AmbientOcclusion>(_daylight))
        {
            world.Add(_daylight, new AmbientOcclusion(1.5f, 1f));
        }
        else if (!indoors && world.Has<AmbientOcclusion>(_daylight))
        {
            world.Remove<AmbientOcclusion>(_daylight);
        }

        var moon = DayCycle.Moon(seconds);
        if (moon is { } light)
        {
            world.Get<Sun>(_moon) = light;
        }

        Spawning.Show(world, _moon, moon is not null);
        _scene.Update(seconds, indoors ? _tour.Cottage.Index : -1);
    }

    private static Camera Camera(CameraPose pose) => new(pose.Position, pose.Target, pose.FieldOfView);

    private void Build(int seed, float seconds)
    {
        var village = Village.Generate(seed, _kinds);
        _tour = new CameraTour(village);
        _scene = new VillageScene(world, content, village, _pieces, seconds);
    }
}
