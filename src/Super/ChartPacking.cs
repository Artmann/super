using System.Numerics;
using Rendition;

namespace Super;

/// <summary>
/// A shape's lightmap charts' rectangles and their places on a page, packed at one scale for all. Each chart is given
/// by its extent in metres.
/// </summary>
internal sealed class ChartPacking
{
    private readonly Vector2[] _extents;
    private readonly (int Width, int Height)[] _rectangles;
    private readonly (int X, int Y)[] _places;
    private readonly float _longest;
    private readonly double _area;

    public ChartPacking(Vector2[] extents)
    {
        _extents = extents;
        _rectangles = new (int Width, int Height)[extents.Length];
        _places = new (int X, int Y)[extents.Length];
        foreach (var extent in extents)
        {
            _longest = MathF.Max(_longest, MathF.Max(extent.X, extent.Y));
            _area += (double)extent.X * extent.Y;
        }
    }

    /// <summary>
    /// The most texels per metre at which every chart fits a page this many texels square, with the charts
    /// packed at that scale; null when they don't fit even at their smallest.
    /// </summary>
    public double? BestScale(int size)
    {
        // A chart's rectangle is its extent in texels rounded up plus one, so the longest
        // fits the page when it's no more than size - 1 texels long; a little less keeps
        // rounding from pushing it over.
        double most = _longest > 0f ? (size - 1.5) / _longest : 1.0;
        if (Fits(most, size))
        {
            return most;
        }

        double least = most * 1e-6;
        if (!Fits(least, size))
        {
            return null;
        }

        // Each rectangle is larger than its chart, so at the scale where the charts alone would cover the
        // page they can't fit, which starts the search close.
        double fails = _area > 0.0 ? Math.Min(most, Math.Sqrt((double)size * size / _area)) : most;
        double fits = Narrow(least, fails, size);

        // Packs again at the scale kept, since the last try may have been one that failed,
        // so rectangles and places are this scale's.
        Fits(fits, size);
        return fits;
    }

    /// <summary>Where chart <paramref name="i"/>'s corner goes so it's centred in its rectangle.</summary>
    public (double Left, double Top) Corner(int i, double fits)
    {
        var extent = _extents[i];
        return (
            _places[i].X + (_rectangles[i].Width - extent.X * fits) * 0.5,
            _places[i].Y + (_rectangles[i].Height - extent.Y * fits) * 0.5);
    }

    /// <summary>
    /// The largest scale that fits, between one that fits and one that doesn't, to a thousandth, far below a
    /// texel.
    /// </summary>
    private double Narrow(double fits, double fails, int size)
    {
        while (fails / fits >= 1.001)
        {
            double middle = (fits + fails) * 0.5;
            if (Fits(middle, size))
            {
                fits = middle;
            }
            else
            {
                fails = middle;
            }
        }

        return fits;
    }

    // Sizes each chart's rectangle at this many texels per metre, its extent rounded up
    // plus one, so at least one texel a side, and packs them; whether they all fit.
    private bool Fits(double texelsPerMetre, int size)
    {
        for (int i = 0; i < _extents.Length; i++)
        {
            var extent = _extents[i];
            _rectangles[i] = (Side(extent.X * texelsPerMetre), Side(extent.Y * texelsPerMetre));
        }

        return SkylinePacker.TryPack(_rectangles, size, size, LightmapUnwrap.Gutter, _places);
    }

    private static int Side(double texels) => (int)Math.Ceiling(texels) + 1;
}
