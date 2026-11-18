using System.Numerics;
using System.Runtime.InteropServices;
using Rendition.Vulkan;
using Silk.NET.Vulkan;

namespace Rendition;

/// <summary>
/// Which light probes a frame updates: the four cascades that follow the camera and the fixed grid, the scheduler that
/// picks the next probes, this frame's batch of them and where they are, and the cells the frame must clear from the
/// atlases. Made anew with the atlases, so every probe starts uncaptured. See "Light probes" in docs/design.md.
/// </summary>
internal sealed class ProbeBatch
{
    private readonly List<ProbeId> _ids = [];
    private readonly List<Vector3> _positions = [];
    private readonly List<float> _spacings = [];

    // The cascades' moves and the fixed grid's changes that this frame must clear from the atlases.
    private readonly List<ProbeInvalidation> _invalidations = [];

    // What the probes were last updated for, to see what changed: the lighting that reaches everything, and each point
    // and spot light by its key.
    private int _lightingKey;
    private readonly ProbeLightTracker _lights = new();

    /// <summary>
    /// The four probe cascades that follow the camera, moved to it every frame the probes are on. Their tiles live in
    /// layers 0 to 3 of the light probe atlases.
    /// </summary>
    public ProbeCascades Cascades { get; private set; } = new(ProbeQuality.High);

    /// <summary>Which probes to update next, or null while there are no atlases.</summary>
    public ProbeScheduler? Scheduler { get; private set; }

    /// <summary>The fixed grid the probes were last updated for, or null for none.</summary>
    public ProbeVolumeSettings? Grid { get; private set; }

    /// <summary>This frame's batch of probes to update.</summary>
    public List<ProbeId> Ids => _ids;

    /// <summary>Where each probe of the batch is.</summary>
    public ReadOnlySpan<Vector3> Positions => CollectionsMarshal.AsSpan(_positions);

    /// <summary>How far apart each batch probe's layer's probes are.</summary>
    public ReadOnlySpan<float> Spacings => CollectionsMarshal.AsSpan(_spacings);

    /// <summary>What the batch's folds fill the sky with, where a probe saw nothing.</summary>
    public float EnvironmentIntensity { get; private set; }

    /// <summary>Starts again at <paramref name="quality"/>, with new cascades and a new scheduler.</summary>
    public void Restart(ProbeQuality quality)
    {
        Cascades = new ProbeCascades(quality);
        Scheduler = new ProbeScheduler(Cascades, 0);
        _ids.Clear();
        Grid = null;
        _lightingKey = 0;
        _lights.Clear();
        _invalidations.Clear();
    }

    /// <summary>Plans nothing more, for probes switched off or atlases let go.</summary>
    public void Stop()
    {
        Scheduler = null;
        _ids.Clear();
        _invalidations.Clear();
    }

    /// <summary>
    /// Empties the batch, moves the cascades with the camera, and tells the scheduler what changed: the cells the
    /// cascades left, which are cleared on the GPU, a new fixed grid, the lighting that reaches every probe, as
    /// <paramref name="lightingKey"/> sums it up, and each point and spot light, which reaches only the probes in its
    /// range.
    /// </summary>
    public void Follow(in FrameView view, int lightingKey, ReadOnlySpan<LocalLight> lights)
    {
        _ids.Clear();
        _positions.Clear();
        _spacings.Clear();
        var scheduler = Scheduler!;
        var scroll = Cascades.Move(view.CameraPosition);
        scheduler.Scrolled(scroll);
        for (int c = 0; c < ProbeCascades.Count; c++)
        {
            var x = scroll.Slabs[c * 3];
            var y = scroll.Slabs[c * 3 + 1];
            var z = scroll.Slabs[c * 3 + 2];
            if (x.Count == 0 && y.Count == 0 && z.Count == 0)
            {
                continue;
            }

            var layout = Cascades.Layout(c);
            _invalidations.Add(new ProbeInvalidation(c, Cascades.Origin(c),
                new Int3(layout.CountX, layout.CountY, layout.CountZ),
                new Int3(x.Start, y.Start, z.Start), new Int3(x.Count, y.Count, z.Count)));
        }

        FollowGrid(view, scheduler);
        if (lightingKey != _lightingKey)
        {
            _lightingKey = lightingKey;
            scheduler.EverythingChanged();
        }

        _lights.Follow(lights, scheduler);
    }

    // A new box for the fixed grid starts it again, with its old probes cleared.
    private void FollowGrid(in FrameView view, ProbeScheduler scheduler)
    {
        if (view.LightProbes == Grid)
        {
            return;
        }

        Grid = view.LightProbes;
        if (Grid is { } grid)
        {
            var (countX, countY, countZ) = ProbeGrid.Counts(grid);
            scheduler.SetFixedGrid(grid.Min, countX, countY, countZ, grid.Spacing);
        }
        else
        {
            scheduler.ClearFixedGrid();
        }

        _invalidations.Add(new ProbeInvalidation(ProbeVolume.FixedGridLayer, default,
            new Int3(ProbeGrid.MaxProbes, 1, 1), default, new Int3(ProbeGrid.MaxProbes, 0, 0)));
    }

    /// <summary>
    /// Takes this frame's batch from the scheduler, nearest and most in view first, at most <paramref name="budget"/>
    /// probes, of which only <paramref name="refresh"/> refresh settled ones. Then finds where each is.
    /// </summary>
    public void Take(in FrameView view, int budget, int refresh)
    {
        var forward = -new Vector3(view.View.M13, view.View.M23, view.View.M33);

        // The scheduler refills its list on every call, and the triangle budget may cut this frame's copy short.
        var next = Scheduler!.Next(budget, view.CameraPosition, forward, refresh);
        for (int i = 0; i < next.Count; i++)
        {
            _ids.Add(next[i]);
        }

        if (_ids.Count > 0)
        {
            EnvironmentIntensity = view.Environment.Intensity;
        }

        // Where each probe of the batch is, and how far apart its layer's probes are.
        foreach (var id in _ids)
        {
            if (id.Cascade == ProbeScheduler.FixedGrid)
            {
                var grid = Grid!.Value;
                _positions.Add(ProbeGrid.Position(grid, id.Slot));
                _spacings.Add(grid.Spacing);
            }
            else
            {
                _positions.Add(Cascades.Position(id.Cascade, id.Slot));
                _spacings.Add(Cascades.Layout(id.Cascade).Spacing);
            }
        }
    }

    /// <summary>Keeps only the first <paramref name="count"/> probes; the rest stay as they are in the scheduler.</summary>
    public void KeepFirst(int count)
    {
        if (count < _ids.Count)
        {
            _ids.RemoveRange(count, _ids.Count - count);
        }
    }

    /// <summary>Clears the cells the cascades left and the fixed grid's old probes from the atlases.</summary>
    public void RecordInvalidations(CommandBuffer cmd, ProbeVolume volume)
    {
        foreach (var invalidation in _invalidations)
        {
            volume.RecordInvalidate(cmd, invalidation);
        }

        _invalidations.Clear();
    }
}
