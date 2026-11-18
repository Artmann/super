using System.Numerics;

namespace Rendition;

/// <summary>The look of the finished picture. Leaving a value at zero leaves that part unchanged.</summary>
public struct PostSettings
{
    /// <summary>
    /// Brightness in photographic stops: +1 is twice as bright. With <see cref="AutoExposure"/> on it is
    /// added to what the camera metered, so -0.3 keeps the picture a third of a stop darker than metered.
    /// </summary>
    public float Exposure;

    /// <summary>
    /// Sets the brightness from what the camera sees, the way an eye or a camera does: it meters the lit
    /// frame each frame, before the glow, and eases towards the brightness that puts it at middle grey.
    /// The darkest 40 per cent and the brightest 5 per cent of the pixels are left out of the metering, so
    /// the sun's disc and sparkle on water don't darken the picture. Off, <see cref="Exposure"/> alone
    /// sets the brightness. See docs/rendering.md.
    /// </summary>
    public bool AutoExposure;

    /// <summary>
    /// How fast <see cref="AutoExposure"/> follows a change, in stops a second, which must be above 0. 0
    /// means the default of 2, so stepping from a sunny yard into a room four stops darker settles in about two seconds.
    /// </summary>
    public float AdaptationSpeed;

    /// <summary>Added contrast. 0.1 is a gentle boost; negative values flatten the picture.</summary>
    public float Contrast;

    /// <summary>Added colour. 0.1 is a gentle boost; -1 is black and white.</summary>
    public float Saturation;

    /// <summary>How much the corners darken, from 0 to 1.</summary>
    public float Vignette;

    /// <summary>Crispness restored after anti-aliasing, from 0 to 1.</summary>
    public float Sharpen;

    /// <summary>Strength of film grain. 0.01 is subtle.</summary>
    public float Grain;

    /// <summary>
    /// Added glow, on top of the soft glow every picture has. 0.1 is a dreamy haze round bright
    /// things; negative values take the glow away.
    /// </summary>
    public float Bloom;

    /// <summary>
    /// Colour added to the darkest parts of the picture, fading out towards white, so shadows
    /// take on a tint instead of going black. 0.02 is gentle; (0.03, 0.02, 0.05) gives lilac shade.
    /// </summary>
    public Vector3 Lift;

    /// <summary>
    /// Extra brightness in each of red, green and blue, which shows most in the brightest parts.
    /// 0.05 is five per cent brighter; (0.05, 0.02, -0.04) warms the highlights to cream.
    /// </summary>
    public Vector3 Gain;

    /// <summary>
    /// The curve that fits the scene's light into what a screen can show. <see cref="Tonemapper.Agx"/>,
    /// the default, keeps bright colours from going flat; <see cref="Tonemapper.Aces"/> is the older,
    /// punchier film look.
    /// </summary>
    public Tonemapper Tonemapper;

    /// <summary>
    /// The look on top of AgX. <see cref="AgxLook.Punchy"/>, the default, deepens the mid-tones and
    /// strengthens colour; <see cref="AgxLook.Base"/> is the plain curve. It has no effect under ACES.
    /// </summary>
    public AgxLook AgxLook;

    /// <summary>
    /// White balance from -1 to 1, with 0 unchanged. Above 0 warms the picture, as if the camera were
    /// balanced for light bluer than daylight (1 is 12000 K); below 0 cools it (-1 is 4000 K).
    /// 0.1 is a gentle afternoon warmth.
    /// </summary>
    public float Temperature;

    /// <summary>
    /// White balance across the other way, from -1 to 1, with 0 unchanged. Above 0 pulls the picture
    /// towards magenta and below 0 towards green, to take out a cast that <see cref="Temperature"/> can't.
    /// </summary>
    public float Tint;

    /// <summary>
    /// Where the camera is focused, in metres in front of it, for depth of field. Only things at this
    /// distance are sharp once <see cref="Aperture"/> opens the lens. It must be further than
    /// <see cref="FocalLength"/>. See docs/rendering.md.
    /// </summary>
    public float FocusDistance;

