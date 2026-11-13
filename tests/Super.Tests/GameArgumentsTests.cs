using Rendition;

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
    public void Reads_no_trace()
    {
        Assert.True(GameArguments.Parse(["--no-trace"]).NoTrace);
        Assert.False(GameArguments.Parse([]).NoTrace);
    }

    [Fact]
    public void Reads_the_trace_debug_view()
    {
        Assert.Equal(Rendition.DebugView.Trace, GameArguments.Parse(["--debug-view", "trace"]).DebugView);
        Assert.Equal(Rendition.DebugView.None, GameArguments.Parse([]).DebugView);
    }

    [Fact]
    public void Debug_view_probes_is_accepted()
    {
        Assert.Equal(Rendition.DebugView.Probes, GameArguments.Parse(["--debug-view", "probes"]).DebugView);
    }

    [Fact]
    public void An_unknown_debug_view_names_both_choices()
    {
        var error = Assert.Throws<ArgumentException>(() => GameArguments.Parse(["--debug-view", "nope"]));
        Assert.Equal("--debug-view doesn't know 'nope'. It knows: trace, probes.", error.Message);
    }

    [Fact]
    public void The_probe_view_needs_no_tracing()
    {
        Assert.Null(GameArguments.DebugViewError(Rendition.DebugView.Probes, canTrace: false, "turned off with --no-trace"));
    }

    [Fact]
    public void Needs_a_name_after_debug_view()
    {
        var error = Assert.Throws<ArgumentException>(() => GameArguments.Parse(["--debug-view"]));
        Assert.Equal("--debug-view needs a value.", error.Message);
    }

    [Fact]
    public void The_trace_view_on_a_GPU_that_cant_trace_names_the_reason()
    {
        Assert.Equal(
            "--debug-view trace needs a GPU that can trace rays, and this one can't (turned off with --no-trace).",
            GameArguments.DebugViewError(Rendition.DebugView.Trace, canTrace: false, "turned off with --no-trace"));
    }

    [Fact]
    public void The_trace_view_on_a_GPU_that_can_trace_is_no_error()
    {
        Assert.Null(GameArguments.DebugViewError(Rendition.DebugView.Trace, canTrace: true, null));
    }

    [Fact]
    public void No_debug_view_is_no_error_even_without_tracing()
    {
        Assert.Null(GameArguments.DebugViewError(Rendition.DebugView.None, canTrace: false, "no VK_KHR_ray_query"));
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
    public void Render_scale_is_parsed_from_the_command_line()
    {
        var original = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("sv-SE");
        try
        {
            Assert.Equal(1.5f, GameArguments.Parse(["--render-scale", "1.5", "--screenshot", "a.png"]).RenderScale);
            Assert.Equal(0.5f, GameArguments.Parse(["--render-scale", "0.5"]).RenderScale);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void No_render_scale_leaves_it_to_the_game()
    {
        Assert.Null(GameArguments.Parse([]).RenderScale);
    }

    [Theory]
    [InlineData("0.4")]
    [InlineData("3")]
    [InlineData("1,5")]
    [InlineData("big")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void A_render_scale_must_be_from_half_to_two(string scale)
    {
        var error = Assert.Throws<ArgumentException>(() => GameArguments.Parse(["--render-scale", scale]));

        Assert.Equal($"--render-scale needs a number from 0.5 to 2, such as 1.5, not '{scale}'.", error.Message);
    }

    [Fact]
    public void Upscaler_is_parsed_from_the_command_line()
    {
        Assert.Equal(Upscaler.Fsr, GameArguments.Parse(["--upscaler", "fsr", "--screenshot", "a.png"]).Upscaler);
        Assert.Equal(Upscaler.Taa, GameArguments.Parse(["--upscaler", "taa"]).Upscaler);
        Assert.Null(GameArguments.Parse([]).Upscaler);
    }

    [Theory]
    [InlineData("dlss")]
    [InlineData("FSR")]
    [InlineData("")]
    public void An_upscaler_must_be_taa_or_fsr(string name)
    {
        var error = Assert.Throws<ArgumentException>(() => GameArguments.Parse(["--upscaler", name]));

        Assert.Equal($"--upscaler needs taa or fsr, not '{name}'.", error.Message);
    }

    [Fact]
    public void An_upscaler_needs_a_value()
    {
        var error = Assert.Throws<ArgumentException>(() => GameArguments.Parse(["--upscaler"]));

        Assert.Equal("--upscaler needs a value.", error.Message);
    }

    [Fact]
    public void A_render_scale_needs_a_value()
    {
        var error = Assert.Throws<ArgumentException>(() => GameArguments.Parse(["--render-scale"]));

        Assert.Equal("--render-scale needs a value.", error.Message);
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

    [Fact]
    public void Bake_is_parsed()
    {
        var arguments = GameArguments.Parse(["--bake", "--ticks", "30", "--seed", "1"]);

        Assert.True(arguments.Bake);
        Assert.False(arguments.BakeCheck);
        Assert.Equal(30, arguments.Ticks);
        Assert.Equal(1, arguments.Seed);
        Assert.Equal(BakeQuality.Final, arguments.BakeQuality);
        Assert.False(GameArguments.Parse([]).Bake);
    }

    [Fact]
    public void Bake_check_is_parsed_with_ticks()
    {
        var arguments = GameArguments.Parse(["--bake-check", "--ticks", "30"]);

        Assert.True(arguments.BakeCheck);
        Assert.False(arguments.Bake);
        Assert.Equal(30, arguments.Ticks);
        Assert.False(GameArguments.Parse([]).BakeCheck);
    }

    [Theory]
    [InlineData("preview", BakeQuality.Preview)]
    [InlineData("final", BakeQuality.Final)]
    public void Bake_quality_reads_preview_and_final(string name, BakeQuality expected)
    {
        Assert.Equal(expected, GameArguments.Parse(["--bake", "--bake-quality", name]).BakeQuality);
    }

    [Fact]
    public void An_unknown_bake_quality_throws()
    {
        var error = Assert.Throws<ArgumentException>(() => GameArguments.Parse(["--bake", "--bake-quality", "draft"]));

        Assert.Equal("--bake-quality needs preview or final, not 'draft'.", error.Message);
    }

    [Fact]
    public void Bake_with_screenshot_throws()
    {
        var error = Assert.Throws<ArgumentException>(() => GameArguments.Parse(["--bake", "--screenshot", "day.png"]));

        Assert.Equal("--bake can't run with --screenshot.", error.Message);
    }

    [Theory]
    [InlineData("--bake", "--record", "clip.mp4", "--seconds", "1")]
    [InlineData("--bake", "--profile", "reef.txt")]
    [InlineData("--bake", "--bake-check")]
    [InlineData("--bake-check", "--screenshot", "day.png")]
    public void Bake_or_bake_check_with_another_run_names_both(params string[] args)
    {
        var error = Assert.Throws<ArgumentException>(() => GameArguments.Parse(args));

        Assert.Equal($"{args[0]} can't run with {args[1]}.", error.Message);
    }

    [Theory]
    [InlineData(null, 60, 120)]
    [InlineData(null, 30, 60)]
    [InlineData(5L, 60, 5)]
    public void Bake_plays_two_seconds_unless_ticks_are_given(long? ticks, int ticksPerSecond, long played)
    {
        Assert.Equal(played, Game.BakeTicks(ticks, ticksPerSecond));
    }

    [Fact]
    public void A_bake_that_cannot_be_made_prints_its_message_on_one_line_instead_of_throwing()
    {
        var error = new StringWriter();

        bool made = Game.TryBake(() => throw new InvalidOperationException("--bake found nothing."), error);

        Assert.False(made);
        Assert.Equal("--bake found nothing." + Environment.NewLine, error.ToString());
    }

    [Fact]
    public void A_bake_that_is_made_prints_nothing()
    {
        var error = new StringWriter();
        int ran = 0;

        bool made = Game.TryBake(() => ran++, error);

        Assert.True(made);
        Assert.Equal(1, ran);
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public void A_bake_that_fails_another_way_is_not_hidden()
    {
        var error = Assert.Throws<IOException>(() => Game.TryBake(() => throw new IOException("disk full"), new StringWriter()));

        Assert.Equal("disk full", error.Message);
    }

    [Fact]
    public void Bake_quality_without_bake_throws()
    {
        var error = Assert.Throws<ArgumentException>(() => GameArguments.Parse(["--bake-quality", "preview"]));

        Assert.Equal("--bake-quality needs --bake.", error.Message);
    }

    [Theory]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    [InlineData("ultra")]
    [InlineData("fastest")]
    [InlineData("auto")]
    [InlineData("profiles/mine.json")]
    public void Reads_a_graphics_profile(string name)
    {
        Assert.Equal(name, GameArguments.Parse(["--graphics", name]).GraphicsProfile);
    }

    [Fact]
    public void A_graphics_profile_it_does_not_know_is_refused()
    {
        var error = Assert.Throws<ArgumentException>(() => GameArguments.Parse(["--graphics", "potato"]));

        Assert.Equal("--graphics needs low, medium, high, ultra, fastest, auto or a profile ending in .json, not 'potato'.", error.Message);
    }

    [Fact]
    public void Flags_win_over_the_graphics_profile()
    {
        var graphics = new Graphics();
        var arguments = GameArguments.Parse(["--graphics", "low", "--render-scale", "1", "--no-vsync"]);

        Game.ApplyGraphicsArguments(graphics, arguments, gpu: null);

        Assert.Equal(1024, graphics.ShadowResolution);
        Assert.False(graphics.Reflections);
        Assert.Equal(1f, graphics.RenderScale);
        Assert.False(graphics.Vsync);
    }

    [Fact]
    public void Auto_graphics_waits_for_the_gpu()
    {
        var graphics = new Graphics();
        var arguments = GameArguments.Parse(["--graphics", "auto", "--no-materials"]);

        Game.ApplyGraphicsArguments(graphics, arguments, gpu: null);

        Assert.Equal(2048, graphics.ShadowResolution);
        Assert.True(graphics.Reflections);
        Assert.False(graphics.Materials);
    }

    [Fact]
    public void Auto_graphics_picks_low_for_a_gpu_built_into_the_processor()
    {
        var graphics = new Graphics();
        var arguments = GameArguments.Parse(["--graphics", "auto"]);

        Game.ApplyGraphicsArguments(graphics, arguments, new GpuInfo("Laptop GPU", GpuKind.Integrated, 1L << 30, false));

        Assert.Equal(0.75f, graphics.RenderScale);
        Assert.Equal(1024, graphics.ShadowResolution);
        Assert.False(graphics.Reflections);
    }

    [Fact]
    public void A_graphics_profile_file_is_read()
    {
        string folder = Directory.CreateTempSubdirectory("super-arguments-").FullName;
        try
        {
            string path = Path.Combine(folder, "mine.json");
            File.WriteAllText(path, """{ "Bloom": false, "ShadowCascades": 1 }""");
            var graphics = new Graphics();

            Game.ApplyGraphicsArguments(graphics, GameArguments.Parse(["--graphics", path]), gpu: null);

            Assert.False(graphics.Bloom);
            Assert.Equal(1, graphics.ShadowCascades);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_missing_graphics_profile_file_is_named()
    {
        var arguments = GameArguments.Parse(["--graphics", "nowhere/mine.json"]);

        var error = Assert.Throws<ArgumentException>(() => Game.ApplyGraphicsArguments(new Graphics(), arguments, gpu: null));

        Assert.Equal("--graphics can't find the profile 'nowhere/mine.json'.", error.Message);
    }
}
