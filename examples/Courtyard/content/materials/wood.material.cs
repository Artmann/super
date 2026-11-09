using System.Globalization;
using System.Numerics;
using Super.Materials;

/// <summary>
/// Old boards, six to the metre across and a metre long, each its own tone from grey to warm brown. Rings and grain
/// run along them and bend round the knots, short checks open along the grain, the weather silvers them in patches and
/// their ends are darker.
/// </summary>
/// <remarks>
/// A board is one row of the image, and each row starts half a board along from the one above. The Courtyard's script
/// lays each board, stave and plank of its model on one row, and maps the real ends of a board onto the dark end of
/// its row (see <c>plank_uv</c> in <c>art/courtyard.py</c>).
/// </remarks>
public sealed class Wood : MaterialGraph
{
    /// <summary>Boards down the image.</summary>
    public const int Boards = 6;

    /// <summary>How many knots the six boards share, 3 to the metre of board.</summary>
    public const int KnotCount = 18;

    /// <summary>
    /// The six tones a board can take, from weathered grey to warm brown. They differ in warmth but not in lightness, all
    /// within 4% of 0.129 linear luminance, so no board reads as a pale or dark stripe beside its neighbours.
    /// </summary>
    public static IReadOnlyList<string> Tones { get; } =
    [
        "#6a665e", // grey
        "#716255",
        "#785f4c",
        "#7e5d44",
        "#845b3b",
        "#8a5a34", // warm brown
    ];

    // The tone of each board, by its row: no board is within a stop of the board above or below it.
    private static readonly int[] Deal = [2, 5, 0, 3, 1, 4];

    /// <summary>
    /// How weathered the boards are, from 0 (fresh from the saw) to 1 (silvered all over). The weather lifts and greys
    /// the colour in patches, makes it rough and opens the checks.
    /// </summary>
    public float Weathering { get; init; } = 0.5f;

    /// <summary>Moves every board's tone towards the warm end (up to 1) or the grey end (down to -1). 0 keeps the six apart.</summary>
    public float Tone { get; init; }

    /// <summary>
    /// Where iron bands cross the boards, as fractions of the way along each board from its start, separated by spaces,
    /// such as <c>"0.66 0.97"</c>. Rust runs down the grain from each, towards the start. Empty, the default, has none.
    /// </summary>
    public string RustAt { get; init; } = "";

    /// <summary>The board at <paramref name="v"/>, a fraction of the image down from the top.</summary>
    public static int BoardAt(float v)
    {
        return Math.Clamp((int)MathF.Floor(v * Boards), 0, Boards - 1);
    }

    /// <summary>How far along its board a point is: 0 at the board's start, rising to 1 at its end.</summary>
    public static float AlongBoard(Vector2 uv)
    {
        var t = uv.X - 0.5f * BoardAt(uv.Y);

        return t - MathF.Floor(t);
    }

    /// <summary>The index into <see cref="Tones"/> of the board in row <paramref name="board"/>.</summary>
    public static int ToneOf(int board)
    {
        return Deal[board];
    }

    /// <summary>
    /// The knots: 1 at the heart of each, falling to 0 at its edge, 2 to 4 cm across, scattered over the boards and
    /// none in the joints.
    /// </summary>
    public static Gray Knots()
    {
        var planks = Tiles.Bricks(columns: 1, rows: Boards, gap: 0.03f, bevel: 0.08f, offset: 0.5f);

        // The radial gradient is 1 in the middle of a stamp and 0 in its corners, so its top half is a round knot.
        return Gradient.Radial().Levels(0.5f, 1f)
            .Splatter(KnotCount, sizeMin: 0.03f, sizeMax: 0.06f, rotate: false, seed: 1)
            .Multiply(planks);
    }

