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
                return [$"Event \"{NameOf(source)}\" {problem}"];
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

    private static ClipEvent ToClipEvent(AnimationEventSource source)
    {
        string name = NameOf(source);
        var clipEvent = source.Time is { } at
            ? new ClipEvent(name, at, source.Joint)
            : new ClipEvent(name, source.From!.Value, source.To!.Value, source.Joint);
        return clipEvent with { Sound = source.Sound is { } sound ? new ClipSound(sound, source.Volume, source.PitchVariation) : null };
    }

    /// <summary>The event's name, or its sound's when it has no name of its own.</summary>
    private static string NameOf(AnimationEventSource source) =>
        string.IsNullOrEmpty(source.Name) ? source.Sound ?? "" : source.Name;

    private static string? Problem(AnimationEventSource source, List<AnimationClip> clips, Skeleton skeleton)
    {
        if (clips.Find(candidate => candidate.Name == source.Clip) is not { } clip)
        {
            return $"names the clip \"{source.Clip}\", but the model has none called that. It has: {string.Join(", ", clips.Select(c => c.Name))}.";
        }

        if (NameOrSoundProblem(source) is { } nameOrSound)
        {
            return nameOrSound;
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

    private static string? NameOrSoundProblem(AnimationEventSource source)
    {
        if (string.IsNullOrEmpty(NameOf(source)))
        {
            return "has no name, and no sound to be named after.";
        }

        if (source.Sound is null)
        {
            return source.Volume != 1f || source.PitchVariation != 0f
                ? "gives a volume or pitch variation, but no sound to play."
                : null;
        }

        if (string.IsNullOrWhiteSpace(source.Sound))
        {
            return "has an empty sound. Name a sound, such as sounds/step.wav, or leave it out.";
        }

        if (!(source.Volume >= 0f))
        {
            return $"has a volume of {source.Volume}, but it can't be negative.";
        }

        if (!(source.PitchVariation >= 0f && source.PitchVariation < 1f))
        {
            return $"has a pitch variation of {source.PitchVariation}, but it must be from 0 up to, but not including, 1.";
        }

        return null;
    }
}
