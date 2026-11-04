using System.Reflection;
using System.Reflection.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Super.Testing;
using Super.Ui;

namespace Super.Tests;

public class HotReloadTests
{
    public enum Screen { Menu, House }

    /// <summary>A game whose Update, Draw and Ui can be broken on demand, as a bad edit would.</summary>
    private class Fragile(States<Screen> screens, Input input) : IGame
    {
        public bool BreakUpdate { get; set; }
        public bool BreakDraw { get; set; }
        public bool BreakUi { get; set; }
        public bool ReadsInputInDraw { get; set; }
        public Screen? SetOnUpdate { get; set; }
        public int Updates { get; private set; }
        public int Draws { get; private set; }
        public List<string> Log { get; } = [];

        public void Update(GameTime time)
        {
            if (BreakUpdate)
            {
                throw new InvalidOperationException("Update broke");
            }

            Updates++;
            Log.Add("update");
            if (SetOnUpdate is { } next)
            {
                screens.Set(next);
            }
        }

        public void Draw(View view)
        {
            if (BreakDraw)
            {
                throw new InvalidOperationException("Draw broke");
            }

            if (ReadsInputInDraw)
            {
                _ = input.Down(Key.W);
            }

            Draws++;
        }

        public UiView? Ui => BreakUi ? throw new InvalidOperationException("Ui broke") : new Text("game ui");

        public void CodeReloaded() => Log.Add("reloaded");
    }

    private static GameHost<Fragile> Host(StringWriter errors, Action<IServiceCollection>? services = null) =>
        new(1, 30, services) { KeepsRunningAfterErrors = true, Errors = errors };

    private static List<string> Shown<TGame>(GameHost<TGame> host)
        where TGame : class, IGame =>
        host.Ui.ShownText.Select(text => text.Text).ToList();

    [Fact]
    public void Without_hot_reload_an_error_in_Update_closes_the_game()
    {
        using var host = new GameHost<Fragile>(1, 30, null);
        host.Game.BreakUpdate = true;

        var error = Assert.Throws<InvalidOperationException>(host.Tick);
        Assert.Equal("Update broke", error.Message);
    }

    [Fact]
    public void An_error_in_Update_stops_the_game_instead_of_closing_it()
    {
        using var errors = new StringWriter();
        using var host = Host(errors);
        host.Tick();
        host.Game.BreakUpdate = true;

        host.Tick();
        host.Game.BreakUpdate = false;
        host.Tick();
        host.Tick();

        Assert.Equal(1, host.Game.Updates);
        Assert.Equal(1, host.Time.Tick);
        Assert.IsType<InvalidOperationException>(host.Stopped);
    }

    [Fact]
    public void A_stop_is_reported_once_with_what_to_do()
    {
        using var errors = new StringWriter();
        using var host = Host(errors);
        host.Game.BreakUpdate = true;

        host.Tick();
        host.Draw(1f);
        host.Tick();
        host.Draw(1f);

        string text = errors.ToString();
        Assert.Equal(1, CountOf(text, "The game stopped"));
        Assert.Contains("System.InvalidOperationException: Update broke", text);
        Assert.Contains("Fix it and save. The game carries on once the change is applied.", text);
    }

    [Fact]
    public void A_second_error_while_stopped_keeps_the_first_and_is_not_reported()
    {
        using var errors = new StringWriter();
        using var host = Host(errors);
        host.Game.BreakUpdate = true;
        host.Game.BreakDraw = true;

        host.Tick();
        host.Draw(1f);

        Assert.Equal("Update broke", host.Stopped?.Message);
        Assert.Equal(1, CountOf(errors.ToString(), "The game stopped"));
        Assert.DoesNotContain("Draw broke", errors.ToString());
        Assert.Contains("InvalidOperationException: Update broke", Shown(host));
    }

    [Fact]
    public void Input_cannot_be_read_in_Draw_after_Update_broke_part_way()
    {
        using var errors = new StringWriter();
        using var host = Host(errors);
        host.Game.BreakUpdate = true;
        host.Game.ReadsInputInDraw = true;

        host.Tick();
        host.Draw(1f);

        Assert.Equal(0, host.Game.Draws);
        Assert.Equal("Update broke", host.Stopped?.Message);
    }

    [Fact]
    public void The_screen_shows_the_error_in_place_of_the_game_interface()
    {
        using var errors = new StringWriter();
        using var host = Host(errors);
        host.Game.BreakUpdate = true;

        host.Tick();
        host.Draw(1f);

        var shown = Shown(host);
        Assert.Contains("The game stopped", shown);
        Assert.Contains("InvalidOperationException: Update broke", shown);
        Assert.Contains("Fix it and save. The game carries on once the change is applied.", shown);
        Assert.DoesNotContain("game ui", shown);
    }

    [Fact]
    public void The_world_is_still_drawn_while_stopped_by_Update()
    {
        using var errors = new StringWriter();
        using var host = Host(errors);
        host.Game.BreakUpdate = true;
        host.Tick();
        int before = host.Game.Draws;

        host.Draw(1f);

        Assert.Equal(before + 1, host.Game.Draws);
    }

