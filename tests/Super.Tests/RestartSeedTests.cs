namespace Super.Tests;

public sealed class RestartSeedTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "super-restart-seed-" + Guid.NewGuid().ToString("N"));

    public RestartSeedTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string SeedFile => Path.Combine(_folder, RestartSeed.FileName);

    [Fact]
    public void Outside_dotnet_watch_every_run_gets_a_fresh_seed_and_nothing_is_written()
    {
        var first = RestartSeed.Pick(watchIteration: null, _folder, () => 11);
        var second = RestartSeed.Pick(watchIteration: null, _folder, () => 22);

        Assert.Equal(new RestartSeed.Choice(11, Kept: false), first);
        Assert.Equal(new RestartSeed.Choice(22, Kept: false), second);
        Assert.False(File.Exists(SeedFile));
    }

    [Fact]
    public void A_restart_under_dotnet_watch_keeps_the_seed_of_the_first_run()
    {
        var first = RestartSeed.Pick("1", _folder, () => 11);
        var restart = RestartSeed.Pick("2", _folder, () => 22);
        var another = RestartSeed.Pick("3", _folder, () => 33);

        Assert.Equal(new RestartSeed.Choice(11, Kept: false), first);
        Assert.Equal(new RestartSeed.Choice(11, Kept: true), restart);
        Assert.Equal(new RestartSeed.Choice(11, Kept: true), another);
    }

    [Fact]
    public void A_new_dotnet_watch_session_picks_a_new_seed()
    {
        RestartSeed.Pick("1", _folder, () => 11);
        RestartSeed.Pick("2", _folder, () => 22);

        var newSession = RestartSeed.Pick("1", _folder, () => 44);
        var itsRestart = RestartSeed.Pick("2", _folder, () => 55);

        Assert.Equal(new RestartSeed.Choice(44, Kept: false), newSession);
        Assert.Equal(new RestartSeed.Choice(44, Kept: true), itsRestart);
    }

    [Fact]
    public void A_restart_with_no_saved_seed_picks_one_and_keeps_it_for_the_next()
    {
        var restart = RestartSeed.Pick("2", _folder, () => 22);
        var next = RestartSeed.Pick("3", _folder, () => 33);

        Assert.Equal(new RestartSeed.Choice(22, Kept: false), restart);
        Assert.Equal(new RestartSeed.Choice(22, Kept: true), next);
    }

    [Fact]
    public void A_damaged_seed_file_is_replaced_with_a_fresh_seed()
    {
        File.WriteAllText(SeedFile, "not a number");

        var restart = RestartSeed.Pick("2", _folder, () => 22);

        Assert.Equal(new RestartSeed.Choice(22, Kept: false), restart);
        Assert.Equal("22", File.ReadAllText(SeedFile));
    }

    [Fact]
    public void A_negative_seed_survives_the_round_trip_under_a_comma_culture()
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("sv-SE");
        try
        {
            RestartSeed.Pick("1", _folder, () => -1234567);
            var restart = RestartSeed.Pick("2", _folder, () => 22);

            Assert.Equal(new RestartSeed.Choice(-1234567, Kept: true), restart);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = culture;
        }
    }
}
