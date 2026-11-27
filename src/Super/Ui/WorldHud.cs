using System.Numerics;

namespace Super.Ui;

/// <summary>
/// Text that pops in, rises and fades, for damage numbers, pickups and points. It runs once when it appears, so give
/// each one its own id; <see cref="FloatingTexts"/> does that for you. Inside <c>.Animations(false)</c> it stays put,
/// fully shown.
/// </summary>
public sealed record FloatingText(string Text) : UiView
{
    // The share of its time it takes to pop in, and the share at the end it takes to fade.
    private const float PopShare = 0.15f;
    private const float FadeShare = 0.4f;

    // How much bigger it starts.
    private const float Pop = 0.4f;

    /// <summary>Its colour, or the text colour around it.</summary>
    public Color? Color { get; init; }

    /// <summary>How far it rises, in interface units.</summary>
    public float Rise { get; init; } = 40f;

    /// <summary>How long it shows, in seconds.</summary>
    public float Seconds { get; init; } = 1f;

    public override UiView Body
    {
        get
        {
            float done = Environment.Still ? 0f : UseAnimation(1f, Seconds, Easing.Out, from: 0f);
            float shown = done < 1f - FadeShare ? 1f : (1f - done) / FadeShare;
            float size = 1f + Pop * (1f - MathF.Min(done / PopShare, 1f));
            UiView text = new Text(Text).FontSize(22f).Bold();
            if (Color is { } color)
            {
                text = text.Foreground(color);
            }

            return text.Scale(size).Opacity(shown).Offset(0f, -Rise * done);
        }
    }
}

/// <summary>
/// Floating text over points in the world, held by the game: add one when something is hit, age them each update,
/// and show <see cref="View"/> in a full-screen layer. Each drops out once it has had its time.
/// </summary>
public sealed class FloatingTexts
{
    private readonly List<(long Id, string Text, Vector3 Point, Color? Color, float Age)> _shown = [];
    private long _next;

    /// <summary>How long each shows, in seconds.</summary>
    public float Seconds { get; init; } = 1f;

    /// <summary>How many are showing.</summary>
    public int Count => _shown.Count;

    /// <summary>Shows text rising from a point in the world.</summary>
    public void Add(string text, Vector3 point, Color? color = null) => _shown.Add((_next++, text, point, color, 0f));

    /// <summary>Ages them all, and drops those that have had their time.</summary>
    public void Update(float seconds)
    {
        for (int i = 0; i < _shown.Count; i++)
        {
            _shown[i] = _shown[i] with { Age = _shown[i].Age + seconds };
        }

        _shown.RemoveAll(shown => shown.Age >= Seconds);
    }

    /// <summary>All of them, each over its point.</summary>
    public UiView View
    {
        get
        {
            var stack = new ZStack();
            foreach (var (id, text, point, color, _) in _shown)
            {
                stack.Add(new FloatingText(text) { Color = color, Seconds = Seconds }.AtWorld(point).Id(("floating", id)));
            }

            return stack;
        }
    }
}

/// <summary>A point on a <see cref="Compass"/>: a quest, a friend, a place. Its bearing is in radians, as the heading's.</summary>
public readonly record struct CompassMark(float Bearing, Color Color, string? Label = null);

