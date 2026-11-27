using System.Numerics;

namespace Super.Ui;

/// <summary>One slice of a <see cref="RadialMenu"/>: what it says, and what picking it does on the next tick.</summary>
public sealed record RadialItem(string Label, Action? Action = null)
{
    public IconName? Icon { get; init; }
}

/// <summary>
/// A wheel of choices, such as weapons, emotes or quick items, picked by pointing. The left stick or the mouse aims
/// at a slice, left and right step round it, and South, Enter or a click picks the one aimed at. The first slice is
/// at the top and the rest go round clockwise. It takes focus as it appears, so show it while a button is held or
/// in a popup, which Escape and East close.
/// </summary>
public sealed record RadialMenu(string Label, IReadOnlyList<RadialItem> Items) : UiView
{
    /// <summary>How wide the wheel is.</summary>
    public float Size { get; init; } = 280f;

    /// <summary>
    /// Whether it takes focus as it appears, so the stick and keys steer it at once. Turn it off for a wheel that
    /// sits among other controls, which focus reaches like any other.
    /// </summary>
    public bool TakeFocus { get; init; } = true;

    public override UiView Body
    {
        get
        {
            var environment = Environment;
            var aimed = UseState(-1);
            int at = aimed.Value;
            var wheel = new ZStack();
            float band = MathF.Round(Size * 0.3f);
            // Every ring first, so no slice covers the name of the one before it.
            for (int i = 0; i < Items.Count; i++)
            {
                wheel.Add(Ring(i, i == at, band, environment));
            }

            for (int i = 0; i < Items.Count; i++)
            {
                wheel.Add(Face(i, i == at, band, environment));
            }

            wheel.Add(Centre(at, environment));
            var aiming = new RadialModifier(wheel.Frame(Size, Size), Items.Count, aimed, 1f - 2f * band / Size);
            var items = Items;
            var click = new ClickModifier(aiming, () => Pick(items, aimed.Value), null);
            var meaning = new SemanticsModifier(click, new UiSemantics(UiRole.Picker, Label, at >= 0 && at < Items.Count ? Items[at].Label : null));
            int count = Items.Count;
            var keys = new FocusKeys
            {
                Activate = () => Pick(items, aimed.Value),
                Adjust = by => aimed.Value = Step(aimed.Value, by, count),
            };
            var focusable = new FocusModifier(meaning, keys);
            return TakeFocus ? focusable.DefaultFocus() : focusable;
        }
    }

    /// <summary>The slice beside this one, going round, or the first or last when nothing is aimed at yet.</summary>
    private static int Step(int at, int by, int count)
    {
        if (count == 0)
        {
            return -1;
        }

        if (at < 0)
        {
            return by > 0 ? 0 : count - 1;
        }

        return ((at + by) % count + count) % count;
    }

    private static void Pick(IReadOnlyList<RadialItem> items, int at)
    {
        if (at >= 0 && at < items.Count)
        {
            items[at].Action?.Invoke();
        }
    }

    /// <summary>One slice of the ring, centred on its own direction.</summary>
    private UiView Ring(int i, bool aimed, float band, UiEnvironment environment)
    {
        var colors = environment.Theme.Colors;
        float share = 1f / Items.Count;
        // A gap of a few units between slices at their inner edge, so each reads as its own.
        float gap = Math.Min(2f / (MathF.Tau * (Size / 2f - band)), share * 0.1f);
        return new Arc(share * (i - 0.5f) + gap, share - 2f * gap, band) { FlatEnds = true }
            .Fill(aimed ? colors.Primary : colors.Muted);
    }

    /// <summary>A slice's icon and name, in the middle of its ring.</summary>
    private UiView Face(int i, bool aimed, float band, UiEnvironment environment)
    {
        var colors = environment.Theme.Colors;
        float angle = i * MathF.Tau / Items.Count;
        float reach = Size / 2f - band / 2f;
        var item = Items[i];
        var text = aimed ? colors.PrimaryForeground : colors.Foreground;
        var face = new VStack(HorizontalAlignment.Center, 2f)
        {
            item.Icon is { } icon ? new Icon(icon).FontSize(environment.Metrics.FontSize * 1.5f) : null,
            new Text(item.Label).FontSize(environment.Metrics.FontSize * 0.85f).LineLimit(1),
        }.Foreground(text);
        return new SemanticsModifier(face, new UiSemantics(UiRole.Option, item.Label, Checked: aimed))
            .Offset(MathF.Sin(angle) * reach, -MathF.Cos(angle) * reach);
    }

    /// <summary>The middle of the wheel, which names the slice aimed at.</summary>
    private UiView Centre(int at, UiEnvironment environment)
    {
        var colors = environment.Theme.Colors;
        string name = at >= 0 && at < Items.Count ? Items[at].Label : Label;
        float side = Size * 0.36f;
        return new Text(name)
            .FontSize(environment.Metrics.FontSize * 1.15f)
            .Weight(FontWeight.SemiBold)
            .LineLimit(1)
            .Frame(side, side)
            .Background(colors.Background with { A = 0.9f })
            .CornerRadius(side / 2f);
    }
}

/// <summary>
/// A wheel of <paramref name="Count"/> slices whose aim is <paramref name="Aimed"/>, with a hole in the middle
/// <paramref name="Hole"/> of its width across, where the mouse aims at nothing.
/// </summary>
internal sealed record RadialModifier(UiView Child, int Count, State<int> Aimed, float Hole) : Modifier(Child);

/// <summary>Aims a <see cref="RadialMenu"/> with the mouse or the left stick.</summary>
internal static class UiRadial
{
    private const float StickPush = 0.5f;

    /// <summary>
    /// Aims the wheel in the focused view along the stick, while it's pushed. Returns whether a wheel has focus, so
    /// the stick steers it and doesn't move focus.
    /// </summary>
    public static bool Steer(UiNode? focused, Vector2 stick)
    {
        if (focused is null || UiFocusStops.Below<RadialModifier>(focused) is not { View: RadialModifier wheel } node)
        {
            return false;
        }

        // Let go, the stick springs back to the middle, and the aim stays where it was.
        if (stick.Length() >= StickPush)
        {
            Aim(node, wheel, MathF.Atan2(stick.X, stick.Y));
        }

        return true;
    }

    /// <summary>Aims the wheel under the cursor at the slice it's over, or at nothing in the hole in the middle.</summary>
    public static void Point(UiNode? hit, Vector2 at)
    {
        if (UiPointerSearch.Find<RadialModifier>(hit) is not { View: RadialModifier wheel } node)
        {
            return;
        }

        var frame = node.Frame;
        var from = at - new Vector2(frame.X + frame.Width / 2f, frame.Y + frame.Height / 2f);
        if (from.Length() < wheel.Hole * frame.Width / 2f)
        {
            wheel.Aimed.Value = -1;
            return;
        }

        // The screen's y goes down, so up is negative.
        Aim(node, wheel, MathF.Atan2(from.X, -from.Y));
    }

    /// <summary>The slice at this angle, a turn clockwise from the top, with each slice centred on its own direction.</summary>
    public static int Slice(float angle, int count) =>
        count == 0 ? -1 : ((int)MathF.Floor(angle / MathF.Tau * count + 0.5f) % count + count) % count;

    private static void Aim(UiNode node, RadialModifier wheel, float angle)
    {
        int slice = Slice(angle, wheel.Count);
        if (slice != wheel.Aimed.Value)
        {
            wheel.Aimed.Value = slice;
            node.Tree.Cues.Raise(node, UiCue.Step);
        }
    }
}
