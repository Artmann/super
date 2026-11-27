namespace Super.Ui;

/// <summary>
/// Covers the screen behind a pause menu or a dialogue: it dims the game and blurs it, so what's on top stands out.
/// Put it first in a <see cref="ZStack"/> with the menu over it. The blur is the picture's own
/// <see cref="Rendition.ScreenEffects.Blur"/>, which the interface on top never gets, and it fades in with the dim. A
/// <see cref="Blur"/> of 0 leaves the game sharp, and the graphics settings' ScreenEffects switch turns it off too.
/// </summary>
public sealed record Backdrop : UiView
{
    /// <summary>How dark the game goes, from 0, not at all, to 1, black.</summary>
    public float Dim { get; init; } = 0.45f;

    /// <summary>How soft the game goes, from 0, sharp, to 1, as soft as the blur goes.</summary>
    public float Blur { get; init; } = 0.8f;

    /// <summary>How long it takes to come in, in seconds.</summary>
    public float Seconds { get; init; } = 0.3f;

    public override UiView Body
    {
        get
        {
            float shown = UseAnimation(1f, Seconds, Easing.Out, from: 0f);
            var cover = new Rectangle().Fill(new Color(0f, 0f, 0f, Math.Clamp(Dim, 0f, 1f) * shown));
            return new BlurModifier(cover, Math.Clamp(Blur, 0f, 1f) * shown);
        }
    }
}

/// <summary>Asks for the picture behind the interface to be blurred by <paramref name="Amount"/> while it's laid out.</summary>
internal sealed record BlurModifier(UiView Child, float Amount) : Modifier(Child)
{
    internal override void Place(UiNode node, UiRect frame)
    {
        var world = node.Tree.World;
        world.Blur = MathF.Max(world.Blur, Amount);
        Inner(node).Place(frame);
    }
}
