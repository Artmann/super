using System.Numerics;

namespace Rendition;

/// <summary>A cell of a probe cascade, counted in whole spacings from the world origin.</summary>
internal readonly record struct Int3(int X, int Y, int Z);

/// <summary>The shape of one probe cascade: how far apart its probes are and how many run along each axis.</summary>
internal readonly record struct ProbeCascadeLayout(float Spacing, int CountX, int CountY, int CountZ)
{
    public int Total => CountX * CountY * CountZ;

    /// <summary>
    /// The slot a cell's probe lives in: each axis wraps as ((cell mod count) + count) mod count, and
    /// the slot is x + countX * (y + countY * z), so x runs fastest.
    /// </summary>
    public int Wrap(Int3 cell)
    {
        int x = (cell.X % CountX + CountX) % CountX;
        int y = (cell.Y % CountY + CountY) % CountY;
        int z = (cell.Z % CountZ + CountZ) % CountZ;
        return x + CountX * (y + CountY * z);
    }

    /// <summary>The box's minimum corner that puts the camera's cell at index count / 2 on each axis.</summary>
    public Int3 SnappedOrigin(Vector3 camera) => new(
        (int)MathF.Floor(camera.X / Spacing) - CountX / 2,
        (int)MathF.Floor(camera.Y / Spacing) - CountY / 2,
        (int)MathF.Floor(camera.Z / Spacing) - CountZ / 2);

    /// <summary>The world position of the probe in a slot of the box at <paramref name="origin"/>.</summary>
    public Vector3 Position(Int3 origin, int slot)
    {
        int sx = slot % CountX;
        int sy = slot / CountX % CountY;
        int sz = slot / (CountX * CountY);
        return new Vector3(
            CellInBox(origin.X, sx, CountX),
            CellInBox(origin.Y, sy, CountY),
            CellInBox(origin.Z, sz, CountZ)) * Spacing;
    }

    /// <summary>Whether a box at <paramref name="origin"/> holds the point one cell inside its edge; see <see cref="ProbeCascades.Finest"/>.</summary>
    public bool HoldsInside(Int3 origin, Vector3 point) =>
        point.X >= (origin.X + 1) * Spacing && point.X < (origin.X + CountX - 1) * Spacing &&
        point.Y >= (origin.Y + 1) * Spacing && point.Y < (origin.Y + CountY - 1) * Spacing &&
        point.Z >= (origin.Z + 1) * Spacing && point.Z < (origin.Z + CountZ - 1) * Spacing;

    // The one cell in [origin, origin + count) whose index wraps to the slot along this axis.
    private static int CellInBox(int origin, int slot, int count)
    {
        int offset = ((slot - origin) % count + count) % count;
        return origin + offset;
    }
}

