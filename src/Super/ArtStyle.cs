using System.Globalization;
using Rendition;

namespace Super;

/// <summary>
/// Shows a <see cref="ArtLook"/> over the whole game: <c>style.Use(Looks.Painterly)</c>, or with a number of seconds to
/// ease from the look on screen into the new one. Ask for it in your game's constructor. It runs on real ticks, so slow
/// motion doesn't stretch a blend, and <c>--look</c> picks the look a game starts with. See "Looks" in docs/rendering.md.
/// </summary>
public sealed class ArtStyle(GameTime time) : IDrawHook
{
    private ArtLook _from;
    private ArtLook _to;
    private ArtLook _shown;
    private long _start;
    private float _seconds;

    /// <summary>The look being shown, or being blended to.</summary>
    public ArtLook Current => _to;

    /// <summary>
    /// Shows <paramref name="look"/> from the next frame on, or eases into it from the look on screen over
    /// <paramref name="seconds"/>. Throws when a setting is out of its range, naming it.
    /// </summary>
    public void Use(in ArtLook look, float seconds = 0f)
    {
        if (!(float.IsFinite(seconds) && seconds >= 0f))
        {
            throw new ArgumentOutOfRangeException(
                nameof(seconds),
                string.Create(CultureInfo.InvariantCulture, $"A look's blend must take 0 seconds or more, but it is {seconds}."));
        }

        Check(look);
        _from = _shown;
        _to = look;
        _start = time.Tick;
        _seconds = seconds;
    }

    /// <summary>The look on screen this frame, eased from the last one.</summary>
    void IDrawHook.AfterDraw(View view)
    {
        float t = _seconds > 0f ? Math.Clamp(view.SecondsSince(_start) / _seconds, 0f, 1f) : 1f;
        _shown = ArtLook.Lerp(_from, _to, t * t * (3f - (2f * t)));
        view.Effects.Look.Style = _shown;
    }

    private static void Check(in ArtLook look)
    {
        PaintChecks.Settings(look.Paint, nameof(look));
        var retro = look.Retro;
        if (!(float.IsFinite(retro.PixelSize) && retro.PixelSize >= 0f))
        {
            throw Refuse($"A look's pixel size must be 0 or more, but it is {retro.PixelSize}.");
        }

        if (retro.Levels != 0 && retro.Levels is < 2 or > 256)
        {
            throw Refuse($"A look's levels must be from 2 to 256, or 0 to keep every shade, but it is {retro.Levels}.");
        }

        if (!(retro.Dither is >= 0f and <= 1f))
        {
            throw Refuse($"A look's dither must be from 0 to 1, but it is {retro.Dither}.");
        }

        var light = look.Light;
        if (!float.IsFinite(light.Sun + light.SunWarmth + light.Sky + light.Fog + light.Clouds + light.Flare))
        {
            throw new ArgumentOutOfRangeException(nameof(look), "Every part of a look's light must be a finite number.");
        }
    }

    private static ArgumentOutOfRangeException Refuse(FormattableString message) =>
        new("look", message.ToString(CultureInfo.InvariantCulture));
}
