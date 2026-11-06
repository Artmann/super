namespace Super.Tests;

public class GameArgumentsTests
{
    [Fact]
    public void Nothing_means_nothing_was_asked_for()
    {
        Assert.Equal(new GameArguments(null, null, null), GameArguments.Parse([]));
    }

    [Fact]
    public void Reads_seed_ticks_and_screenshot()
    {
        var arguments = GameArguments.Parse(["--seed", "42", "--ticks", "6000", "--screenshot", "day.png"]);

        Assert.Equal(new GameArguments(42, 6000, "day.png"), arguments);
    }

    [Fact]
    public void Reads_a_script()
    {
        Assert.Equal("craft", GameArguments.Parse(["--script", "craft"]).ScriptName);
    }

    [Fact]
    public void A_script_needs_a_name()
    {
        Assert.Throws<ArgumentException>(() => GameArguments.Parse(["--script"]));
    }

    [Fact]
    public void Reads_a_user_data_folder()
    {
        Assert.Equal("saves/test", GameArguments.Parse(["--user-data", "saves/test", "--screenshot", "a.png"]).UserDataFolder);
    }

    [Fact]
    public void A_user_data_folder_needs_a_path()
    {
        var error = Assert.Throws<ArgumentException>(() => GameArguments.Parse(["--user-data"]));

        Assert.Equal("--user-data needs a value.", error.Message);
    }

    [Fact]
    public void Reads_a_recording()
    {
        var arguments = GameArguments.Parse(
            ["--seed", "42", "--ticks", "60", "--record", "clip.mp4", "--seconds", "2.5", "--fps", "30", "--size", "640x360"]);

        Assert.Equal(new GameArguments(42, 60, null, "clip.mp4", 2.5, 30, 640, 360), arguments);
    }

    [Fact]
    public void Recordings_default_to_60_frames_a_second()
    {
        Assert.Equal(60, GameArguments.Parse(["--record", "clip.mp4", "--seconds", "1"]).FramesPerSecond);
    }

    [Fact]
    public void Reads_no_vsync()
    {
        Assert.True(GameArguments.Parse(["--no-vsync"]).NoVsync);
        Assert.False(GameArguments.Parse([]).NoVsync);
    }

    [Fact]
    public void Reads_no_materials_in_a_window_and_with_a_screenshot()
    {
        Assert.True(GameArguments.Parse(["--no-materials"]).NoMaterials);
        Assert.True(GameArguments.Parse(["--no-materials", "--screenshot", "day.png"]).NoMaterials);
        Assert.False(GameArguments.Parse(["--screenshot", "day.png"]).NoMaterials);
    }

    [Fact]
    public void No_vsync_is_only_for_playing_in_a_window()
    {
        Assert.Throws<ArgumentException>(() => GameArguments.Parse(["--no-vsync", "--profile", "reef.txt"]));
        Assert.Throws<ArgumentException>(() => GameArguments.Parse(["--no-vsync", "--screenshot", "day.png"]));
        Assert.Throws<ArgumentException>(() => GameArguments.Parse(["--no-vsync", "--record", "clip.mp4", "--seconds", "1"]));
    }

    [Fact]
    public void Reads_a_profile()
    {
        var arguments = GameArguments.Parse(["--seed", "7", "--ticks", "300", "--profile", "reef.txt", "--frames", "120"]);

        Assert.Equal(new GameArguments(7, 300, null, ProfilePath: "reef.txt", Frames: 120), arguments);
    }

    [Fact]
    public void Profiles_default_to_60_frames()
    {
        Assert.Equal(60, GameArguments.Parse(["--profile", "reef.txt"]).Frames);
    }

    [Fact]
    public void Leaves_the_games_own_options_alone()
    {
        Assert.Equal(new GameArguments(42, null, null), GameArguments.Parse(["--god-mode", "--seed", "42"]));
    }

    [Theory]
    [InlineData("--seed")]
    [InlineData("--seed", "soon")]
    [InlineData("--seed", "99999999999")]
    [InlineData("--ticks", "many", "--screenshot", "a.png")]
    [InlineData("--ticks", "-1", "--screenshot", "a.png")]
    [InlineData("--ticks", "10")]
    [InlineData("--screenshot")]
    [InlineData("--record", "clip.mp4")]
    [InlineData("--record", "clip.avi", "--seconds", "1")]
    [InlineData("--record", "clip.mp4", "--seconds", "0")]
    [InlineData("--record", "clip.mp4", "--seconds", "soon")]
    [InlineData("--record", "clip.mp4", "--seconds", "1", "--fps", "0")]
    [InlineData("--record", "clip.mp4", "--seconds", "1", "--fps", "99999999999")]
    [InlineData("--record", "clip.mp4", "--seconds", "1", "--screenshot", "a.png")]
    [InlineData("--seconds", "1")]
    [InlineData("--frames", "60")]
    [InlineData("--profile", "reef.txt", "--frames", "0")]
    [InlineData("--profile", "reef.txt", "--frames", "99999999999")]
    [InlineData("--profile", "reef.txt", "--screenshot", "a.png")]
    [InlineData("--profile", "reef.txt", "--record", "clip.mp4", "--seconds", "1")]
    [InlineData("--size", "big", "--screenshot", "a.png")]
    [InlineData("--size", "0x10", "--screenshot", "a.png")]
    public void Refuses_a_bad_command_line(params string[] args)
    {
        Assert.Throws<ArgumentException>(() => GameArguments.Parse(args));
    }

    [Fact]
    public void Reads_an_interface_scale_with_a_point_whatever_the_language()
    {
        var original = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("sv-SE");
        try
        {
            Assert.Equal(1.5f, GameArguments.Parse(["--ui-scale", "1.5"]).UiScale);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void No_interface_scale_leaves_it_to_the_game()
    {
        Assert.Null(GameArguments.Parse([]).UiScale);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-2")]
    [InlineData("big")]
    [InlineData("Infinity")]
    public void An_interface_scale_must_be_a_number_above_zero(string scale)
    {
        var error = Assert.Throws<ArgumentException>(() => GameArguments.Parse(["--ui-scale", scale]));

        Assert.Equal($"--ui-scale needs a number above zero, such as 2 or 1.5, not '{scale}'.", error.Message);
    }
}
