using System.Numerics;

namespace Super.Ui;

/// <summary>
/// Popups that hang off a view: a popover, a sheet, an alert, a confirmation dialog, a context menu, and
/// toasts. Each lives where it's declared, so it reads the state round it and takes its theme, and the
/// tree shows it above everything else while it's presented. A popup that's open takes the pointer, and
/// Tab, the arrows and the gamepad stay inside it until it closes. Escape and the gamepad's East button
/// close it, and so does a click outside one that isn't an alert.
/// </summary>
public static class PopupExtensions
{
    /// <summary>
    /// Shows <paramref name="content"/> in a raised card under this view while <paramref name="isPresented"/>
    /// is true, or above it when there's no room below. A click outside, Escape or the gamepad's East button
    /// call <paramref name="onDismiss"/>, on the tick, and the game sets <paramref name="isPresented"/> back.
    /// </summary>
    public static UiView Popover(this UiView view, bool isPresented, Action onDismiss, UiView content) =>
        new PopupModifier(view, isPresented ? new LayerView(new PopupCard(content, 12f), LayerPlace.Below, onDismiss) { Light = true } : null);

    /// <summary>
    /// Shows <paramref name="content"/> in a card in the middle of the screen, over a scrim that dims the rest,
    /// while <paramref name="isPresented"/> is true. A click on the scrim, Escape or the gamepad's East button
    /// call <paramref name="onDismiss"/>. The card is between 320 and 560 units wide.
    /// </summary>
    public static UiView Sheet(this UiView view, bool isPresented, Action onDismiss, UiView content) =>
        new PopupModifier(view, isPresented
            ? new LayerView(new PopupCard(content, 24f, MinWidth: 320f, MaxWidth: 560f), LayerPlace.Center, onDismiss) { Scrim = true, Light = true }
            : null);

    /// <summary>
    /// Asks the player something that needs an answer, in the middle of the screen over a scrim, while
    /// <paramref name="isPresented"/> is true. Each button runs its action and then <paramref name="onDismiss"/>.
    /// A click outside does nothing. Escape and the gamepad's East button press the cancel button, or the
    /// only button. With no buttons it has an OK.
    /// </summary>
    public static UiView Alert(this UiView view, bool isPresented, Action onDismiss, Dialog alert) =>
        new PopupModifier(view, isPresented
            ? new LayerView(new DialogCard(alert, onDismiss, Stacked: false), LayerPlace.Center, alert.Cancel(onDismiss)) { Scrim = true }
            : null);

    /// <summary>
    /// Offers a choice of actions at the bottom of the screen, one under the other with cancel apart, over a
    /// scrim, while <paramref name="isPresented"/> is true. Each button runs its action and then
    /// <paramref name="onDismiss"/>. A click outside, Escape and the gamepad's East button cancel it.
    /// </summary>
    public static UiView ConfirmationDialog(this UiView view, bool isPresented, Action onDismiss, Dialog dialog) =>
        new PopupModifier(view, isPresented
            ? new LayerView(new DialogCard(dialog, onDismiss, Stacked: true), LayerPlace.Bottom, dialog.Cancel(onDismiss) ?? onDismiss) { Scrim = true, Light = true }
            : null);

    /// <summary>
    /// Opens a menu of <paramref name="items"/> where the player right-clicks this view, or holds a finger on
    /// it. Picking an item closes the menu and runs its action on the tick.
    /// </summary>
    public static UiView ContextMenu(this UiView view, IReadOnlyList<MenuItem> items) => new ContextMenuView(view, items);

    /// <summary>
    /// Shows the toasts in <paramref name="toasts"/> in the bottom corner of the screen, newest at the bottom,
    /// over this view. Put it on the root of the interface, and call <see cref="Super.Ui.Toasts.Show(Toast, float)"/>.
    /// </summary>
    public static UiView Toasts(this UiView view, Toasts toasts) => new ToastsView(view, toasts);
}

/// <summary>Where a popup goes on the screen.</summary>
internal enum LayerPlace
{
    /// <summary>Under the view it hangs off, lined up with its leading edge, or above it when there's no room below.</summary>
    Below,

    /// <summary>With its top leading corner at a point, or flipped round it at the screen's edges.</summary>
    At,

    Center,

    /// <summary>Across the middle of the bottom edge.</summary>
    Bottom,

    /// <summary>The bottom trailing corner.</summary>
    Corner,
}

/// <summary>
/// A popup, laid out on the whole screen above the tree: a backdrop that takes the pointer everywhere else,
/// and its content. The tree draws it after everything, not in its place. One that doesn't trap, like the
/// toasts, has no backdrop, and the pointer goes past it.
/// </summary>
internal sealed record LayerView(UiView Content, LayerPlace Spot, Action? Dismiss, bool Traps = true) : PrimitiveView
{
    // How near the screen's edges a popup comes, and how far it stands off the view it hangs off.
    private const float Margin = 12f;
    private const float Gap = 6f;

    /// <summary>Where it opens, in interface units, for <see cref="LayerPlace.At"/>.</summary>
    public Vector2 Point { get; init; }

    /// <summary>Whether the backdrop dims what's under it.</summary>
    public bool Scrim { get; init; }

    /// <summary>Whether a click outside calls <see cref="Dismiss"/>.</summary>
    public bool Light { get; init; }

