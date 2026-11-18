using System.Numerics;
using System.Runtime.InteropServices;
using Rendition;

namespace Super;

/// <summary>What an <see cref="AnimationEvent"/> reports: a moment passed, or a window opening or closing.</summary>
public enum AnimationEventKind
{
    /// <summary>The clip passed a moment, such as a foot landing.</summary>
    Moment,

    /// <summary>A window opened, such as a sword's hitbox going live.</summary>
    Begin,

    /// <summary>A window closed: the clip passed its end, or the clip stopped counting while it was open.</summary>
    End,
}

/// <summary>
/// Something an animator's clips passed on the last tick, from <see cref="Animator.Events"/>.
/// <see cref="Weight"/> is how much the clip counted: its state's share while states
/// cross-fade, and 1 for an action. <see cref="Joint"/> is where the event's joint is in the
/// model after the tick, when the event names one.
/// </summary>
public readonly record struct AnimationEvent(string Name, AnimationEventKind Kind, ClipRef Clip, float Weight, Matrix4x4? Joint);

/// <summary>
/// Plays a clip event's sound in the world. The game loop passes one that plays it on its
/// <see cref="Audio"/>, which holds content, so animation doesn't hold the audio itself.
/// </summary>
internal delegate void ClipSoundPlayer(SoundRef sound, Vector3 at, float volume, float pitchVariation);

/// <summary>A clip's events, and where each one's moments fall, sorted so a sweep meets them in order.</summary>
internal sealed class ClipEventTable
{
    private readonly List<ClipEvent> _events = [];
    private readonly List<int> _joints = [];
    private readonly List<SoundRef?> _sounds = [];

    public IReadOnlyList<ClipEvent> Events => _events;

    /// <summary>The sound an event plays, once content has named it, or null.</summary>
    public SoundRef? SoundOf(int clipEvent) => _sounds[clipEvent];

    /// <summary>Names every event's sound in content, which throws for one it hasn't got.</summary>
    public void NameSounds(Func<string, SoundRef> sounds)
    {
        for (int i = 0; i < _events.Count; i++)
        {
            _sounds[i] = _events[i].Sound is { } sound ? sounds(sound.Path) : null;
        }
    }

    /// <summary>Every moment, window start and window end, by time. At the same time a window ends before a moment, and a moment comes before a window begins.</summary>
    public EventMark[] Marks { get; private set; } = [];

    public int JointOf(int clipEvent) => _joints[clipEvent];

    /// <summary>Adds an event, finding its joint in the skeleton, which must have it, and its sound in content, when there's content.</summary>
    public void Add(ClipEvent clipEvent, Skeleton skeleton, Func<string, SoundRef>? sounds)
    {
        int joint = clipEvent.Joint is { } name ? skeleton.IndexOf(name) : -1;
        if (clipEvent.Joint is not null && joint < 0)
        {
            throw new ArgumentException(
                $"Event {clipEvent.Name} names the joint {clipEvent.Joint}, but the rig has no joint called that. It has: {string.Join(", ", skeleton.Names)}.");
        }

        SoundRef? sound = clipEvent.Sound is { } named && sounds is not null ? sounds(named.Path) : null;
        _events.Add(clipEvent);
        _joints.Add(joint);
        _sounds.Add(sound);

        var marks = new List<EventMark>(Marks);
        int index = _events.Count - 1;
        if (clipEvent.IsWindow)
        {
            marks.Add(new EventMark(clipEvent.From, AnimationEventKind.Begin, index));
            marks.Add(new EventMark(clipEvent.To, AnimationEventKind.End, index));
        }
        else
        {
            marks.Add(new EventMark(clipEvent.From, AnimationEventKind.Moment, index));
        }

        marks.Sort(static (a, b) =>
        {
            int byTime = a.Time.CompareTo(b.Time);
            if (byTime != 0)
            {
                return byTime;
            }

            int byKind = Rank(a.Kind).CompareTo(Rank(b.Kind));
            return byKind != 0 ? byKind : a.Event.CompareTo(b.Event);
        });
        Marks = marks.ToArray();
    }

