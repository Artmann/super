using System.Numerics;
using static Rendition.SettingsMix;

namespace Rendition;

/// <summary>
/// Effects on the whole screen that a game drives from what happens in play, such as a red edge when the player is
/// hurt, a white flash on a hit or a blur at speed. Each one is off at zero and costs next to nothing there, and
/// changing them never bakes the grade again, so a game can set them every frame. See docs/rendering.md.
/// </summary>
public struct ScreenEffects
{
    /// <summary>
    /// Colour fringes that grow towards the edges of the screen, as through a cheap lens, from 0, none, to 1, about
    /// 16 pixels at the corners of a 1080p picture. 0.2 is a hint; a short burst near 1 sells a heavy hit.
    /// </summary>
    public float ChromaticAberration;

    /// <summary>
    /// A blur streaking out from <see cref="SpeedBlurCenter"/>, as at speed, from 0, none, to 1, where the corners
    /// smear a quarter of the way to it. 0.2 is a fast run; 0.6 a dive.
    /// </summary>
    public float SpeedBlur;

    /// <summary>
    /// Where <see cref="SpeedBlur"/> streaks out from, as a fraction of the screen: (0.5, 0.5) is the middle, which is
    /// what null means. Put it where the camera is heading.
    /// </summary>
    public Vector2? SpeedBlurCenter;

    /// <summary>
    /// How much of <see cref="FlashColor"/> lights the picture, from 0, none, to 1, the colour over all of it. The
    /// picture shows through, brightened, as under a burst of light. Ease it back to 0 over a few frames for a hit.
    /// </summary>
    public float Flash;

    /// <summary>The colour of <see cref="Flash"/>, linear red, green and blue from 0 to 1. Zero is white.</summary>
    public Vector3 FlashColor;

    /// <summary>
    /// How far the picture fades into <see cref="FadeColor"/>, from 0, none, to 1, only the colour. Unlike
    /// <see cref="Flash"/> it covers the picture, as for a fade to black when the player falls.
    /// </summary>
    public float Fade;

    /// <summary>The colour of <see cref="Fade"/>, linear red, green and blue from 0 to 1. Zero is black.</summary>
    public Vector3 FadeColor;

    /// <summary>
    /// A soft blur over the whole picture, from 0, none, to 1, about as soft as the glow round a bright light, for the game
    /// behind a pause menu or a dialogue. The interface on top stays sharp. <c>Super.Ui.Backdrop</c> sets it for you.
    /// </summary>
    public float Blur;

    /// <summary>A colour creeping in from the edges of the screen, such as red while the player is hurt.</summary>
    public EdgeTint EdgeTint;

    /// <summary>A second edge colour over the first, such as green while the player heals, so both can show at once.</summary>
    public EdgeTint SecondEdgeTint;

    /// <summary>
    /// How much colour the picture loses, from 0, none, to 1, grey, after the grade, so it's on top of
    /// <see cref="PostSettings.Saturation"/>. 0.7 drains a world near death.
    /// </summary>
    public float Desaturate;

    /// <summary>
    /// A texture multiplied over the picture, tiled, such as paper or canvas for a painted look. White leaves the
    /// picture as it is and darker parts darken it. Null is none.
    /// </summary>
    public TextureHandle? Canvas;

    /// <summary>How much <see cref="Canvas"/> shows, from 0, none, to 1, all of it.</summary>
    public float CanvasStrength;

    /// <summary>
    /// How many pixels of the window one tile of <see cref="Canvas"/> covers across. 0 is the texture's own size, a texel
    /// a pixel. The paper stays put on the screen while the scene moves under it.
    /// </summary>
    public float CanvasTileSize;