    [Fact]
    public void A_code_change_carries_on_from_where_it_stopped()
    {
        using var errors = new StringWriter();
        using var host = Host(errors);
        host.Tick();
        host.Game.BreakUpdate = true;
        host.Tick();

        host.Game.BreakUpdate = false;
        host.CodeChanged();
        host.Tick();
        host.Draw(1f);

        Assert.Null(host.Stopped);
        Assert.Equal(2, host.Game.Updates);
        Assert.Equal(2, host.Time.Tick);
        Assert.Contains("game ui", Shown(host));
    }

    [Fact]
    public void A_content_change_tries_again_too()
    {
        var files = new MemoryContent();
        using var errors = new StringWriter();
        using var host = Host(errors, services => services.AddContentFiles(files));
        host.Game.BreakUpdate = true;
        host.Tick();

        host.Game.BreakUpdate = false;
        host.Content.ReloadWanted = true;
        host.Tick();

        Assert.Null(host.Stopped);
        Assert.Equal(1, host.Game.Updates);
    }

    [Fact]
    public void A_change_that_still_breaks_stops_again_and_says_so()
    {
        using var errors = new StringWriter();
        using var host = Host(errors);
        host.Game.BreakUpdate = true;
        host.Tick();

        host.CodeChanged();
        host.Tick();

        Assert.NotNull(host.Stopped);
        Assert.Equal(0, host.Game.Updates);
        Assert.Equal(2, CountOf(errors.ToString(), "The game stopped"));
    }

    [Fact]
    public void An_error_in_Draw_stops_the_game_and_shows_the_error()
    {
        using var errors = new StringWriter();
        using var host = Host(errors);
        host.Game.BreakDraw = true;

        host.Draw(1f);
        host.Tick();
        host.Draw(1f);

        Assert.Equal(0, host.Game.Updates);
        Assert.Contains("InvalidOperationException: Draw broke", Shown(host));
        Assert.Equal(1, CountOf(errors.ToString(), "The game stopped"));
    }

    [Fact]
    public void An_error_in_Ui_stops_the_game_and_shows_the_error()
    {
        using var errors = new StringWriter();
        using var host = Host(errors);
        host.Game.BreakUi = true;

        host.Draw(1f);
        host.Tick();

        Assert.Equal(0, host.Game.Updates);
        Assert.Contains("InvalidOperationException: Ui broke", Shown(host));
    }

    [Fact]
    public void A_broken_Draw_is_not_called_again_until_a_change()
    {
        using var errors = new StringWriter();
        using var host = Host(errors);
        host.Game.BreakDraw = true;
        host.Draw(1f);
        host.Game.BreakDraw = false;

        host.Draw(1f);
        Assert.Equal(0, host.Game.Draws);

        host.CodeChanged();
        host.Tick();
        host.Draw(1f);
        Assert.Equal(1, host.Game.Draws);
    }

    [Fact]
    public void States_can_change_after_a_Draw_broke_part_way()
    {
        using var errors = new StringWriter();
        using var host = Host(errors);
        host.Game.BreakDraw = true;
        host.Draw(1f);

        host.Game.BreakDraw = false;
        host.Game.SetOnUpdate = Screen.House;
        host.CodeChanged();
        host.Tick();

        Assert.Null(host.Stopped);
        Assert.True(host.GetService(typeof(States<Screen>)) is States<Screen> screens && screens.Is(Screen.House));
    }

    [Fact]
    public void CodeReloaded_runs_once_on_the_next_tick_before_Update()
    {
        using var run = HeadlessGame.Create<Fragile>(1);
        run.Ticks(1);

        run.CodeChanged();
        Assert.Equal(["update"], run.Game.Log);
        run.Ticks(2);

        Assert.Equal(["update", "reloaded", "update", "update"], run.Game.Log);
    }

    private static int CountOf(string text, string part)
    {
        int count = 0;
        for (int at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}

/// <summary>The runtime finds the handler by name and calls it from outside the game, so this bumps process-wide state.</summary>
[Collection(Name)]
[Trait("Speed", "Slow")]
public class HotReloadHandlerTests
{
    public const string Name = "Hot reload handler";

    private class Listener : IGame
    {
        public int Reloads { get; private set; }

        public void Update(GameTime time)
        {
        }

        public void Draw(View view)
        {
        }

        public void CodeReloaded() => Reloads++;
    }

    [Fact]
    public void The_runtime_handler_tells_running_games_on_their_next_tick()
    {
        var handler = typeof(Game).Assembly.GetCustomAttributes<MetadataUpdateHandlerAttribute>().Single().HandlerType;
        var update = handler.GetMethod("UpdateApplication", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, [typeof(Type[])]);
        Assert.NotNull(update);
        using var run = HeadlessGame.Create<Listener>(1);
        run.Ticks(1);

        update.Invoke(null, [null]);
        Assert.Equal(0, run.Game.Reloads);
        run.Ticks(2);

        Assert.Equal(1, run.Game.Reloads);
    }
}

[CollectionDefinition(HotReloadHandlerTests.Name, DisableParallelization = true)]
public class HotReloadHandlerCollection;
