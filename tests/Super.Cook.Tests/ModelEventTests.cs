using Rendition;

namespace Super.Cook.Tests;

public sealed class ModelEventTests : TempFolders
{
    private const string Walk = """{ "file": "walk.gltf", "name": "Walk" }""";

    private static string Sidecar(string events) => $$"""{ "animations": [{{Walk}}], "events": [{{events}}] }""";

    private string Problem(string events)
    {
        var (result, _) = WalkingRig.Cook(Temp(), Sidecar(events));
        Assert.False(result.Succeeded);
        return Assert.Single(result.Problems, p => p.IsError).Message;
    }

    [Fact]
    public void Events_in_the_settings_are_in_the_cooked_clip()
    {
        var model = WalkingRig.CookModel(Temp(), Sidecar("""
            { "clip": "Walk", "time": 0.25, "name": "step", "joint": "hips" },
            { "clip": "Walk", "from": 0.5, "to": 0.75, "name": "lunge" }
            """));

        var walk = model.Clips.Single(c => c.Name == "Walk");
        Assert.Equal([new ClipEvent("step", 0.25f, "hips"), new ClipEvent("lunge", 0.5f, 0.75f)], walk.Events);
    }

    [Fact]
    public void An_event_on_an_unknown_clip_lists_the_clips()
    {
        Assert.Equal(
            "Event \"step\" names the clip \"Run\", but the model has none called that. It has: Walk.",
            Problem("""{ "clip": "Run", "time": 0.25, "name": "step" }"""));
    }

    [Fact]
    public void An_event_with_both_a_time_and_a_window_is_an_error()
    {
        Assert.Equal(
            "Event \"step\" needs either a time, for a moment, or a from and a to, for a window.",
            Problem("""{ "clip": "Walk", "time": 0.25, "from": 0.5, "to": 0.75, "name": "step" }"""));
    }

    [Fact]
    public void An_event_past_the_end_of_its_clip_is_an_error()
    {
        Assert.Equal(
            "Event \"step\" runs from 2 to 2 seconds, but clip Walk lasts 1, and the start can't come after the end.",
            Problem("""{ "clip": "Walk", "time": 2, "name": "step" }"""));
    }

    [Fact]
    public void An_event_on_an_unknown_joint_lists_the_joints()
    {
        Assert.StartsWith(
            "Event \"step\" names the joint \"paw\", but the model has none called that. It has: ",
            Problem("""{ "clip": "Walk", "time": 0.25, "name": "step", "joint": "paw" }"""));
    }

    [Fact]
    public void An_event_needs_a_name()
    {
        Assert.Equal("Event \"\" has no name, and no sound to be named after.", Problem("""{ "clip": "Walk", "time": 0.25 }"""));
    }

    [Fact]
    public void An_events_sound_is_in_the_cooked_clip_and_names_an_unnamed_event()
    {
        var model = WalkingRig.CookModel(Temp(), Sidecar("""
            { "clip": "Walk", "time": 0.25, "sound": "sounds/step.wav", "joint": "hips", "volume": 0.6, "pitchVariation": 0.08 },
            { "clip": "Walk", "from": 0.5, "to": 0.75, "name": "swing", "sound": "sounds/whoosh.wav" },
            { "clip": "Walk", "time": 0.5, "name": "quiet" }
            """));

        var events = model.Clips.Single(c => c.Name == "Walk").Events;
        Assert.Equal(3, events.Count);
        Assert.Equal(("sounds/step.wav", new ClipSound("sounds/step.wav", 0.6f, 0.08f)), (events[0].Name, events[0].Sound));
        Assert.Equal(("swing", new ClipSound("sounds/whoosh.wav")), (events[1].Name, events[1].Sound));
        Assert.Null(events[2].Sound);
    }

    [Fact]
    public void A_negative_volume_is_an_error()
    {
        Assert.Equal(
            "Event \"step\" has a volume of -1, but it can't be negative.",
            Problem("""{ "clip": "Walk", "time": 0.25, "name": "step", "sound": "sounds/step.wav", "volume": -1 }"""));
    }

    [Fact]
    public void A_pitch_variation_of_one_or_more_is_an_error()
    {
        Assert.Equal(
            "Event \"step\" has a pitch variation of 1, but it must be from 0 up to, but not including, 1.",
            Problem("""{ "clip": "Walk", "time": 0.25, "name": "step", "sound": "sounds/step.wav", "pitchVariation": 1 }"""));
    }

    [Fact]
    public void A_volume_without_a_sound_is_an_error()
    {
        Assert.Equal(
            "Event \"step\" gives a volume or pitch variation, but no sound to play.",
            Problem("""{ "clip": "Walk", "time": 0.25, "name": "step", "volume": 0.5 }"""));
    }
}
