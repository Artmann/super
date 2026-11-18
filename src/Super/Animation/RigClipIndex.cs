using Rendition;

namespace Super;

/// <summary>A rig's clips by name, and the check that a <see cref="ClipRef"/> names one of them.</summary>
internal sealed class RigClipIndex
{
    private readonly Dictionary<string, ClipRef> _byName = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<AnimationClip> _clips;

    public RigClipIndex(IReadOnlyList<AnimationClip> clips)
    {
        _clips = clips;
        for (int i = 0; i < clips.Count; i++)
        {
            _byName.TryAdd(clips[i].Name, new ClipRef(i));
        }
    }

    public IEnumerable<string> Names => _clips.Select(clip => clip.Name);

    public ClipRef Find(string name)
    {
        if (!_byName.TryGetValue(name, out var clip))
        {
            throw new ArgumentException($"The rig has no clip called {name}. It has: {string.Join(", ", Names)}.", nameof(name));
        }

        return clip;
    }

    public bool Has(string name) => _byName.ContainsKey(name);

    public void Check(ClipRef clip)
    {
        if (clip.Index < 0 || clip.Index >= _clips.Count)
        {
            throw new ArgumentException($"Clip {clip.Index} doesn't exist. The rig has {_clips.Count} clips.", nameof(clip));
        }
    }
}