    /// <summary>
    /// The effects <paramref name="t"/> of the way from <paramref name="from"/> to <paramref name="to"/>. Amounts move
    /// evenly. A colour whose amount is 0 at one end takes the other end's, so a red edge fades in red rather than
    /// through black. Two different <see cref="Canvas"/> textures cross through none, as two colour tables do.
    /// <paramref name="t"/> is held from 0, all <paramref name="from"/>, to 1, all <paramref name="to"/>.
    /// </summary>
    public static ScreenEffects Lerp(in ScreenEffects from, in ScreenEffects to, float t)
    {
        if (!(t > 0f))
        {
            return from;
        }

        if (t >= 1f)
        {
            return to;
        }

        bool sameCanvas = from.Canvas == to.Canvas;
        var middle = new Vector2(0.5f);
        return new ScreenEffects
        {
            ChromaticAberration = Mix(from.ChromaticAberration, to.ChromaticAberration, t),
            SpeedBlur = Mix(from.SpeedBlur, to.SpeedBlur, t),
            SpeedBlurCenter = Toward(
                from.SpeedBlurCenter ?? middle, from.SpeedBlur, to.SpeedBlurCenter ?? middle, to.SpeedBlur, t),
            Flash = Mix(from.Flash, to.Flash, t),
            FlashColor = Toward(from.FlashColor, from.Flash, to.FlashColor, to.Flash, t),
            Fade = Mix(from.Fade, to.Fade, t),
            FadeColor = Toward(from.FadeColor, from.Fade, to.FadeColor, to.Fade, t),
            EdgeTint = EdgeTint.Lerp(from.EdgeTint, to.EdgeTint, t),
            SecondEdgeTint = EdgeTint.Lerp(from.SecondEdgeTint, to.SecondEdgeTint, t),
            Blur = Mix(from.Blur, to.Blur, t),
            Desaturate = Mix(from.Desaturate, to.Desaturate, t),
            Canvas = sameCanvas || t < 0.5f ? from.Canvas : to.Canvas,
            CanvasStrength = sameCanvas
                ? Mix(from.CanvasStrength, to.CanvasStrength, t)
                : t < 0.5f ? from.CanvasStrength * (1f - (2f * t)) : to.CanvasStrength * ((2f * t) - 1f),
            CanvasTileSize = sameCanvas ? Mix(from.CanvasTileSize, to.CanvasTileSize, t) : t < 0.5f ? from.CanvasTileSize : to.CanvasTileSize,
        };
    }

    /// <summary>
    /// <paramref name="top"/> over <paramref name="under"/>: each effect <paramref name="top"/> sets wins, and the rest
    /// are <paramref name="under"/>'s. A flash from play over a finish's paper keeps the paper.
    /// </summary>
    public static ScreenEffects Over(in ScreenEffects top, in ScreenEffects under)
    {
        bool blur = top.SpeedBlur != 0f;
        bool flash = top.Flash != 0f;
        bool fade = top.Fade != 0f;
        bool canvas = top.Canvas is not null && top.CanvasStrength != 0f;
        return new ScreenEffects
        {
            ChromaticAberration = Pick(top.ChromaticAberration != 0f, top.ChromaticAberration, under.ChromaticAberration),
            SpeedBlur = Pick(blur, top.SpeedBlur, under.SpeedBlur),
            SpeedBlurCenter = Pick(blur, top.SpeedBlurCenter, under.SpeedBlurCenter),
            Flash = Pick(flash, top.Flash, under.Flash),
            FlashColor = Pick(flash, top.FlashColor, under.FlashColor),
            Fade = Pick(fade, top.Fade, under.Fade),
            FadeColor = Pick(fade, top.FadeColor, under.FadeColor),
            EdgeTint = Pick(top.EdgeTint.Strength != 0f, top.EdgeTint, under.EdgeTint),
            SecondEdgeTint = Pick(top.SecondEdgeTint.Strength != 0f, top.SecondEdgeTint, under.SecondEdgeTint),
            Blur = Pick(top.Blur != 0f, top.Blur, under.Blur),
            Desaturate = Pick(top.Desaturate != 0f, top.Desaturate, under.Desaturate),
            Canvas = Pick(canvas, top.Canvas, under.Canvas),
            CanvasStrength = Pick(canvas, top.CanvasStrength, under.CanvasStrength),
            CanvasTileSize = Pick(canvas, top.CanvasTileSize, under.CanvasTileSize),
        };
    }

    private static T Pick<T>(bool fromTop, T top, T under) => fromTop ? top : under;

    // A colour on its way between two ends, taking the other end's colour where one end's amount is none.
    private static Vector3 Toward(Vector3 from, float fromAmount, Vector3 to, float toAmount, float t) =>
        fromAmount == 0f ? to : toAmount == 0f ? from : Vector3.Lerp(from, to, t);

    private static Vector2 Toward(Vector2 from, float fromAmount, Vector2 to, float toAmount, float t) =>
        fromAmount == 0f ? to : toAmount == 0f ? from : Vector2.Lerp(from, to, t);
}

/// <summary>
/// A colour creeping in from the edges of the screen, for <see cref="ScreenEffects.EdgeTint"/>. <paramref name="Color"/>
/// is linear red, green and blue from 0 to 1. <paramref name="Strength"/> runs from 0, none, to 1, where the colour
/// covers the edges and reaches a third of the way in; the middle stays clear.
/// </summary>
public readonly record struct EdgeTint(Vector3 Color, float Strength)
{
    /// <summary>The tint <paramref name="t"/> of the way between two, keeping one's colour while the other's strength is 0.</summary>
    public static EdgeTint Lerp(in EdgeTint from, in EdgeTint to, float t)
    {
        var color = from.Strength == 0f ? to.Color : to.Strength == 0f ? from.Color : Vector3.Lerp(from.Color, to.Color, t);
        return new EdgeTint(color, Mix(from.Strength, to.Strength, t));
    }
}
