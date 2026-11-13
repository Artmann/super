using System.Numerics;
using Rendition;

namespace Super.Tests;

/// <summary>
/// <see cref="BakeQueue"/>: which missing or stale area the renderer bakes next in the background, one at a time.
/// </summary>
public class BakeQueueTests
{
    private static readonly BakeInputs InputsA = new(1, 2, 3, 4);
    private static readonly BakeInputs InputsB = new(5, 6, 7, 8);

    [Fact]
    public void Nearest_area_starts_first()
    {
        var queue = new BakeQueue([BakeQuality.Preview]);
        BakeWant[] wants =
        [
            Want("five", InputsA, 5f),
            Want("one", InputsA, 1f),
            Want("three", InputsA, 3f),
        ];

        var started = queue.Update(wants, Vector3.Zero, out bool cancel, out var quality);

        Assert.NotNull(started);
        Assert.Equal("one", started.Value.Name);
        Assert.Equal(BakeQuality.Preview, quality);
        Assert.False(cancel);
        Assert.Equal(2, queue.Waiting);
    }

    [Fact]
    public void Nearest_is_measured_from_the_camera()
    {
        var queue = new BakeQueue([BakeQuality.Preview]);
        BakeWant[] wants = [Want("near-origin", InputsA, 1f), Want("near-camera", InputsA, 9f)];

        var started = queue.Update(wants, new Vector3(10f, 0f, 0f), out _, out _);

        Assert.Equal("near-camera", started!.Value.Name);
    }

    [Fact]
    public void Equally_near_areas_start_by_file_name()
    {
        var queue = new BakeQueue([BakeQuality.Preview]);
        BakeWant[] wants = [Want("b", InputsA, 2f), Want("a", InputsA, -2f)];

        var started = queue.Update(wants, Vector3.Zero, out _, out _);

        Assert.Equal("a", started!.Value.Name);
    }

    [Fact]
    public void Every_preview_runs_before_any_final()
    {
        var queue = new BakeQueue([BakeQuality.Preview, BakeQuality.Final]);
        BakeWant[] wants = [Want("b", InputsB, 4f), Want("a", InputsA, 1f)];
        var starts = new List<string>();

        for (int frame = 0; frame < 10; frame++)
        {
            if (queue.Update(wants, Vector3.Zero, out _, out var quality) is { } started)
            {
                starts.Add($"{started.Name} {quality}");
                queue.Finished(started.File, started.Inputs, quality);
            }
        }

        Assert.Equal(["a Preview", "b Preview", "a Final", "b Final"], starts);
    }

    [Fact]
    public void Running_bake_carries_on_while_another_waits()
    {
        var queue = new BakeQueue([BakeQuality.Preview]);
        BakeWant[] wants = [Want("a", InputsA, 1f), Want("b", InputsB, 2f)];

        Assert.NotNull(queue.Update(wants, Vector3.Zero, out _, out _));
        var next = queue.Update(wants, Vector3.Zero, out bool cancel, out _);

        Assert.Null(next);
        Assert.False(cancel);
        Assert.Equal(1, queue.Waiting);
    }

    [Fact]
    public void Changing_the_scene_mid_bake_restarts_it()
    {
        var queue = new BakeQueue([BakeQuality.Preview]);
        Assert.NotNull(queue.Update([Want("a", InputsA, 1f)], Vector3.Zero, out _, out _));

        var restarted = queue.Update([Want("a", InputsB, 1f)], Vector3.Zero, out bool cancel, out var quality);

        Assert.True(cancel);
        Assert.NotNull(restarted);
        Assert.Equal(InputsB, restarted.Value.Inputs);
        Assert.Equal(BakeQuality.Preview, quality);
    }

    [Fact]
    public void Running_bake_carries_on_when_its_area_leaves_the_frame()
    {
        var queue = new BakeQueue([BakeQuality.Preview]);
        Assert.NotNull(queue.Update([Want("a", InputsA, 1f)], Vector3.Zero, out _, out _));

        var next = queue.Update([Want("b", InputsB, 1f)], Vector3.Zero, out bool cancel, out _);

        Assert.Null(next);
        Assert.False(cancel);
    }

