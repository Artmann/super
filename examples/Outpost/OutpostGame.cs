using System.Numerics;
using Super;
using Super.Ui;

namespace OutpostExample;

public sealed class OutpostGame(Field field, Look look, Scenery scenery, Player player, Enemies enemies, Towers towers, Shots shots, Waves waves, States<Screen> screen, Actions<Control> actions, Input input) : IGame
{
    private static readonly Vector3 Grass = new(0.42f, 0.66f, 0.34f);
    private static readonly Vector3 Sand = new(0.9f, 0.76f, 0.52f);
    private static readonly Vector3 Stone = new(0.55f, 0.53f, 0.58f);
    private static readonly Vector3 BaseRoof = new(0.85f, 0.42f, 0.36f);

    public Camera Camera { get; } = new(new Vector3(0, 22, 16), Vector3.Zero) { WideAs = 16f / 9f };

    public Player Player => player;

    public Enemies Enemies => enemies;

    public Towers Towers => towers;

    public Shots Shots => shots;

    public Waves Waves => waves;

    public UiView? Ui => screen.Current switch
    {
        Screen.Title => new Banner("Outpost", "Click or press Space to start"),
        Screen.Won => new Banner("You held the outpost!", "Press R to play again"),
        Screen.Lost => new Banner("The outpost fell", "Press R to play again"),
        _ => new Hud(player.Gold, player.Lives, waves.Number, Waves.Count),
    };

    public IReadOnlyDictionary<string, InputScript> Scripts => new Dictionary<string, InputScript>
    {
        // Starts the game, for screenshots: --script start
        ["start"] = new InputScript().Press(Key.Space),
        // Starts, then builds a tower on the middle tile and one by the first bend: --script towers
        ["towers"] = new InputScript().Press(Key.Space).Wait(1).Click(new Vector2(640, 360)).Wait(1).Click(new Vector2(411, 261)).Wait(5),
    };

    public void Load()
    {
        look.Load();

        actions.Bind(Control.Build, MouseButton.Left);
        actions.Bind(Control.Start, Key.Space, MouseButton.Left);
        actions.Bind(Control.Restart, Key.R);

        screen.OnEnter(Screen.Playing, NewGame);
    }

    public void Update(GameTime time)
    {
        switch (screen.Current)
        {
            case Screen.Title when actions.Pressed(Control.Start):
            case Screen.Won or Screen.Lost when actions.Pressed(Control.Restart):
                screen.Set(Screen.Playing);
                break;
            case Screen.Playing:
                Play(time);
                break;
        }
    }

    private void Play(GameTime time)
    {
        if (actions.Pressed(Control.Build))
        {
            // Follow the line from the camera through the cursor down to the ground, and build on that tile.
            if (input.Mouse.Ray(Camera).Ground() is { } spot && field.TileAt(spot) is { } tile)
            {
                towers.TryBuild(tile.X, tile.Z);
            }
        }

        waves.Update(time);
        enemies.Update(time);
        towers.Update(time);
        shots.Update(time);

        if (player.Lives <= 0)
        {
            screen.Set(Screen.Lost);
        }
        else if (waves.AllSpawned && enemies.All.Count == 0)
        {
            screen.Set(Screen.Won);
        }
    }

    /// <summary>Everything back to how a game starts. Runs each time the game enters Playing.</summary>
    private void NewGame()
    {
        player.Reset();
        waves.Reset();
        enemies.Clear();
        towers.Clear();
        shots.Clear();
    }

    public void Draw(View view)
    {
        view.Camera(Camera);
        scenery.Draw(view);

        for (int z = 0; z < Field.Depth; z++)
        {
            for (int x = 0; x < Field.Width; x++)
            {
                var tile = Matrix4x4.CreateScale(1.96f, 0.2f, 1.96f)
                    * Matrix4x4.CreateTranslation(field.Center(x, z) - new Vector3(0, 0.1f, 0));
                view.Model(10_000 + z * Field.Width + x, look.Tile, tile, field.IsRoad(x, z) ? Sand : Grass);
            }
        }

        var baseBox = Matrix4x4.CreateScale(1.6f) * Matrix4x4.CreateTranslation(field.BaseCenter + new Vector3(0, 0.8f, 0));
        var baseRoof = Matrix4x4.CreateScale(2.1f, 1.2f, 2.1f) * Matrix4x4.CreateTranslation(field.BaseCenter + new Vector3(0, 2.2f, 0));
        view.Model(1, look.Cube, baseBox, Stone);
        view.Model(3, look.Cone, baseRoof, BaseRoof);

        towers.Draw(view);
        enemies.Draw(view);
        shots.Draw(view);
    }
}