/// <summary>
/// A strip across the top of the screen showing which way the player faces, with the points of the compass, ticks
/// and marks for what's around. The <paramref name="Heading"/> is in radians, clockwise from north, which is -Z,
/// so east is +X. <see cref="HeadingOf"/> and <see cref="BearingTo"/> work them out.
/// </summary>
public sealed record Compass(float Heading) : UiView
{
    private const float Height = 40f;
    private const int TickDegrees = 15;
    private const float MarkSize = 10f;
    private static readonly string[] Points = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];

    /// <summary>How wide it is, in interface units.</summary>
    public float Width { get; init; } = 360f;

    /// <summary>How much of the circle it shows across, in radians.</summary>
    public float Across { get; init; } = MathF.PI;

    /// <summary>What's marked on it. A mark out past the edge waits at the edge.</summary>
    public IReadOnlyList<CompassMark> Marks { get; init; } = [];

    /// <summary>Which way a camera faces, in radians clockwise from north.</summary>
    public static float HeadingOf(in Camera camera) => BearingTo(camera.Position, camera.Target);

    /// <summary>Which way a point is from another, in radians clockwise from north, on the ground.</summary>
    public static float BearingTo(Vector3 from, Vector3 to) => MathF.Atan2(to.X - from.X, -(to.Z - from.Z));

    public override UiView Body
    {
        get
        {
            var colors = Environment.Theme.Colors;
            var strip = new ZStack(Alignment.Center);
            for (int degrees = 0; degrees < 360; degrees += TickDegrees)
            {
                if (Along(degrees * MathF.PI / 180f) is { } x)
                {
                    strip.Add(Tick(degrees).Opacity(Faded(x)).Offset(x, 0f));
                }
            }

            foreach (var mark in Marks)
            {
                float x = Math.Clamp(Unclamped(mark.Bearing), -Width / 2f + MarkSize, Width / 2f - MarkSize);
                strip.Add(Mark(mark).Offset(x, -Height / 2f + MarkSize / 2f + 3f));
            }

            strip.Add(new Rectangle().Fill(colors.Primary).Frame(width: 2f, height: Height));
            return strip
                .Frame(width: Width, height: Height)
                .Background(colors.Surface.Mix(colors.Background, 0.5f) with { A = 0.75f })
                .CornerRadius(Height / 2f)
                .Border(1f, colors.Border);
        }
    }

    // Where a bearing is across the strip from the middle, or null when it's off the strip.
    private float? Along(float bearing)
    {
        float x = Unclamped(bearing);
        return MathF.Abs(x) <= Width / 2f - 12f ? x : null;
    }

    private float Unclamped(float bearing) => MathF.IEEERemainder(bearing - Heading, MathF.Tau) / Across * Width;

    // The ticks fade towards the ends.
    private float Faded(float x)
    {
        float share = x / (Width / 2f);
        return 1f - share * share * 0.8f;
    }

    private static UiView Tick(int degrees)
    {
        if (degrees % 45 != 0)
        {
            return new Rectangle().Frame(width: 1f, height: 6f).Foreground(Tone.Muted);
        }

        var point = new Text(Points[degrees / 45]).TextStyle(degrees % 90 == 0 ? TextStyle.Headline : TextStyle.Caption);
        return degrees % 90 == 0 ? point : point.Foreground(Tone.Muted);
    }

    private static UiView Mark(CompassMark mark)
    {
        UiView dot = new Circle().Fill(mark.Color).Frame(width: MarkSize, height: MarkSize);
        return mark.Label is { } label
            ? new VStack(HorizontalAlignment.Center, 1f) { dot, new Text(label).TextStyle(TextStyle.Caption) }.Offset(0f, MarkSize / 2f)
            : dot;
    }
}

/// <summary>A point on a <see cref="MinimapFrame"/>, from -1 to 1 across and down. One past the rim waits on it.</summary>
public readonly record struct MinimapMark(Vector2 At, Color Color);

/// <summary>
/// A round frame for a minimap: the game's map inside, clipped to a circle, with a ring, north at the top, the
/// player in the middle facing <see cref="Heading"/>, and marks. Pan the map with <c>.Offset</c> to follow the player.
/// </summary>
public sealed record MinimapFrame(UiView Map) : UiView
{
    private const float MarkSize = 8f;

    /// <summary>How wide it is, in interface units.</summary>
    public float Size { get; init; } = 160f;

    /// <summary>Which way the player faces, in radians clockwise from north, as <see cref="Compass"/> takes.</summary>
    public float Heading { get; init; }

    /// <summary>What's marked on it.</summary>
    public IReadOnlyList<MinimapMark> Marks { get; init; } = [];

    public override UiView Body
    {
        get
        {
            var colors = Environment.Theme.Colors;
            float reach = Size / 2f - MarkSize;
            var inside = new ZStack(Alignment.Center)
            {
                Map.Frame(width: Size, height: Size),
                new Arc(Heading / MathF.Tau - 0.08f, 0.16f, Size * 0.18f) { FlatEnds = true }
                    .Fill(colors.Primary with { A = 0.35f })
                    .Frame(width: Size * 0.36f, height: Size * 0.36f),
                new Circle().Fill(colors.Primary).Stroke(colors.Background, 2f).Frame(width: 12f, height: 12f),
            };

            foreach (var mark in Marks)
            {
                var at = mark.At.LengthSquared() > 1f ? Vector2.Normalize(mark.At) : mark.At;
                inside.Add(new Circle().Fill(mark.Color).Frame(width: MarkSize, height: MarkSize).Offset(at.X * reach, at.Y * reach));
            }

            return inside
                .Frame(width: Size, height: Size)
                .Background(colors.Surface)
                .CornerRadius(Size / 2f)
                .Border(3f, colors.Border)
                .Overlay(new Text("N").TextStyle(TextStyle.Caption).Bold().Padding(4f, 0f, 0f, 0f), Alignment.Top);
        }
    }
}