    [Fact]
    public void Saved_bake_that_is_still_stale_is_not_baked_again()
    {
        var queue = new BakeQueue([BakeQuality.Preview, BakeQuality.Final]);
        BakeWant[] wants = [Want("a", InputsA, 1f)];
        queue.Finished("a.lightbake", InputsA, BakeQuality.Preview);
        queue.Finished("a.lightbake", InputsA, BakeQuality.Final);

        var started = queue.Update(wants, Vector3.Zero, out bool cancel, out _);

        Assert.Null(started);
        Assert.False(cancel);
        Assert.Equal(0, queue.Waiting);
    }

    [Fact]
    public void Same_area_with_other_inputs_is_baked_again()
    {
        var queue = new BakeQueue([BakeQuality.Preview]);
        queue.Finished("a.lightbake", InputsA, BakeQuality.Preview);

        var started = queue.Update([Want("a", InputsB, 1f)], Vector3.Zero, out _, out _);

        Assert.NotNull(started);
        Assert.Equal(InputsB, started.Value.Inputs);
    }

    [Fact]
    public void Finished_preview_leaves_the_final_to_do()
    {
        var queue = new BakeQueue([BakeQuality.Preview, BakeQuality.Final]);
        Assert.False(queue.Unfinished("a.lightbake", InputsA));

        queue.Finished("a.lightbake", InputsA, BakeQuality.Preview);
        Assert.True(queue.Unfinished("a.lightbake", InputsA));
        Assert.False(queue.Unfinished("a.lightbake", InputsB));

        queue.Finished("a.lightbake", InputsA, BakeQuality.Final);
        Assert.False(queue.Unfinished("a.lightbake", InputsA));
    }

    [Fact]
    public void Copies_of_a_chunk_are_one_bake()
    {
        var queue = new BakeQueue([BakeQuality.Preview]);
        BakeWant[] wants =
        [
            Want("chunk", InputsA, 3f, chunk: 0),
            Want("chunk", InputsA, 1f, chunk: 1),
            Want("chunk", InputsA, 2f, chunk: 2),
        ];

        var started = queue.Update(wants, Vector3.Zero, out _, out _);

        Assert.NotNull(started);
        Assert.Equal(1, started.Value.Chunk);
        Assert.Equal(0, queue.Waiting);
        Assert.Null(queue.Update(wants, Vector3.Zero, out _, out _));
        Assert.Equal(0, queue.Waiting);
    }