/// <summary>
/// What a <see cref="ProbeCascades.Move"/> changed: for every cascade, which probes now sit in
/// slots that held a probe from the other side of the box, and so must be captured again. Its arrays and
/// lists belong to the cascades and are reused, so a result is only valid until the next Move.
/// </summary>
internal readonly record struct ScrollResult
{
    // Per cascade and axis, the half-open range of new cells [start, start + count).
    internal readonly Slab[] Slabs;
    private readonly Int3[] _origins;
    private readonly ProbeCascadeLayout[] _layouts;
    private readonly bool[] _teleported;
    private readonly List<int>[] _newSlots;

    internal ScrollResult(
        ProbeCascadeLayout[] layouts, Int3[] origins, Slab[] slabs, bool[] teleported, List<int>[] newSlots)
    {
        _layouts = layouts;
        _origins = origins;
        Slabs = slabs;
        _teleported = teleported;
        _newSlots = newSlots;
    }

    /// <summary>
    /// True when the whole cascade is new: the first move, or a move of a whole box width or more
    /// along any axis. Every slot is then in <see cref="NewSlots"/>.
    /// </summary>
    public bool Teleported(int cascade) => _teleported[cascade];

    /// <summary>
    /// The slots of the probes that are new in this cascade, in no particular order. The list is the cascade's
    /// own, refilled by each call, so read it before the next Move.
    /// </summary>
    public IReadOnlyList<int> NewSlots(int cascade)
    {
        if (_layouts == null || !Moved(cascade))
        {
            return Array.Empty<int>();
        }

        var slots = _newSlots[cascade];
        slots.Clear();
        var layout = _layouts[cascade];
        var origin = _origins[cascade];

        for (int k = 0; k < layout.CountZ; k++)
        {
            for (int j = 0; j < layout.CountY; j++)
            {
                for (int i = 0; i < layout.CountX; i++)
                {
                    var cell = new Int3(origin.X + i, origin.Y + j, origin.Z + k);
                    if (IsNew(cascade, cell))
                    {
                        slots.Add(layout.Wrap(cell));
                    }
                }
            }
        }

        return slots;
    }

    // Whether any of the cascade's slabs of new cells holds a cell.
    private bool Moved(int cascade) =>
        Slabs[cascade * 3].Count != 0 || Slabs[cascade * 3 + 1].Count != 0 || Slabs[cascade * 3 + 2].Count != 0;

    // Whether the cell is in one of the cascade's slabs of new cells.
    private bool IsNew(int cascade, Int3 cell) =>
        Slabs[cascade * 3].Contains(cell.X) || Slabs[cascade * 3 + 1].Contains(cell.Y) || Slabs[cascade * 3 + 2].Contains(cell.Z);

    internal readonly record struct Slab(int Start, int Count)
    {
        public bool Contains(int cell) => cell >= Start && cell < Start + Count;

        /// <summary>
        /// The cells along one axis that are in the new box and weren't in the old one, and whether the box moved
        /// its whole width or more.
        /// </summary>
        public static (Slab slab, bool teleported) Entered(int oldOrigin, int newOrigin, int count)
        {
            int delta = newOrigin - oldOrigin;
            if (delta == 0)
            {
                return (new Slab(newOrigin, 0), false);
            }

            if (Math.Abs(delta) >= count)
            {
                return (new Slab(newOrigin, count), true);
            }

            return delta > 0
                ? (new Slab(oldOrigin + count, delta), false)
                : (new Slab(newOrigin, -delta), false);
        }
    }
}

/// <summary>
/// Four nested cascades of light probes that follow the camera. Each one is a box of cells of a
/// fixed spacing (0.5, 1, 2 and 4 m), snapped to its own spacing so the camera sits in the middle
/// cell and probes keep their world positions as the box moves. A probe lives in a slot given by its
/// cell index modulo the box size (a torus), so a move only replaces the slice of probes that
/// fell off one side, and every other probe keeps its slot and its capture.
/// </summary>
internal sealed class ProbeCascades
{
    public const int Count = 4;

    private static readonly float[] Spacings = { 0.5f, 1f, 2f, 4f };

    private readonly ProbeCascadeLayout[] _layouts;
    private readonly Int3[] _origins = new Int3[Count];
    private bool _moved;

    // What Move returns when no cascade moved: no slabs, nothing teleported. Made once.
    private ScrollResult? _stayed;

    // What Move fills in when some cascade moved, reused by every such move, so crossing a cell allocates nothing
    // once the lists have grown to the largest slice.
    private readonly ScrollResult.Slab[] _slabs = new ScrollResult.Slab[Count * 3];
    private readonly bool[] _teleported = new bool[Count];
    private readonly Int3[] _movedOrigins = new Int3[Count];
    private readonly List<int>[] _newSlots = new List<int>[Count];

    public ProbeCascades(ProbeQuality quality)
    {
        _layouts = Layouts(quality);
        for (int c = 0; c < Count; c++)
        {
            _origins[c] = _layouts[c].SnappedOrigin(Vector3.Zero);
            _newSlots[c] = [];
        }
    }