    public override Material Build()
    {
        if (!(Weathering >= 0f && Weathering <= 1f))
        {
            throw new ArgumentOutOfRangeException(nameof(Weathering), Weathering, FormattableString.Invariant($"Weathering must be from 0 to 1, not {Weathering}."));
        }

        if (!(Tone >= -1f && Tone <= 1f))
        {
            throw new ArgumentOutOfRangeException(nameof(Tone), Tone, FormattableString.Invariant($"Tone must be from -1 to 1, not {Tone}."));
        }

        var rustAt = ParseRustAt(RustAt);

        // The knots are made first. A node's noise is seeded by the order it is made in, so made first they come out
        // the same here as in a graph that makes nothing else, which is how WoodTests looks at them.
        var knots = Knots();
        var planks = Tiles.Bricks(columns: 1, rows: Boards, gap: 0.03f, bevel: 0.08f, offset: 0.5f);
        var ids = planks.FloodFill();

        // The grain bends round each knot: the rings and the fibres are pushed out along the slope of a soft bump.
        var knotBump = knots.Blur(0.01f);

        // Rings: a ramp repeated down the image, bent by noise stretched along the grain, shifted per board so no two
        // boards share their rings, and bent round the knots.
        var rings = Gradient.Linear(90f)
            .Transform(tile: 12)
            .Warp(Noise.Directional(scale: 6, angle: 0f, stretch: 6f, octaves: 2, seed: 2), amount: 0.006f)
            .DirectionalWarp(ids.RandomGray(seed: 3), amount: 0.4f, angle: 90f)
            .Warp(knotBump, amount: 0.0015f)
            .Curve((0f, 0f), (0.7f, 0.15f), (0.9f, 1f), (1f, 0.6f));
        var grain = Noise.Directional(scale: 96, angle: 0f, stretch: 16f, octaves: 4, seed: 4)
            .Warp(knotBump, amount: 0.0015f);

        // Checks: short splits along the grain, a few millimetres wide and a few centimetres long, from the peaks of a
        // fine, long noise, in patches that grow with the weather.
        var checks = Noise.Directional(scale: 150, angle: 0f, stretch: 24f, octaves: 2, seed: 5).Levels(0.83f, 0.88f)
            .Multiply(Noise.Perlin(scale: 7, octaves: 3, seed: 6).HistogramScan(0.25f + 0.5f * Weathering, contrast: 0.7f))
            .Multiply(planks.Levels(0.95f, 1f));

        // Silvering: the weather greys and lifts the wood in broad patches, more of it the more weathered.
        var silver = Noise.Perlin(scale: 5, octaves: 5, seed: 7).HistogramScan(Weathering, contrast: 0.55f);

        // The ends: end grain drinks the wet and dirt, so the last few centimetres of each board are darker.
        var ends = Pixel.Gray(p =>
        {
            var t = AlongBoard(p.Uv);
            var fromEnd = MathF.Min(t, 1f - t);

            return 1f - Math.Clamp((fromEnd - 0.006f) / 0.03f, 0f, 1f);
        });

        var tone = Pixel.Gray(p => Math.Clamp(ToneOf(BoardAt(p.Uv.Y)) / (float)(Tones.Count - 1) + 0.5f * Tone, 0f, 1f));
        var figure = rings.Multiply(0.6f).Add(grain.Multiply(0.4f));
        var color = tone
            .GradientMap(Ramp.Of(Tones.Select((hex, i) => (i / (float)(Tones.Count - 1), hex)).ToArray()))
            .Multiply(figure.Remap(1.14f, 0.8f))
            .Multiply(ids.RandomGray(seed: 8).Remap(0.95f, 1.04f))
            .Multiply(knots.Curve((0f, 1f), (0.25f, 0.72f), (0.6f, 0.5f), (1f, 0.4f)));

        // Silvered wood keeps its grain and a little of its board's tone, but loses most of its colour and turns paler.
        color = color.Blend(color.Hsl(saturation: -0.15f, lightness: 0.05f), mask: silver);
        color = color
            .Multiply(ends.Remap(1f, 0.62f))
            .Multiply(checks.Remap(1f, 0.45f));

        // The weather wears the soft wood between the rings away, so the rings and knots stand proud.
        var height = planks
            .Multiply(ids.RandomGray(seed: 9).Remap(0.9f, 1f))
            .Add(rings.Multiply(0.02f + 0.06f * Weathering))
            .Subtract(grain.Multiply(0.08f))
            .Add(knots.Multiply(0.04f))
            .Subtract(checks.Multiply(0.3f));

        // Planed wood is satin, silvered wood dry and rough, end grain rougher still, and knots resinous and smoother.
        var roughness = grain.Remap(0.62f, 0.8f).Add(rings.Multiply(0.05f))
            .Blend(Gray.Of(0.9f), mask: silver)
            .Blend(Gray.Of(0.94f), mask: ends)
            .Blend(Gray.Of(0.55f), mask: knots.Levels(0.2f, 0.6f).Multiply(0.7f))
            .Blend(Gray.Of(0.95f), mask: checks);

        // Rust washed down from iron bands, last, so the noise above is the same with or without it.
        if (rustAt.Count > 0)
        {
            var below = Pixel.Gray(p =>
            {
                var t = AlongBoard(p.Uv);
                var most = 0f;

                foreach (var band in rustAt)
                {
                    // How far below the band, along the board towards its start, wrapping round.
                    var down = band - t;
                    down -= MathF.Floor(down);
                    most = MathF.Max(most, MathF.Pow(1f - Math.Clamp(down / RustReach, 0f, 1f), 1.6f));
                }

                return most;
            });

            // The rust runs in streaks down the grain, some longer than others.
            var runs = Noise.Directional(scale: 40, angle: 0f, stretch: 10f, octaves: 3, seed: 10).Remap(-0.35f, 0.35f);
            var rust = below.Add(runs).Add(Noise.Perlin(scale: 16, octaves: 3, seed: 11).Remap(-0.15f, 0.15f))
                .Levels(0.35f, 0.9f)
                .Multiply(planks.Levels(0.5f, 1f));
            var rustColor = Noise.Perlin(scale: 30, octaves: 4, seed: 12).GradientMap(Ramp.Of("#4a2412", "#6e3a1a", "#7d4a24"));

            color = color.Blend(color.Multiply(rustColor.Multiply(Gray.Of(1.7f))), mask: rust.Multiply(0.85f));
            roughness = roughness.Blend(Gray.Of(0.85f), mask: rust.Multiply(0.6f));
        }

        return new Material
        {
            Width = 1f,
            Depth = 0.01f,
            Occlusion = 0.85f,
            Height = height,
            Color = color,
            Roughness = roughness,
        };
    }

    /// <summary>How far rust runs down a board from an iron band, as a fraction of the board: 20 cm.</summary>
    public const float RustReach = 0.2f;

    private static List<float> ParseRustAt(string text)
    {
        var bands = new List<float>();

        foreach (var part in (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var band) || !(band >= 0f && band <= 1f))
            {
                throw new ArgumentException($"RustAt must be numbers from 0 to 1 separated by spaces, and \"{part}\" isn't one.", nameof(RustAt));
            }

            bands.Add(band);
        }

        return bands;
    }
}
