using Microsoft.Extensions.Time.Testing;
using Super.Testing;

namespace Super.Tests;

public class LoadingTests
{
    /// <summary>Content holding every model these tests name, so naming them passes the check.</summary>
    private static MemoryContent Models()
    {
        var files = new MemoryContent();
        foreach (string name in new[] { "models/rock", "a", "b", "slow", "quick", "rock", "missing" })
        {
            files.AddModel(name);
        }

        return files;
    }

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
        public Content Content { get; } = new(Models()) { Store = { Loading = { Streaming = true } } };
        public List<string> Uploaded { get; } = [];
        public Dictionary<string, TaskCompletionSource> Gates { get; } = [];

        /// <summary>The clock the loader times its uploads by. Each upload moves it on by <see cref="UploadTime"/>.</summary>
        public FakeTimeProvider Clock { get; } = new();

        public TimeSpan UploadTime { get; set; }

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
                (prepared, _) =>
                {
                    Uploaded.Add((string)prepared);
                    Clock.Advance(UploadTime);
                    return (string)prepared;
                })
            {
                Clock = Clock,
                // The work blocks until the test finishes it. On the thread pool, a pool kept busy by the
                // tests running alongside could leave it unstarted past every deadline here.
                Start = work =>
                {
                    var task = OwnThread(work);
                    Started.Add(task);
                    return task;
                },
            };
        }

        /// <summary>Runs work on a thread of its own, so the thread pool being busy can't hold it up.</summary>
        public static Task<T> OwnThread<T>(Func<T> work) =>
            Task.Factory.StartNew(work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        public int Name(string path)
        {
            Gates[path] = new TaskCompletionSource();
            return Content.Model(path).Index;
        }

        public void Finish(string path)
        {
            Gates[path].SetResult();
        }

        /// <summary>Opens every gate and waits until all background work started so far has ended, so the next frame can upload it all.</summary>
        public void FinishAll()
        {
            foreach (var gate in Gates.Values)
            {
                gate.TrySetResult();
            }

            Assert.True(Task.WaitAll([.. Started], TimeSpan.FromSeconds(10)), "The background work never ended.");
        }

        /// <summary>The background work the loader started, in the order it started it.</summary>
        public List<Task<object>> Started { get; } = [];

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
        using var run = HeadlessGame.Create<Named>(1, services => services.AddContentFiles(Models()));
        Assert.True(run.Game.Content.IsLoaded);
        Assert.Equal(1f, run.Game.Content.Progress);
    }

    [Fact]
    public void A_test_can_hold_a_load_open()
    {
        using var run = HeadlessGame.Create<Named>(1, services => services.AddContentFiles(Models()));
        run.HoldLoading();
        Assert.False(run.Game.Content.IsLoaded);
        Assert.Equal(0f, run.Game.Content.Progress);

        run.FinishLoading();
        Assert.True(run.Game.Content.IsLoaded);
    }

    [Fact]
    public void Nothing_named_counts_as_loaded()
    {
        var content = new Content(Models()) { Store = { Loading = { Streaming = true } } };
        Assert.True(content.IsLoaded);
        Assert.Equal(1f, content.Progress);
    }

    [Fact]
    public void Streaming_reports_how_far_it_has_got()
    {
        var content = new Content(Models()) { Store = { Loading = { Streaming = true } } };
        content.Model("a");
        content.Model("b");
        Assert.False(content.IsLoaded);
        Assert.Equal(0f, content.Progress);

        content.Store.Loading.ItemLoaded(1);
        Assert.False(content.IsLoaded);
        Assert.Equal(0.5f, content.Progress);

        content.Store.Loading.ItemLoaded(2);
        Assert.True(content.IsLoaded);
        Assert.Equal(1f, content.Progress);
    }

    [Fact]
    public void A_second_load_starts_again_from_zero()
    {
        var content = new Content(Models()) { Store = { Loading = { Streaming = true } } };
        content.Model("a");
        content.Store.Loading.ItemLoaded(1);

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
    public void Finished_work_uploads_until_the_frame_budget_is_used()
    {
        var fake = new Fake { UploadTime = TimeSpan.FromMilliseconds(0.75) };
        fake.Content.Store.Loading.UploadBudget = TimeSpan.FromMilliseconds(2);
        foreach (string name in new[] { "a", "b", "slow", "quick", "rock" })
        {
            fake.Name(name);
        }

        fake.Loader.Update(fake.Content);
        fake.FinishAll();

        // 0.75, 1.5 and 2.25 ms: the third upload uses the budget up, so the frame stops there.
        fake.Loader.Update(fake.Content);
        Assert.Equal(3, fake.Uploaded.Count);

        fake.Loader.Update(fake.Content);
        Assert.Equal(["a", "b", "quick", "rock", "slow"], fake.Uploaded.Order());
        Assert.True(fake.Content.IsLoaded);
    }

    [Fact]
    public void An_upload_longer_than_the_budget_still_goes_one_a_frame()
    {
        var fake = new Fake { UploadTime = TimeSpan.FromMilliseconds(10) };
        fake.Content.Store.Loading.UploadBudget = TimeSpan.FromMilliseconds(2);
        fake.Name("a");
        fake.Name("b");
        fake.Loader.Update(fake.Content);
        fake.FinishAll();

        fake.Loader.Update(fake.Content);
        Assert.Single(fake.Uploaded);

        fake.Loader.Update(fake.Content);
        Assert.Equal(["a", "b"], fake.Uploaded.Order());
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
        Fake.OwnThread(() =>
        {
            fake.Finish("rock");
            return true;
        });

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
        var content = new Content(Models());
        var uploaded = new List<object>();
        var loader = new ContentLoader<string>("cube", item => item, (prepared, _) =>
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
