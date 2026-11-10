using System.Numerics;
using Super.Materials;

/// <summary>
/// Old window glass: faintly green and rippled, each pane its own sheet, with dust packed into the corners of the
/// panes and along their foot, rain streaks and specks, and one pane cracked across.
/// </summary>
public sealed class Glass : MaterialGraph
{
    // The window's panes are 0.617 m wide and 0.461 m high between the middles of the glazing bars. Six by eight
    // panes 3.7 m across come within 2 mm a pane of that, so the graph is that wide and its grid lines up with the
    // bars of the whole window without repeating. The model's UVs are in metres from the origin, so the grid is moved
    // to put a line on the frame's inner edge, 1.475 m across and 1.245 m up.
    private const int Columns = 6;
    private const int Rows = 8;
    private static readonly Vector2 GridOffset = new(0.065315f, 0.038514f);

    // The cracked pane, the right-hand one in the third row down, as the corners of its box in the image.
    private static readonly Vector2 CrackedMin = new(0.7320f, 0.2885f);
    private static readonly Vector2 CrackedMax = new(0.8986f, 0.4135f);

    public override Material Build()
    {
        // The panes, each found as an island, with a 0 to 1 ramp across and up each one.
        var panes = Tiles.Grid(Columns, Rows, gap: 0.03f, bevel: 0f).Transform(offset: GridOffset).FloodFill(threshold: 0.5f);
        var across = panes.ToGradient(0f);
        var up = panes.ToGradient(90f);

        // Old glass was drawn by hand, so it has gentle ripples running across it that bend what it reflects. Each
        // pane was cut from a different sheet, so neighbours don't carry the same ripples on: half the panes have one
        // set and half another.
        var rippleA = Noise.Directional(scale: 37, angle: 0f, stretch: 4f, octaves: 2, seed: 1);
        var rippleB = Noise.Directional(scale: 37, angle: 0f, stretch: 4f, octaves: 2, seed: 2);
        var ripples = rippleA.Blend(rippleB, mask: panes.RandomGray(seed: 3).Levels(0.49f, 0.51f));

        // Dust packs into each pane's corners, against the putty, and settles along its foot. The corners come from a
        // radial gradient laid over each pane by its own ramps and turned inside out, ragged at the edge.
        var corners = Pixel.Gray(p => p.SampleGray(2, new Vector2(p.Gray(0), 1f - p.Gray(1))), across, up, Gradient.Radial().Invert());
        var ragged = Noise.Perlin(scale: 60, octaves: 3, seed: 4).Remap(-0.12f, 0.12f);
        var packed = corners.Add(ragged).Levels(0.74f, 0.96f)
            .Max(up.Invert().Add(ragged).Levels(0.9f, 0.99f).Multiply(0.35f));

        // Grime over the rest: broad dusty patches, rain that ran down and dried, and specks of dirt.
        var dust = Noise.Perlin(scale: 11, octaves: 5, seed: 5).Levels(0.55f, 0.85f);
        var streaks = Noise.Directional(scale: 89, angle: 90f, stretch: 8f, octaves: 3, seed: 6)
            .Levels(0.55f, 0.8f)
            .Multiply(Noise.Perlin(scale: 15, octaves: 2, seed: 7).Levels(0.4f, 0.7f));
        var specks = Noise.White(seed: 8).Blur(0.0005f).Levels(0.7f, 0.74f);
        var grime = dust.Multiply(0.45f).Max(streaks.Multiply(0.5f)).Max(specks).Max(packed);

        // One pane has cracked from its lower left to the top, with a short branch, drawn as a hairline and grown a
        // little with Distance for the stressed glass round it. One side of the crack has sagged a fraction, so it
        // reflects at a slightly different angle.
        var hairline = Pixel.Gray(p => Crack(p.Uv) < 0.00035f ? 1f : 0f);
        var crack = hairline.Distance(0.0025f);
        var sagged = Pixel.Gray(p =>
        {
            var local = (p.Uv - CrackedMin) / (CrackedMax - CrackedMin);
            if (local.X < 0f || local.Y < 0f || local.X > 1f || local.Y > 1f)
            {
                return 0f;
            }

            // Right of the crack's main run, the piece is tipped forward towards its right edge.
            float side = Side(local);
            return side > 0f ? 0.5f * local.X : 0f;
        });

        return new Material
        {
            Width = 3.7f,
            Depth = 0.001f,
            Height = ripples.Multiply(0.7f).Add(grime.Multiply(0.15f)).Add(sagged).Subtract(crack.Multiply(0.2f)),
            // Clean glass is a faint green tint over the dark room; the grime is a dry brown-grey, and the crack's
            // broken faces catch the light.
            Color = grime.GradientMap(Ramp.Of("#2e3a35", "#6f6758", "#7d7464"))
                .Blend(Rgb.Of("#a3aca7"), mask: crack.Levels(0.6f, 1f)),
            // Clean glass is clear and smooth, and the grime clouds it and dulls the reflection.
            Opacity = grime.Remap(0.05f, 0.8f).Max(crack.Levels(0.6f, 1f).Multiply(0.6f)),
            Roughness = grime.Remap(0.05f, 0.6f).Max(crack.Levels(0.3f, 1f).Multiply(0.35f)),
        };
    }

    // The crack's run across its pane, from near the lower left corner up to the top edge, in the pane's own 0 to 1
    // box, with y down the image as the image's is.
    private static readonly Vector2[] Run =
    [
        new(-0.02f, 0.82f), new(0.14f, 0.71f), new(0.22f, 0.73f), new(0.37f, 0.55f), new(0.45f, 0.52f),
        new(0.58f, 0.33f), new(0.63f, 0.31f), new(0.74f, 0.12f), new(0.79f, -0.02f),
    ];

    // A branch splitting off the run towards the right edge.
    private static readonly Vector2[] Branch = [new(0.45f, 0.52f), new(0.6f, 0.5f), new(0.71f, 0.56f), new(0.86f, 0.53f)];

    /// <summary>How far <paramref name="uv"/> is from the crack, in fractions of the image. Far away outside the cracked pane.</summary>
    private static float Crack(Vector2 uv)
    {
        var size = CrackedMax - CrackedMin;
        if (uv.X < CrackedMin.X || uv.Y < CrackedMin.Y || uv.X > CrackedMax.X || uv.Y > CrackedMax.Y)
        {
            return 1f;
        }

        float nearest = MathF.Min(Nearest(uv, Run, size), Nearest(uv, Branch, size));
        return nearest;
    }

    private static float Nearest(Vector2 uv, Vector2[] points, Vector2 size)
    {
        float best = float.MaxValue;
        for (int i = 0; i + 1 < points.Length; i++)
        {
            var a = CrackedMin + points[i] * size;
            var b = CrackedMin + points[i + 1] * size;
            var ab = b - a;
            float t = Math.Clamp(Vector2.Dot(uv - a, ab) / ab.LengthSquared(), 0f, 1f);
            best = MathF.Min(best, (a + ab * t - uv).Length());
        }

        return best;
    }

    /// <summary>Above 0 on the right of the crack's main run, in the pane's own box.</summary>
    private static float Side(Vector2 local)
    {
        // Find the run's x at this height, walking its points from the bottom up.
        for (int i = 0; i + 1 < Run.Length; i++)
        {
            var a = Run[i];
            var b = Run[i + 1];
            if (local.Y <= a.Y && local.Y >= b.Y)
            {
                float t = (local.Y - a.Y) / (b.Y - a.Y);
                return local.X - (a.X + (b.X - a.X) * t);
            }
        }

        return -1f;
    }
}
