using Super.Testing;

namespace Super.Tests;

public class LoadingTests
{
    private class Named(Content content) : IGame
    {
        public Content Content => content;

        public void Load() => content.Model("models/rock");

        public void Update(GameTime time)
        {
        }

        public void Draw(View view)
        {
        }
    }

    /// <summary>A loader whose background work finishes only when a test says so.</summary>
    private sealed class Fake
    {
        public Content Content { get; } = new() { Streaming = true };
        public List<string> Uploaded { get; } = [];
        public Dictionary<string, TaskCompletionSource> Gates { get; } = [];

        public ContentLoader<string> Loader { get; }

        public Fake()
        {
            Loader = new ContentLoader<string>(
                "cube",
                item =>
                {
                    var gate = Gates[(string)item].Task;
                    if (Task.WhenAny(gate, Task.Delay(TimeSpan.FromSeconds(10))).GetAwaiter().GetResult() != gate)
                    {
                        throw new TimeoutException($"{item} was never finished by the test.");
                    }

                    gate.GetAwaiter().GetResult();
                    return item;
                },
                prepared =>
                {
                    Uploaded.Add((string)prepared);
                    return (string)prepared;
                });
        }

        public int Name(string path)
        {
            Gates[path] = new TaskCompletionSource();
            return Content.Model(path).Index;
        }

        public void Finish(string path)
        {
            Gates[path].SetResult();
        }

        public void Fail(string path)
        {
            Gates[path].SetException(new FileNotFoundException(path));
        }

        /// <summary>Frames until the upload lands, since the finish happens on another thread.</summary>
        public void FramesUntil(int uploads)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (Uploaded.Count < uploads && DateTime.UtcNow < deadline)
            {
                Loader.Update(Content);
                Thread.Sleep(1);
            }
        }
    }

    [Fact]
    public void Outside_the_window_everything_is_loaded_at_once()
    {
        using var run = HeadlessGame.Create<Named>(1);
        Assert.True(run.Game.Content.IsLoaded);
        Assert.Equal(1f, run.Game.Content.Progress);
    }

    [Fact]
    public void A_test_can_hold_a_load_open()
    {
        using var run = HeadlessGame.Create<Named>(1);
        run.HoldLoading();
        Assert.False(run.Game.Content.IsLoaded);
        Assert.Equal(0f, run.Game.Content.Progress);

        run.FinishLoading();
        Assert.True(run.Game.Content.IsLoaded);
    }

    [Fact]
    public void Nothing_named_counts_as_loaded()
    {
        var content = new Content { Streaming = true };
        Assert.True(content.IsLoaded);
        Assert.Equal(1f, content.Progress);
    }

    [Fact]
    public void Streaming_reports_how_far_it_has_got()
    {
        var content = new Content { Streaming = true };
        content.Model("a");
        content.Model("b");
        Assert.False(content.IsLoaded);
        Assert.Equal(0f, content.Progress);

        content.ItemLoaded();
        Assert.False(content.IsLoaded);
        Assert.Equal(0.5f, content.Progress);

        content.ItemLoaded();
        Assert.True(content.IsLoaded);
        Assert.Equal(1f, content.Progress);
    }

    [Fact]
    public void A_second_load_starts_again_from_zero()
    {
        var content = new Content { Streaming = true };
        content.Model("a");
        content.ItemLoaded();

        content.Model("b");
        Assert.False(content.IsLoaded);
        Assert.Equal(0f, content.Progress);
    }

    [Fact]
    public void The_cube_is_there_from_the_start()
    {
        var fake = new Fake();
        Assert.Equal("cube", fake.Loader.Get(0, fake.Content));
    }

    [Fact]
    public void Unfinished_work_uploads_nothing()
    {
        var fake = new Fake();
        fake.Name("a");
        fake.Loader.Update(fake.Content);
        Assert.Empty(fake.Uploaded);
        Assert.False(fake.Content.IsLoaded);
        fake.Finish("a");
    }

    [Fact]
    public void Finished_work_uploads_one_item_a_frame()
    {
        var fake = new Fake();
        fake.Name("a");
        fake.Name("b");
        fake.Loader.Update(fake.Content);
        fake.Finish("a");
        fake.Finish("b");
        fake.FramesUntil(uploads: 1);
        Assert.Single(fake.Uploaded);

        fake.FramesUntil(uploads: 2);
        Assert.Equal(["a", "b"], fake.Uploaded.Order());
        Assert.True(fake.Content.IsLoaded);
    }

    [Fact]
    public void A_slow_item_does_not_hold_up_the_ones_after_it()
    {
        var fake = new Fake();
        fake.Name("slow");
        fake.Name("quick");
        fake.Loader.Update(fake.Content);
        fake.Finish("quick");
        fake.FramesUntil(uploads: 1);
        Assert.Equal(["quick"], fake.Uploaded);
        fake.Finish("slow");
    }

    [Fact]
    public void A_model_being_drawn_is_waited_for()
    {
        var fake = new Fake();
        int rock = fake.Name("rock");
        fake.Loader.Update(fake.Content);
        Task.Run(() => fake.Finish("rock"));

        Assert.Equal("rock", fake.Loader.Get(rock, fake.Content));
        Assert.True(fake.Content.IsLoaded);
    }

    [Fact]
    public void A_model_uploaded_once_is_not_uploaded_again()
    {
        var fake = new Fake();
        int rock = fake.Name("rock");
        fake.Finish("rock");
        fake.Loader.Update(fake.Content);
        fake.Loader.Get(rock, fake.Content);
        fake.Loader.Update(fake.Content);
        fake.Loader.Get(rock, fake.Content);
        Assert.Equal(["rock"], fake.Uploaded);
    }

    [Fact]
    public void A_failed_load_throws_on_the_frame_that_finds_it()
    {
        var fake = new Fake();
        fake.Name("missing");
        fake.Loader.Update(fake.Content);
        fake.Fail("missing");

        var deadline = DateTime.UtcNow.AddSeconds(10);
        Assert.Throws<FileNotFoundException>(() =>
        {
            while (DateTime.UtcNow < deadline)
            {
                fake.Loader.Update(fake.Content);
                Thread.Sleep(1);
            }
        });
    }

    [Fact]
    public void Without_streaming_everything_uploads_before_the_frame_is_drawn()
    {
        var content = new Content();
        var uploaded = new List<object>();
        var loader = new ContentLoader<string>("cube", item => item, prepared =>
        {
            uploaded.Add(prepared);
            return "done";
        });
        content.Model("a");
        content.Model("b");

        loader.Update(content);

        Assert.Equal(["a", "b"], uploaded);
    }
}