    /// <summary>The four cascades' layouts, finest first. The spacings are the same at every quality.</summary>
    public static ProbeCascadeLayout[] Layouts(ProbeQuality quality)
    {
        var (x, y, z) = quality switch
        {
            ProbeQuality.Low => (16, 8, 16),
            ProbeQuality.Medium => (24, 12, 24),
            _ => (32, 16, 32)
        };

        var layouts = new ProbeCascadeLayout[Count];
        for (int c = 0; c < Count; c++)
        {
            layouts[c] = new ProbeCascadeLayout(Spacings[c], x, y, z);
        }

        return layouts;
    }

    /// <summary>One cascade's layout: its spacing and how many probes run along each axis.</summary>
    public ProbeCascadeLayout Layout(int cascade) => _layouts[cascade];

    /// <summary>The cell index of the cascade's minimum corner. Its box is cells Origin to Origin + count, less one.</summary>
    public Int3 Origin(int cascade) => _origins[cascade];

    /// <summary>
    /// Snaps each cascade so the camera's cell, floor(camera / spacing) on each axis, is the cell at
    /// index count / 2 from the origin. Returns, per cascade, the slabs of cells that entered the box.
    /// The first call reports every cascade as teleported, so the scheduler starts with every probe
    /// never captured.
    /// </summary>
    public ScrollResult Move(Vector3 camera)
    {
        // Most frames the camera stays in its cells, and that result is always the same.
        if (_moved && Unmoved(camera))
        {
            _stayed ??= new ScrollResult(
                _layouts, new Int3[Count], new ScrollResult.Slab[Count * 3], new bool[Count], _newSlots);
            return _stayed.Value;
        }

        var slabs = _slabs;
        var teleported = _teleported;
        var origins = _movedOrigins;

        for (int c = 0; c < Count; c++)
        {
            var layout = _layouts[c];
            var old = _origins[c];
            var next = layout.SnappedOrigin(camera);

            var (x, tx) = ScrollResult.Slab.Entered(old.X, next.X, layout.CountX);
            var (y, ty) = ScrollResult.Slab.Entered(old.Y, next.Y, layout.CountY);
            var (z, tz) = ScrollResult.Slab.Entered(old.Z, next.Z, layout.CountZ);
            slabs[c * 3] = x;
            slabs[c * 3 + 1] = y;
            slabs[c * 3 + 2] = z;
            teleported[c] = !_moved || tx || ty || tz;

            if (teleported[c])
            {
                // Every cell counts as new, whatever the slabs say.
                slabs[c * 3] = new ScrollResult.Slab(next.X, layout.CountX);
            }

            origins[c] = next;
            _origins[c] = next;
        }

        _moved = true;
        return new ScrollResult(_layouts, origins, slabs, teleported, _newSlots);
    }

    /// <summary>
    /// The slot a cell's probe lives in: each axis wraps as ((cell mod count) + count) mod count, and
    /// the slot is x + countX * (y + countY * z), so x runs fastest.
    /// </summary>
    public int Slot(int cascade, Int3 cell) => _layouts[cascade].Wrap(cell);

    /// <summary>The world position of the probe now in a slot: the box's cell that wraps to it, times the spacing.</summary>
    public Vector3 Position(int cascade, int slot) => _layouts[cascade].Position(_origins[cascade], slot);

    /// <summary>
    /// The finest cascade that holds the point one cell inside its edge, or -1. A cascade holds a
    /// point when, on every axis, (Origin + 1) * spacing &lt;= p &lt; (Origin + count - 1) * spacing: its
    /// box is [Origin * spacing, (Origin + count) * spacing), less one cell on each side so the
    /// trilinear lookup always has all eight probes inside the box. The shader repeats this rule.
    /// </summary>
    public int Finest(Vector3 point)
    {
        for (int c = 0; c < Count; c++)
        {
            if (_layouts[c].HoldsInside(_origins[c], point))
            {
                return c;
            }
        }

        return -1;
    }

    private bool Unmoved(Vector3 camera)
    {
        for (int c = 0; c < Count; c++)
        {
            if (_layouts[c].SnappedOrigin(camera) != _origins[c])
            {
                return false;
            }
        }

        return true;
    }
}