    /// <summary>The backdrop, which fades in, and the content, which eases in from the way it opens.</summary>
    internal override IReadOnlyList<UiView?> Children =>
        [Traps ? new Backdrop(Scrim, Light ? Dismiss : null).Transition(Entrance.Fade, Fade) : null, Spot == LayerPlace.Corner ? Content : Content.Transition(Arrival, Fade)];

    // How long a popup takes to come in, in seconds.
    private const float Fade = 0.15f;

    /// <summary>A menu drops from its button, a sheet rises from the bottom, and a dialog fades in where it is.</summary>
    private Entrance Arrival => Spot switch
    {
        LayerPlace.Below or LayerPlace.At => Entrance.Drop,
        LayerPlace.Bottom => Entrance.Rise,
        _ => Entrance.Fade,
    };

    /// <summary>The pointer goes past it in the tree. The tree's layers ask it with <see cref="HitInside"/>.</summary>
    internal override bool PassesPointer => true;

    internal override UiSize Measure(UiNode node, Proposal proposal) => default;

    /// <summary>What the pointer touches in this popup: its content, then the backdrop.</summary>
    public static UiNode? HitInside(UiNode node, float x, float y) =>
        node.Children[1]?.Hit(x, y) ?? node.Children[0]?.Hit(x, y);

    internal override void Place(UiNode node, UiRect frame)
    {
        node.Children[0]?.Place(frame);
        var content = node.Children[1]!;
        var room = new UiRect(frame.X + Margin, frame.Y + Margin, Math.Max(frame.Width - 2f * Margin, 0f), Math.Max(frame.Height - 2f * Margin, 0f));

        // As wide as it likes, up to the room there is, and then as tall as that makes it.
        float width = Math.Min(content.Measure(new Proposal(null, null)).Width, room.Width);
        var size = content.Measure(new Proposal(width, null));
        size = new UiSize(Math.Min(size.Width, room.Width), Math.Min(size.Height, room.Height));
        content.Place(Clamp(Spot switch
        {
            LayerPlace.Below => Below(node, size, room),
            LayerPlace.At => At(size, room),
            LayerPlace.Bottom => new UiRect(room.X + (room.Width - size.Width) / 2f, room.Bottom - size.Height, size.Width, size.Height),
            LayerPlace.Corner => new UiRect(room.Right - size.Width, room.Bottom - size.Height, size.Width, size.Height),
            _ => room.Align(size, Alignment.Center),
        }, room));
    }

    /// <summary>Where the view this hangs off shows.</summary>
    private static UiRect Owner(UiNode node) => node.Parent is { } popup ? PrimitiveView.ShownArea(popup.Children[0]!) : default;

    private static UiRect Below(UiNode node, UiSize size, UiRect room)
    {
        var owner = Owner(node);
        float y = owner.Bottom + Gap;
        if (y + size.Height > room.Bottom && owner.Y - Gap - size.Height >= room.Y)
        {
            y = owner.Y - Gap - size.Height;
        }

        return new UiRect(owner.X, y, size.Width, size.Height);
    }

    private UiRect At(UiSize size, UiRect room)
    {
        float x = Point.X + size.Width > room.Right ? Point.X - size.Width : Point.X;
        float y = Point.Y + size.Height > room.Bottom ? Point.Y - size.Height : Point.Y;
        return new UiRect(x, y, size.Width, size.Height);
    }

    /// <summary>Moves a popup back onto the screen, keeping its size.</summary>
    private static UiRect Clamp(UiRect spot, UiRect room) => spot with
    {
        X = Math.Clamp(spot.X, room.X, Math.Max(room.Right - spot.Width, room.X)),
        Y = Math.Clamp(spot.Y, room.Y, Math.Max(room.Bottom - spot.Height, room.Y)),
    };
}

/// <summary>
/// A view with a popup hanging off it. Only the view takes part in layout. The popup signs up with the tree
/// as the view is placed, and the tree lays it out above everything.
/// </summary>
internal sealed record PopupModifier(UiView Child, LayerView? Layer) : Modifier(Child)
{
    internal override IReadOnlyList<UiView?> Children => [Child, Layer];

    internal override void Place(UiNode node, UiRect frame)
    {
        Inner(node).Place(frame);
        if (node.Children[1] is { } layer)
        {
            node.Tree.Layers.Open(layer);
        }
    }
}

/// <summary>What covers the screen behind a popup: it dims it for a dialog, and a click on it closes a light popup.</summary>
internal sealed record Backdrop(bool Scrim, Action? Dismiss) : UiView
{
    public override UiView Body => new ClickModifier(new Rectangle().Fill(Scrim ? Environment.Theme.Colors.Scrim : default), Dismiss, Cue: UiCue.Back);
}

/// <summary>The raised card a popup sits in, with the theme's edge, corners and biggest shadow.</summary>
internal sealed record PopupCard(UiView Content, float Inset, float? MinWidth = null, float? MaxWidth = null) : UiView
{
    public override UiView Body
    {
        get
        {
            var theme = Environment.Theme;
            var content = Content.Padding(Inset).Frame(minWidth: MinWidth, maxWidth: MaxWidth);
            return ThemePanel.On(content, theme.Panels.Popup, theme.Colors.Raised, box => box
                .Background(theme.Colors.Raised)
                .Border(1f, theme.Colors.Border)
                .CornerRadius(theme.RadiusLarge)
                .Shadow(theme.Shadows.Large));
        }
    }
}
