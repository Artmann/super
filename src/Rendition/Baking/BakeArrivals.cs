using System.Runtime.InteropServices;

namespace Rendition;

/// <summary>
/// Remembers, for each bake file the frames go on naming, whether it was last drawn live and when its bake arrived, so
/// a bake that arrives while its area is drawn live fades in. A file left out of a frame is forgotten, so the next
/// frame that names it is its first. Part of <see cref="BakeResolver"/>.
/// </summary>
internal sealed class BakeArrivals
{
    private readonly Dictionary<string, Arrival> _arrivals = new(StringComparer.Ordinal);
    private int _frame;

    /// <summary>How long, in seconds, an arriving bake takes to fade in. 0 shows every bake at once.</summary>
    public float FadeSeconds { get; set; }

    /// <summary>Starts a frame: the files <see cref="Arrive"/> names until <see cref="ForgetUnnamed"/> are kept.</summary>
    public void BeginFrame() => _frame++;

    /// <summary>Forgets every file, so the next frame that names one is its first.</summary>
    public void Clear() => _arrivals.Clear();

    /// <summary>Forgets the files this frame didn't name.</summary>
    public void ForgetUnnamed()
    {
        // Removing while enumerating is allowed for a Dictionary.
        foreach (var (file, arrival) in _arrivals)
        {
            if (arrival.Frame != _frame)
            {
                _arrivals.Remove(file);
            }
        }
    }

    // How far the bake in file has faded in this frame, from 0 to 1. A file drawn live, its bake missing or stale,
    // remembers that; its bake arrives on the first frame it is fresh after that, and fades in over FadeSeconds from
    // then. A bake fresh on the first frame that names its file never fades, nor does one while it is pending, which
    // is drawn live but is already saved, as when a level loads. Asking twice in a frame, as two placed copies of a
    // chunk do, gives the same answer.
    public float Arrive(string file, bool fresh, bool pending, float time)
    {
        ref var arrival = ref CollectionsMarshal.GetValueRefOrAddDefault(_arrivals, file, out bool known);
        arrival.Frame = _frame;
        if (!known)
        {
            arrival.Live = !fresh && !pending;
            return fresh ? 1f : 0f;
        }

        if (pending)
        {
            return 0f;
        }

        if (!fresh)
        {
            arrival.Live = true;
            arrival.Fading = false;
            return 0f;
        }

        return FadeIn(ref arrival, time);
    }

    // A fresh bake's fade: it starts on the first fresh frame after the file was drawn live, and eases in over
    // FadeSeconds. Time going back counts as the fade done.
    private float FadeIn(ref Arrival arrival, float time)
    {
        if (arrival.Live)
        {
            arrival.Live = false;
            arrival.Fading = true;
            arrival.At = time;
        }

        if (!arrival.Fading)
        {
            return 1f;
        }

        float since = time - arrival.At;
        if (FadeSeconds <= 0f || since < 0f || since >= FadeSeconds)
        {
            arrival.Fading = false;
            return 1f;
        }

        float t = since / FadeSeconds;
        return t * t * (3f - 2f * t);
    }

    private struct Arrival
    {
        public int Frame;
        public bool Live;
        public bool Fading;
        public float At;
    }
}