    [Fact]
    public void Unnamed_wants_are_forgotten()
    {
        var queue = new BakeQueue([BakeQuality.Preview]);
        var first = queue.Update([Want("a", InputsA, 1f), Want("b", InputsB, 2f)], Vector3.Zero, out _, out var quality);
        Assert.Equal("a", first!.Value.Name);
        Assert.Equal(1, queue.Waiting);

        // The next frame names only the running area; b is forgotten.
        Assert.Null(queue.Update([Want("a", InputsA, 1f)], Vector3.Zero, out _, out _));
        Assert.Equal(0, queue.Waiting);
        queue.Finished(first.Value.File, first.Value.Inputs, quality);

        // A frame with no wants starts nothing, and allocates nothing.
        queue.Update([], Vector3.Zero, out _, out _);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var next = queue.Update([], Vector3.Zero, out bool cancel, out _);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Null(next);
        Assert.False(cancel);
        Assert.Equal(0, queue.Waiting);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void A_frame_of_the_same_wants_allocates_nothing()
    {
        var queue = new BakeQueue([BakeQuality.Preview]);
        BakeWant[] wants = [Want("a", InputsA, 1f), Want("b", InputsB, 2f), Want("b", InputsB, 3f, chunk: 1)];
        queue.Update(wants, Vector3.Zero, out _, out _);
        queue.Update(wants, Vector3.Zero, out _, out _);

        long before = GC.GetAllocatedBytesForCurrentThread();
        queue.Update(wants, Vector3.Zero, out _, out _);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void A_queue_without_qualities_explains_itself()
    {
        var error = Assert.Throws<ArgumentException>(() => new BakeQueue([]));
        Assert.Equal("A background bake needs at least one quality to bake at. (Parameter 'qualities')", error.Message);
    }

    [Fact]
    public void Fresh_saved_preview_still_wants_its_final()
    {
        // A new queue, as after the game restarts: it baked nothing itself, but the saved file is a fresh Preview.
        var queue = new BakeQueue([BakeQuality.Preview, BakeQuality.Final]);
        var store = new SavedStore { ["a.lightbake"] = (InputsA, BakeQuality.Preview) };

        Assert.True(queue.Wants(store, "a.lightbake", InputsA));
        var started = queue.Update([Want("a", InputsA, 1f)], Vector3.Zero, out _, out var quality);

        Assert.Equal("a", started!.Value.Name);
        Assert.Equal(BakeQuality.Final, quality);
    }

    [Fact]
    public void Fresh_saved_preview_wants_the_final_when_only_the_final_is_asked_for()
    {
        var queue = new BakeQueue([BakeQuality.Final]);
        var store = new SavedStore { ["a.lightbake"] = (InputsA, BakeQuality.Preview) };

        Assert.True(queue.Wants(store, "a.lightbake", InputsA));
    }

    [Fact]
    public void Fresh_saved_final_wants_nothing()
    {
        var queue = new BakeQueue([BakeQuality.Preview, BakeQuality.Final]);
        var store = new SavedStore { ["a.lightbake"] = (InputsA, BakeQuality.Final) };

        Assert.False(queue.Wants(store, "a.lightbake", InputsA));
    }

    [Fact]
    public void Missing_or_stale_saved_bake_is_wanted()
    {
        var queue = new BakeQueue([BakeQuality.Preview]);
        var store = new SavedStore { ["a.lightbake"] = (InputsA, BakeQuality.Final) };

        Assert.True(queue.Wants(store, "a.lightbake", InputsB));
        Assert.True(queue.Wants(store, "missing.lightbake", InputsA));
    }

    [Fact]
    public void Fresh_bake_of_unknown_quality_is_wanted_only_while_unfinished()
    {
        var queue = new BakeQueue([BakeQuality.Preview, BakeQuality.Final]);
        var store = new SavedStore { ["a.lightbake"] = (InputsA, null) };

        Assert.False(queue.Wants(store, "a.lightbake", InputsA));

        queue.Finished("a.lightbake", InputsA, BakeQuality.Preview);
        Assert.True(queue.Wants(store, "a.lightbake", InputsA));
    }

    [Fact]
    public void Running_bake_is_called_off_when_its_file_is_named_with_other_inputs_though_not_wanted()
    {
        var queue = new BakeQueue([BakeQuality.Preview]);
        Assert.NotNull(queue.Update([Want("a", InputsA, 1f)], Vector3.Zero, out _, out _));

        // Named with its own inputs, it carries on.
        Assert.False(queue.Named("a.lightbake", InputsA));
        Assert.True(queue.Running);

        // The file is already fresh from other inputs, so it isn't a want, but the old bake would overwrite it.
        Assert.True(queue.Named("a.lightbake", InputsB));
        Assert.False(queue.Running);
        Assert.Null(queue.Update([], Vector3.Zero, out bool cancel, out _));
        Assert.False(cancel);
    }

    [Fact]
    public void Another_file_named_leaves_the_running_bake_alone()
    {
        var queue = new BakeQueue([BakeQuality.Preview]);
        queue.Update([Want("a", InputsA, 1f)], Vector3.Zero, out _, out _);

        Assert.False(queue.Named("b.lightbake", InputsB));
        Assert.True(queue.Running);
    }

    private static BakeWant Want(string name, BakeInputs inputs, float x, int chunk = -1) =>
        new(name + ".lightbake", name, inputs, chunk, new Vector3(x, 0f, 0f));

    // Saved files by name: the inputs each was made from and its quality, null when the store can't tell.
    private sealed class SavedStore : Dictionary<string, (BakeInputs Inputs, BakeQuality? Quality)>, IBakeStore
    {
        public bool IsFresh(string file, BakeInputs inputs) => TryGetValue(file, out var saved) && saved.Inputs == inputs;

        public BakeQuality? SavedQuality(string file) => TryGetValue(file, out var saved) ? saved.Quality : null;
    }
}
