using Rendition;

namespace Super.Cook.Models;

/// <summary>Puts the events a model's settings mark on the clips it cooked.</summary>
internal static class ClipEventSettings
{
    /// <summary>
    /// Puts the settings' events on their clips. Each must name a clip and a joint the model has, and fall inside its
    /// clip. The first that doesn't is returned, saying what it could have been, and leaves the clips as they were.
    /// </summary>
    public static IReadOnlyList<string> Attach(List<AnimationEventSource> sources, List<AnimationClip> clips, Skeleton? skeleton)
    {
        if (sources.Count == 0)
        {
            return [];
        }

        if (skeleton is null)
        {
            return ["The settings give events, but the model has no skeleton to play clips on."];
        }

        foreach (var source in sources)
        {
            if (Problem(source, clips, skeleton) is { } problem)
            {
                return [$"Event \"{source.Name}\" {problem}"];
            }
        }

        for (int i = 0; i < clips.Count; i++)
        {
            var clip = clips[i];
            var events = sources.Where(source => source.Clip == clip.Name).Select(ToClipEvent).ToList();
            if (events.Count > 0)
            {
                clips[i] = new AnimationClip(clip.Name, clip.FrameRate, clip.JointCount, clip.Frames, [.. clip.Events, .. events]);
            }
        }

        return [];
    }

    private static ClipEvent ToClipEvent(AnimationEventSource source) =>
        source.Time is { } at
            ? new ClipEvent(source.Name, at, source.Joint)
            : new ClipEvent(source.Name, source.From!.Value, source.To!.Value, source.Joint);

    private static string? Problem(AnimationEventSource source, List<AnimationClip> clips, Skeleton skeleton)
    {
        if (clips.Find(candidate => candidate.Name == source.Clip) is not { } clip)
        {
            return $"names the clip \"{source.Clip}\", but the model has none called that. It has: {string.Join(", ", clips.Select(c => c.Name))}.";
        }

        if (string.IsNullOrEmpty(source.Name))
        {
            return "has no name.";
        }

        bool window = source.From is not null || source.To is not null;
        if (source.Time is not null == window)
        {
            return "needs either a time, for a moment, or a from and a to, for a window.";
        }

        float from = source.Time ?? source.From ?? -1f;
        float to = source.Time ?? source.To ?? -1f;
        if (!(from >= 0f) || !(to <= clip.Duration) || !(from <= to))
        {
            return $"runs from {from} to {to} seconds, but clip {clip.Name} lasts {clip.Duration}, and the start can't come after the end.";
        }

        if (source.Joint is { } joint && skeleton.IndexOf(joint) < 0)
        {
            return $"names the joint \"{joint}\", but the model has none called that. It has: {string.Join(", ", skeleton.Names)}.";
        }

        return null;
    }
}