    private static int Rank(AnimationEventKind kind) => kind switch
    {
        AnimationEventKind.End => 0,
        AnimationEventKind.Moment => 1,
        _ => 2,
    };
}

internal readonly record struct EventMark(float Time, AnimationEventKind Kind, int Event);

/// <summary>
/// One clip's stretch of play over a tick, in seconds of the clip: from where it was, on by
/// <see cref="Length"/>. <see cref="Source"/> names what played it, so a window it opens is
/// closed by the same source.
/// </summary>
internal readonly record struct EventSweep(int Source, int Clip, float From, float Length, bool Loops, bool FromStart, float Weight, bool Counts);

/// <summary>
/// Works out an animator's events each tick: what its clips passed, in the order it happened,
/// and which windows are open. Its lists are kept between ticks, so a steady game allocates nothing.
/// </summary>
internal sealed class EventTracker(AnimationControllerBase controller)
{
    private readonly Rig _rig = controller.Rig;
    private readonly TickEvents _tick = new();
    private readonly List<OpenWindow> _open = [];
    private readonly List<OpenWindow> _wanted = [];

    // Which sweep of the tick this is. When two events fall at the same moment, the earlier sweep's goes first.
    private int _order;

    public ReadOnlySpan<AnimationEvent> Events => _tick.Events;

