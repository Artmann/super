using System.Numerics;

namespace Rendition.Passes;

/// <summary>A part of the screen in normalised device coordinates, -1 to 1 across and down.</summary>
internal readonly record struct ScreenRect(float MinX, float MinY, float MaxX, float MaxY)
{
    public static readonly ScreenRect Empty = new(float.MaxValue, float.MaxValue, float.MinValue, float.MinValue);
    public static readonly ScreenRect Full = new(-1f, -1f, 1f, 1f);

    public bool IsEmpty => MinX > MaxX || MinY > MaxY;

    public ScreenRect Union(ScreenRect other) => new(
        MathF.Min(MinX, other.MinX), MathF.Min(MinY, other.MinY), MathF.Max(MaxX, other.MaxX), MathF.Max(MaxY, other.MaxY));

    public bool Overlaps(ScreenRect other) =>
        MinX <= other.MaxX && other.MinX <= MaxX
        && MinY <= other.MaxY && other.MinY <= MaxY;

    /// <summary>Where a box in the world lands on screen, or all of it when the box reaches behind the camera.</summary>
    public static ScreenRect OfBox(Vector3 min, Vector3 max, in Matrix4x4 viewProjection)
    {
        var rect = Empty;
        for (int corner = 0; corner < 8; corner++)
        {
            var point = new Vector3(
                (corner & 1) != 0 ? max.X : min.X,
                (corner & 2) != 0 ? max.Y : min.Y,
                (corner & 4) != 0 ? max.Z : min.Z);
            var clip = Vector4.Transform(new Vector4(point, 1f), viewProjection);
            if (clip.W <= 1e-5f)
            {
                return Full;
            }

            float x = clip.X / clip.W;
            float y = clip.Y / clip.W;
            rect = rect.Union(new ScreenRect(x, y, x, y));
        }

        return rect;
    }
}

/// <summary>A piece of glass to draw: which draw it is, how far away its middle is along the view, and where it lands on screen.</summary>
internal readonly record struct GlassItem(int Draw, float Depth, ScreenRect Rect, bool CanRefract);

internal enum TransparentStepKind
{
    /// <summary>Particles <see cref="TransparentStep.First"/> to First + Count of the sorted list.</summary>
    Particles,

    /// <summary>Copies the picture so far, for glass after it to see through.</summary>
    Copy,

    /// <summary>The draw at <see cref="TransparentStep.First"/>, bending what's behind it when <see cref="TransparentStep.Refracts"/>.</summary>
    Glass,
}

internal readonly record struct TransparentStep(TransparentStepKind Kind, int First, int Count = 0, bool Refracts = false);

/// <summary>
/// Puts glass and particles in one order, furthest first, so each blends over what's
/// behind it. Glass sees the scene through a copy of the picture, so the copy is made
/// again before a pane that something drawn since the last copy overlaps. A frame makes
/// at most <see cref="MaxCopies"/>; glass after that blends without bending.
/// </summary>
internal static class TransparentPlan
{
    public const int MaxCopies = 4;

    /// <summary>
    /// Writes the steps into <paramref name="steps"/>. <paramref name="glass"/> is sorted in
    /// place, furthest first. <paramref name="particles"/> must already be furthest first.
    /// </summary>
    public static void Build(
        Span<GlassItem> glass,
        ReadOnlySpan<Particle> particles,
        Vector3 eye,
        Vector3 forward,
        in Matrix4x4 viewProjection,
        List<TransparentStep> steps)
    {
        steps.Clear();
        glass.Sort(FurthestFirst);

        int copies = 0;
        int drawnParticles = 0;
        var sinceCopy = ScreenRect.Empty;
        foreach (var item in glass)
        {
            int first = drawnParticles;
            while (drawnParticles < particles.Length && Depth(particles[drawnParticles], eye, forward) > item.Depth)
            {
                drawnParticles++;
            }

            if (drawnParticles > first)
            {
                steps.Add(new TransparentStep(TransparentStepKind.Particles, first, drawnParticles - first));
                sinceCopy = sinceCopy.Union(Bounds(particles[first..drawnParticles], viewProjection));
            }

            bool refracts = false;
            if (item.CanRefract)
            {
                if (copies == 0 || (sinceCopy.Overlaps(item.Rect) && copies < MaxCopies))
                {
                    steps.Add(new TransparentStep(TransparentStepKind.Copy, 0));
                    copies++;
                    sinceCopy = ScreenRect.Empty;
                    refracts = true;
                }
                else
                {
                    // The copy is still right here, unless something drawn since covers this pane.
                    refracts = !sinceCopy.Overlaps(item.Rect);
                }
            }

            steps.Add(new TransparentStep(TransparentStepKind.Glass, item.Draw, Refracts: refracts));
            sinceCopy = sinceCopy.Union(item.Rect);
        }

        if (drawnParticles < particles.Length)
        {
            steps.Add(new TransparentStep(TransparentStepKind.Particles, drawnParticles, particles.Length - drawnParticles));
        }
    }

    /// <summary>How far a point is from the eye along the view, as particles are sorted.</summary>
    public static float Depth(Vector3 point, Vector3 eye, Vector3 forward) => Vector3.Dot(point - eye, forward);

    private static float Depth(in Particle particle, Vector3 eye, Vector3 forward) => Depth(particle.Position, eye, forward);

    private static ScreenRect Bounds(ReadOnlySpan<Particle> particles, in Matrix4x4 viewProjection)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (ref readonly var particle in particles)
        {
            var reach = new Vector3(particle.Size);
            min = Vector3.Min(min, particle.Position - reach);
            max = Vector3.Max(max, particle.Position + reach);
        }

        return ScreenRect.OfBox(min, max, viewProjection);
    }

    private static int FurthestFirst(GlassItem a, GlassItem b)
    {
        int compared = b.Depth.CompareTo(a.Depth);
        return compared != 0 ? compared : a.Draw.CompareTo(b.Draw);
    }
}
