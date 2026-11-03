using Super.Testing;

namespace Super.Tests;

public sealed class UserDataTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "user-data-tests-" + Guid.NewGuid().ToString("N"));

    public class Progress
    {
        public bool SeenIntro;
        public int ChaptersUnlocked { get; set; } = 1;
        public string Name { get; set; } = "Ada";
    }

    public class Exploding
    {
        public int Before { get; set; } = 1;
        public bool Boom { get; set; }

        public int After => Boom ? throw new InvalidOperationException("boom") : 2;
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void A_saved_class_reads_back_in_a_later_run()
    {
        var first = new UserData(_folder);
        var progress = first.Get<Progress>();
        progress.SeenIntro = true;
        progress.ChaptersUnlocked = 4;
        progress.Name = "Grace";
        first.Save<Progress>();

        var read = new UserData(_folder).Get<Progress>();

        Assert.True(read.SeenIntro);
        Assert.Equal(4, read.ChaptersUnlocked);
        Assert.Equal("Grace", read.Name);
    }

    [Fact]
    public void Each_class_is_kept_in_its_own_json_file_named_after_it()
    {
        var data = new UserData(_folder);
        data.Get<Progress>().ChaptersUnlocked = 3;
        data.Save<Progress>();

        string text = File.ReadAllText(Path.Combine(_folder, "Progress.json"));

        Assert.Contains("\"ChaptersUnlocked\": 3", text);
        Assert.Contains("\"SeenIntro\": false", text);
    }

    [Fact]
    public void With_no_file_the_class_starts_from_its_own_defaults_and_nothing_is_written()
    {
        var progress = new UserData(_folder).Get<Progress>();

        Assert.False(progress.SeenIntro);
        Assert.Equal(1, progress.ChaptersUnlocked);
        Assert.False(Directory.Exists(_folder));
    }

    [Fact]
    public void Get_hands_out_the_same_copy_each_time_so_changes_are_shared()
    {
        var data = new UserData(_folder);
        data.Get<Progress>().ChaptersUnlocked = 7;

        Assert.Equal(7, data.Get<Progress>().ChaptersUnlocked);
    }

    [Fact]
    public void A_value_missing_from_an_older_file_keeps_its_default()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "Progress.json"), """{ "SeenIntro": true, "Retired": 5 }""");

        var progress = new UserData(_folder).Get<Progress>();

        Assert.True(progress.SeenIntro);
        Assert.Equal(1, progress.ChaptersUnlocked);
        Assert.Equal("Ada", progress.Name);
    }

    [Theory]
    [InlineData("{ \"ChaptersUnlocked\": ")]
    [InlineData("null")]
    public void A_broken_file_is_kept_aside_with_a_warning_and_the_defaults_are_used(string contents)
    {
        Directory.CreateDirectory(_folder);
        string path = Path.Combine(_folder, "Progress.json");
        File.WriteAllText(path, contents);
        var warnings = new StringWriter();

        var progress = new UserData(_folder, warnings).Get<Progress>();

        Assert.Equal(1, progress.ChaptersUnlocked);
        Assert.False(File.Exists(path));
        Assert.Equal(contents, File.ReadAllText(path + ".broken"));
        Assert.Contains($"{path} couldn't be read", warnings.ToString());
        Assert.Contains($"It's kept as {path}.broken, and Progress starts from its defaults.", warnings.ToString());
    }

    [Fact]
    public void A_save_that_fails_part_way_leaves_the_old_file_whole()
    {
        var data = new UserData(_folder);
        data.Save<Exploding>();
        string path = Path.Combine(_folder, "Exploding.json");
        string saved = File.ReadAllText(path);

        data.Get<Exploding>().Boom = true;
        var error = Assert.Throws<InvalidOperationException>(data.Save<Exploding>);

        Assert.Equal("boom", error.Message);
        Assert.Equal(saved, File.ReadAllText(path));
        Assert.Equal(["Exploding.json"], Directory.GetFiles(_folder).Select(Path.GetFileName));
    }

    [Fact]
    public void Two_classes_with_the_same_name_are_refused_because_they_would_share_a_file()
    {
        var data = new UserData(_folder);
        data.Get<Progress>();

        var error = Assert.Throws<ArgumentException>(data.Get<Other.Progress>);

        Assert.Equal(
            "Super.Tests.Other.Progress and Super.Tests.UserDataTests+Progress would share the file Progress.json. Rename one of them.",
            error.Message);
    }

    [Fact]
    public void A_generic_class_is_refused_because_it_has_no_file_name()
    {
        var error = Assert.Throws<ArgumentException>(new UserData(_folder).Get<List<int>>);

        Assert.Equal("List`1 is generic, so it has no file name. Store a plain class instead.", error.Message);
    }

    [Fact]
    public void Windows_keeps_it_under_roaming_app_data()
    {
        string folder = UserData.FolderFor("reef", UserDataPlatform.Windows, Env(("APPDATA", @"C:\Users\ada\AppData\Roaming")));

        Assert.Equal(@"C:\Users\ada\AppData\Roaming\reef", folder);
    }

    [Fact]
    public void Mac_keeps_it_under_application_support()
    {
        string folder = UserData.FolderFor("reef", UserDataPlatform.MacOS, Env(("HOME", "/Users/ada")));

        Assert.Equal("/Users/ada/Library/Application Support/reef", folder);
    }

    [Fact]
    public void Linux_keeps_it_under_the_config_home_when_one_is_set()
    {
        string folder = UserData.FolderFor("reef", UserDataPlatform.Linux, Env(("HOME", "/home/ada"), ("XDG_CONFIG_HOME", "/data/config/")));

        Assert.Equal("/data/config/reef", folder);
    }

    [Fact]
    public void Linux_falls_back_to_dot_config_in_the_home_folder()
    {
        string folder = UserData.FolderFor("reef", UserDataPlatform.Linux, Env(("HOME", "/home/ada")));

        Assert.Equal("/home/ada/.config/reef", folder);
    }

    [Fact]
    public void A_missing_home_says_which_variable_is_missing()
    {
        var error = Assert.Throws<InvalidOperationException>(() => UserData.FolderFor("reef", UserDataPlatform.Linux, Env()));

        Assert.Equal("HOME isn't set, so there's no user folder to keep user data in. Pass --user-data to choose one.", error.Message);
    }

    [Theory]
    [InlineData("reef/deep")]
    [InlineData("reef:deep")]
    [InlineData(" reef")]
    public void An_id_that_cant_be_a_folder_name_is_refused(string id)
    {
        var error = Assert.Throws<ArgumentException>(() => UserData.FolderFor(id, UserDataPlatform.Windows, Env(("APPDATA", @"C:\x"))));

        Assert.StartsWith($"The game id '{id}' can't be a folder name.", error.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    public void An_empty_id_is_refused(string id)
    {
        var error = Assert.Throws<ArgumentException>(() => UserData.FolderFor(id, UserDataPlatform.Windows, Env(("APPDATA", @"C:\x"))));

        Assert.StartsWith($"A game id must name a folder, so '{id}' won't do.", error.Message);
    }

    [Theory]
    [InlineData("Reef: Deep  Sea", "Reef Deep Sea")]
    [InlineData("Super", "Super")]
    [InlineData("What? Now.", "What Now")]
    public void A_title_becomes_a_folder_name_by_dropping_what_folders_cant_hold(string title, string id)
    {
        Assert.Equal(id, UserData.IdFromTitle(title));
    }

    private class Remembers(UserData userData) : IGame
    {
        public UserData UserData { get; } = userData;

        public void Load()
        {
            UserData.Get<Progress>().SeenIntro = true;
            UserData.Save<Progress>();
        }

        public void Update(GameTime time)
        {
        }

        public void Draw(View view)
        {
        }
    }

    [Fact]
    public void A_headless_run_saves_into_a_throwaway_folder_that_is_gone_afterwards()
    {
        string folder;
        using (var run = HeadlessGame.Create<Remembers>(1))
        {
            folder = run.Game.UserData.Folder;
            Assert.True(File.Exists(Path.Combine(folder, "Progress.json")));
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), folder);
        }

        Assert.False(Directory.Exists(folder));
    }

    private static Func<string, string?> Env(params (string Name, string Value)[] values) =>
        name => values.FirstOrDefault(v => v.Name == name).Value;
}