    public bool IsActive(string name)
    {
        foreach (var window in _open)
        {
            if (string.Equals(NameOf(window.Clip, window.Event), name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Starts a tick with the base states' play over it: the state fading out first, if there is
    /// one, then the current state, each with its share of the cross-fade once the tick is done.
    /// </summary>
    public void SweepStates(int state, float parameter, float phase, StateFadeOut? outgoing, float seconds)
    {
        _tick.Clear();
        _wanted.Clear();
        _order = 0;
        float incoming = 1f;
        if (outgoing is { } fading)
        {
            incoming = Math.Min((fading.Seconds + seconds) / controller.CrossFadeSeconds, 1f);
            SweepState(fading.From, fading.Parameter, fading.Phase, seconds, 1f - incoming);
        }

        SweepState(state, parameter, phase, seconds, incoming);
    }

    /// <summary>
    /// An action's play over the tick, from one time in its clip to another. A fresh action passes
    /// its start too, and ends the windows it left open the last time it played.
    /// </summary>
    public void SweepAction(int action, int clip, float from, float to, bool fresh)
    {
        // Actions have odd sources and states even ones.
        int source = action * 2 + 1;
        if (fresh)
        {
            Close(source);
        }

        Sweep(new EventSweep(source, clip, from, to - from, Loops: false, FromStart: fresh, Weight: 1f, Counts: true));
    }

    /// <summary>
    /// Closes windows whose clip stopped counting and opens those it came to count inside,
    /// then puts the tick's events in the order they happened.
    /// </summary>
    public void Finish(Animator animator, ClipSoundPlayer? play)
    {
        Reconcile();
        _tick.Publish(_rig, animator);
        if (play is not null && animator.Pose.Drawn is { } placement)
        {
            _tick.PlaySounds(_rig, play, placement, controller.PickSound);
        }
    }

    /// <summary>
    /// A state's play over the tick. A blend's clips are in step, so only the heavier one's events
    /// count, at the state's whole share.
    /// </summary>
    private void SweepState(int state, float parameter, float phase, float seconds, float share)
    {
        var definition = controller.GetState(state);
        var (first, second, towards) = definition.Motion.Pick(parameter);
        int clip = towards > 0.5f ? second : first;
        float duration = _rig.Clips[clip].Duration;
        float length = definition.Cycles(parameter, seconds) * duration;
        bool counts = share > 0f && share >= controller.EventThreshold;
        Sweep(new EventSweep(state * 2, clip, phase * duration, length, Loops: true, FromStart: false, share, counts));
    }

    /// <summary>Ends every window a source has open, as when an action starts again from the top.</summary>
    private void Close(int source)
    {
        for (int i = _open.Count - 1; i >= 0; i--)
        {
            if (_open[i].Source == source)
            {
                _tick.Add(_open[i], AnimationEventKind.End, weight: 0f, at: 0f, order: -1);
                _open.RemoveAt(i);
            }
        }
    }

    private void Sweep(in EventSweep sweep)
    {
        _order++;
        var table = _rig.EventTables[sweep.Clip];
        if (!sweep.Counts || table.Events.Count == 0)
        {
            return;
        }

        float duration = _rig.Clips[sweep.Clip].Duration;
        float to = sweep.From + sweep.Length;
        if (sweep.Loops && duration > 0f)
        {
            // Lap by lap, so a long tick passes each moment every time round.
            long last = (long)MathF.Floor(to / duration);
            for (long lap = (long)MathF.Floor(sweep.From / duration); lap <= last; lap++)
            {
                PassMarks(sweep, table, lap * duration, to);
            }

            WantWindows(sweep, table, to - MathF.Floor(to / duration) * duration);
        }
        else
        {
            PassMarks(sweep, table, 0f, MathF.Min(to, duration));
            WantWindows(sweep, table, to);
        }
    }

    /// <summary>Passes the marks of one lap of a clip that starts at this time, up to the end of the sweep.</summary>
    private void PassMarks(in EventSweep sweep, ClipEventTable table, float lapStart, float to)
    {
        foreach (var mark in table.Marks)
        {
            Pass(sweep, mark, lapStart + mark.Time, to);
        }
    }

    /// <summary>Notes the windows the clip is inside once the tick is done, at this time in the clip.</summary>
    private void WantWindows(in EventSweep sweep, ClipEventTable table, float now)
    {
        var events = table.Events;
        for (int i = 0; i < events.Count; i++)
        {
            if (events[i].IsWindow && events[i].From <= now && now < events[i].To)
            {
                _wanted.Add(new OpenWindow(sweep.Source, sweep.Clip, i));
            }
        }
    }

    /// <summary>Ends the open windows no clip wants any more, and begins the wanted ones that aren't open.</summary>
    private void Reconcile()
    {
        int kept = 0;
        for (int i = 0; i < _open.Count; i++)
        {
            var window = _open[i];
            if (_wanted.Contains(window))
            {
                _open[kept++] = window;
                continue;
            }

            _tick.Add(window, AnimationEventKind.End, weight: 0f, at: 0f, order: -1);
        }

        _open.RemoveRange(kept, _open.Count - kept);
        foreach (var window in _wanted)
        {
            if (!_open.Contains(window))
            {
                _open.Add(window);
                _tick.Add(window, AnimationEventKind.Begin, weight: 1f, at: 1f, order: int.MaxValue);
            }
        }
    }

    private void Pass(in EventSweep sweep, EventMark mark, float at, float to)
    {
        // After the start and up to the end, so a tick ending on a mark passes it and the next tick doesn't.
        bool passed = (at > sweep.From || (sweep.FromStart && at == sweep.From)) && at <= to;
        if (!passed)
        {
            return;
        }

        float fraction = sweep.Length > 0f ? (at - sweep.From) / sweep.Length : 0f;
        var window = new OpenWindow(sweep.Source, sweep.Clip, mark.Event);
        // A window that opens when it's already open, or closes when it isn't, says nothing.
        if (mark.Kind == AnimationEventKind.Begin)
        {
            if (_open.Contains(window))
            {
                return;
            }

            _open.Add(window);
        }
        else if (mark.Kind == AnimationEventKind.End && !_open.Remove(window))
        {
            return;
        }

        _tick.Add(window, mark.Kind, sweep.Weight, fraction, _order);
    }

    private string NameOf(int clip, int clipEvent) => _rig.EventTables[clip].Events[clipEvent].Name;
}

/// <summary>A clip event as one source plays it. Windows open and close per source.</summary>
internal readonly record struct OpenWindow(int Source, int Clip, int Event);

/// <summary>
/// A tick's events, gathered as the sweeps find them, then put in the order they happened. Its
/// list and array are kept between ticks, so a steady game allocates nothing.
/// </summary>
internal sealed class TickEvents
{
    private readonly List<Pending> _pending = [];
    private AnimationEvent[] _events = [];
    private OpenWindow[] _windows = [];
    private int _count;

    public ReadOnlySpan<AnimationEvent> Events => _events.AsSpan(0, _count);

    public void Clear() => _pending.Clear();

    /// <summary>An event at a fraction of the tick, from a sweep. Events at the same moment keep the sweeps' order.</summary>
    public void Add(OpenWindow window, AnimationEventKind kind, float weight, float at, int order) =>
        _pending.Add(new Pending(window, kind, weight, at, order, _pending.Count));

    /// <summary>Sorts what the tick gathered and turns it into the events the game reads.</summary>
    public void Publish(Rig rig, Animator animator)
    {
        var pending = CollectionsMarshal.AsSpan(_pending);
        SortByTime(pending);
        if (_events.Length < pending.Length)
        {
            Array.Resize(ref _events, Math.Max(pending.Length, _events.Length * 2));
            Array.Resize(ref _windows, _events.Length);
        }

        for (int i = 0; i < pending.Length; i++)
        {
            var (window, kind, weight, _, _, _) = pending[i];
            var table = rig.EventTables[window.Clip];
            int joint = table.JointOf(window.Event);
            _events[i] = new AnimationEvent(
                table.Events[window.Event].Name, kind, new ClipRef(window.Clip), weight,
                joint >= 0 ? animator.JointCache.InModel(joint, animator.Pose.Version) : null);
            _windows[i] = window;
        }

        _count = pending.Length;
    }

    /// <summary>
    /// Plays the sounds of the tick's moments and opening windows, at the event's joint, or the
    /// model's origin without one, where the model was last drawn. Each is as loud as its clip
    /// counted, and the picker, if there is one, may swap it or leave it out.
    /// </summary>
    public void PlaySounds(Rig rig, ClipSoundPlayer play, Matrix4x4 placement, EventSoundPicker? pick)
    {
        for (int i = 0; i < _count; i++)
        {
            var animationEvent = _events[i];
            var table = rig.EventTables[_windows[i].Clip];
            if (animationEvent.Kind == AnimationEventKind.End || table.SoundOf(_windows[i].Event) is not { } sound)
            {
                continue;
            }

            var at = ((animationEvent.Joint ?? Matrix4x4.Identity) * placement).Translation;
            if (pick is not null)
            {
                if (pick(at, animationEvent, sound) is not { } picked)
                {
                    continue;
                }

                sound = picked;
            }

            var settings = table.Events[_windows[i].Event].Sound!;
            play(sound, at, settings.Volume * animationEvent.Weight, settings.PitchVariation);
        }
    }

    /// <summary>Sorts the tick's events by when they happened, then by sweep, then as they came. An insertion sort, as there are few.</summary>
    private static void SortByTime(Span<Pending> pending)
    {
        for (int i = 1; i < pending.Length; i++)
        {
            var item = pending[i];
            int j = i - 1;
            while (j >= 0 && Before(item, pending[j]))
            {
                pending[j + 1] = pending[j];
                j--;
            }

            pending[j + 1] = item;
        }
    }

    private static bool Before(in Pending a, in Pending b)
    {
        if (a.At != b.At)
        {
            return a.At < b.At;
        }

        return a.Order != b.Order ? a.Order < b.Order : a.Sequence < b.Sequence;
    }

    /// <summary>An event waiting for the tick to finish: which event, at what fraction of the tick, from which sweep.</summary>
    private readonly record struct Pending(OpenWindow Window, AnimationEventKind Kind, float Weight, float At, int Order, int Sequence);
}
