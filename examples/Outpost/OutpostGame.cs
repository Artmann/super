using System.Numerics;
using Super;
using Super.Ui;

namespace OutpostExample;

public sealed class OutpostGame(Scenery scenery, Field field, Player player, Enemies enemies, Towers towers, Waves waves, Scenes scenes, Systems systems, States<Screen> screen, Actions<Control> actions, Input input, Look look) : IGame
{
    // Each tick the waves send enemies out, the enemies walk, the towers shoot and the shots fly, in that order.
    private readonly Systems _systems = systems.Add<Waves>().Add<Enemies>().Add<Towers>().Add<Shots>();

    public Camera Camera => scenery.Camera;

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
        ["start"] = new InputScript().Press(Control.Start),
        // Starts, then builds a tower on the middle tile and one by the first bend: --script towers
        ["towers"] = new InputScript().Press(Control.Start).Wait(1).Click(new Vector2(640, 360)).Wait(1).Click(new Vector2(411, 261)).Wait(5),
    };

    public void Load()
    {
        look.Load();

        actions.Bind(Control.Build, MouseButton.Left);
        actions.Bind(Control.Start, Key.Space, MouseButton.Left);
        actions.Bind(Control.Restart, Key.R);

        scenes.Go(scenery.Build);
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

        _systems.Update(time);

        if (player.Lives <= 0)
        {
            screen.Set(Screen.Lost);
        }
        else if (waves.AllSpawned && enemies.Count == 0)
        {
            screen.Set(Screen.Won);
        }
    }

    /// <summary>
    /// Everything back to how a game starts. Runs each time the game enters Playing. Building the scene again takes
    /// away every enemy, tower and shot, since they were all spawned into it.
    /// </summary>
    private void NewGame()
    {
        player.Reset();
        waves.Reset();
        scenes.Rebuild();
    }
}
