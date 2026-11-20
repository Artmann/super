using System.Numerics;
using Super;
using Super.Physics;
using Super.Ui;

namespace SkylarkExample;

public sealed class SkylarkGame(Levels levels, Scenery scenery, Journey journey, Gems gems, Scenes scenes, Systems systems, States<Screen> screen, Actions<Control> actions, Input input, UserData saved, World world, Look look) : IGame
{
    // Each tick the lifts glide, the bird walks, the gems are collected and fallen crates go home, in that order.
    private readonly Systems _systems = systems.Add<Lifts>().Add<Bird>().Add<Gems>().Add<Crates>();

    public UiView? Ui => screen.Current switch
    {
        Screen.Title => new Banner("Skylark", "Press Space to start"),
        Screen.Won => new Banner(
            $"You made it in {Clock.Show(journey.Seconds)}",
            $"Best {Clock.Show(saved.Get<Records>().BestSeconds)}. Press R to play again"),
        _ => new Hud(levels.Current.Name, gems.Found, gems.Total, journey.Seconds),
    };

    public IReadOnlyDictionary<string, InputScript> Scripts => new Dictionary<string, InputScript>
    {
        // Starts the game, for screenshots: --script start
        ["start"] = new InputScript().Press(Control.Start),
        // Starts, walks to the edge and hops to the next island: --script hop
        ["hop"] = new InputScript()
            .Press(Control.Start)
            .Wait(2)
            .Hold(Control.Move, new Vector2(0f, 1f))
            .Wait(22)
            .Hold(Control.Jump)
            .Wait(20)
            .Release(Control.Jump)
            .Release(Control.Move),
        // Starts and walks into the first gem, which is to the left and ahead: --script gem
        ["gem"] = new InputScript()
            .Press(Control.Start)
            .Wait(11)
            .Hold(Control.Move, Vector2.Normalize(new Vector2(-2.5f, 3.5f)))
            .Wait(30)
            .Release(Control.Move),
    };

    public void Load()
    {
        look.Load();

        actions.Bind(Control.Move, Keys.WASD, Keys.Arrows, GamepadStick.Left);
        actions.Bind(Control.Look, Binding.MouseDelta.Scaled(0.005f), GamepadStick.Right);
        actions.Bind(Control.Jump, Key.Space, GamepadButton.South);
        actions.Bind(Control.Start, Key.Space, GamepadButton.South);
        actions.Bind(Control.Restart, Key.R, GamepadButton.North);

        // A gem is an area that notices who walks in, so every gem gets a Trigger without asking.
        world.Require<Gem, Trigger>(new Trigger());

        scenes.Go(scenery.Build);
        screen.OnEnter(Screen.Playing, NewRun);
        screen.OnExit(Screen.Playing, () => input.Mouse.Locked = false);
    }

    public void Update(GameTime time)
    {
        switch (screen.Current)
        {
            case Screen.Title when actions.Pressed(Control.Start):
            case Screen.Won when actions.Pressed(Control.Restart):
                screen.Set(Screen.Playing);
                break;
            case Screen.Playing when actions.Pressed(Control.Restart):
                screen.Reenter();
                break;
            case Screen.Playing when !scenes.IsChanging:
                Play(time);
                break;
        }
    }

    private void Play(GameTime time)
    {
        journey.Seconds += time.DeltaSeconds;
        _systems.Update(time);

        if (!world.Only<Goal>().Reached)
        {
            return;
        }

        if (journey.Level + 1 < Levels.All.Count)
        {
            // The same build, for the next level, behind a fade. Nothing moves until it's in.
            journey.Level++;
            scenes.Rebuild(Transition.Fade(0.6f));
            return;
        }

        var records = saved.Get<Records>();
        if (records.BestSeconds <= 0f || journey.Seconds < records.BestSeconds)
        {
            records.BestSeconds = journey.Seconds;
            saved.Save<Records>();
        }

        screen.Set(Screen.Won);
    }

    /// <summary>A fresh run from the first level. Runs each time the game enters Playing.</summary>
    private void NewRun()
    {
        journey.Reset();
        scenes.Rebuild();
        input.Mouse.Locked = true;
    }
}