    /// <summary>
    /// How wide the lens is open, as an f-number: 2.8 blurs strongly, 8 gently, 16 hardly at all.
    /// 0, the default, is no depth of field: everything is sharp, as through a pinhole.
    /// </summary>
    public float Aperture;

    /// <summary>
    /// The lens's focal length in millimetres for depth of field, on a 36 mm wide sensor. Longer lenses
    /// blur more at the same f-number. 0 means the default of 35. It changes only the blur, not the
    /// field of view, which the camera sets.
    /// </summary>
    public float FocalLength;
}

/// <summary>The curve that turns the scene's light into the colours a screen can show.</summary>
public enum Tonemapper
{
    /// <summary>
    /// AgX, as Blender 4 uses by default: bright, strongly coloured light fades towards white
    /// gradually, so a sunlit brick wall keeps its detail instead of going one flat orange.
    /// </summary>
    Agx,

    /// <summary>A fit of the ACES film curve: punchier mid-tones, but saturated highlights go flat.</summary>
    Aces,
}

/// <summary>A look on top of the <see cref="Tonemapper.Agx"/> curve, as Blender offers. ACES ignores it.</summary>
public enum AgxLook
{
    /// <summary>Blender's Punchy look: deeper mid-tones and richer colour, so a sunlit scene doesn't read pale.</summary>
    Punchy,

    /// <summary>The plain AgX curve: softer and paler, with the most room in the highlights.</summary>
    Base,
}

/// <summary>
/// What smooths the scene's edges over several frames and, below a <see cref="Renderer.RenderScale"/> of 1, rebuilds
/// it at the window's size. See <see cref="Renderer.Upscaler"/>.
/// </summary>
public enum Upscaler
{
    /// <summary>
    /// The engine's own temporal anti-aliasing, at the scene's size, then stretched to the window with a sharp
    /// resample. Cheap, and it needs nothing outside the engine.
    /// </summary>
    Taa,

    /// <summary>
    /// AMD FidelityFX Super Resolution 3.1, which blends the frames at the window's size and keeps thin detail such
    /// as grass and railings steadier. It needs its library next to the game, and falls back to <see cref="Taa"/>
    /// without it or above a render scale of 1.
    /// </summary>
    Fsr,
}

/// <summary>How a <see cref="ScreenTransition"/> brings in the new picture.</summary>
public enum TransitionStyle
{
    /// <summary>The frame as it is.</summary>
    None,

    /// <summary>Mixes the frame towards <see cref="ScreenTransition.Color"/>.</summary>
    Fade,

    /// <summary>Mixes the held picture into the frame evenly.</summary>
    Dissolve,

    /// <summary>The frame sweeps across the held picture behind a straight edge.</summary>
    Wipe,

    /// <summary>The frame opens out of a circle over the held picture.</summary>
    Iris,
}

/// <summary>
/// Moves from one picture to another. A fade mixes the frame towards a colour. A dissolve,
/// wipe or iris shows a held picture, one frame kept from earlier, over the frame, and
/// takes it away as <see cref="Amount"/> goes from 0 to 1. Leaving Style at None shows the
/// frame as it is. It changes the scene's picture only: the overlay is drawn on top.
/// </summary>
public struct ScreenTransition
{
    public TransitionStyle Style;

    /// <summary>
    /// How far along, from 0 to 1. For a fade, how much of the colour shows. For the
    /// others, 0 shows only the held picture and 1 only the frame.
    /// </summary>
    public float Amount;

    /// <summary>What a fade goes to, linear red, green and blue.</summary>
    public Vector3 Color;

    /// <summary>Which way a wipe's edge travels across the screen, with x to the right and y down. Zero goes to the right.</summary>
    public Vector2 Direction;

    /// <summary>Where an iris opens from, as a fraction of the screen: (0.5, 0.5) is the middle.</summary>
    public Vector2 Center;

    /// <summary>How soft a wipe's or an iris's edge is, as a fraction of the way it travels.</summary>
    public float Softness;

    /// <summary>
    /// Changing this number holds a new picture: the frame drawn just before this one, as it
    /// looked before the overlay. Keep it the same to keep the picture held. Before any frame
    /// has been drawn, the held picture is black.
    /// </summary>
    public int Hold;
}
