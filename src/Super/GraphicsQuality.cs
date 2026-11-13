namespace Super;

/// <summary>
/// A ready-made set of <see cref="Graphics"/> settings, from the cheapest to the finest. Get one with
/// <see cref="Graphics.For"/>, or let <see cref="Graphics.Recommend"/> pick one for the player's GPU.
/// </summary>
public enum GraphicsQuality
{
    /// <summary>
    /// For integrated GPUs and old cards. The scene is drawn at three quarters of the window's size, the sun casts
    /// short, coarse shadows, and the costly effects are off: bounced light, reflections, ambient occlusion,
    /// volumetric fog, depth of field, decals, puddles, wind and lamp shadows.
    /// </summary>
    Low,

    /// <summary>
    /// For modest cards. Everything is on but volumetric fog and depth of field, with fewer light probes, a shadow
    /// cascade less and models switching to their simpler versions a little sooner.
    /// </summary>
    Medium,

    /// <summary>Everything on, as <c>new Graphics()</c> gives: the engine's default.</summary>
    High,

    /// <summary>For cards with plenty of memory: High with the sharpest sun shadows and models kept finer for longer.</summary>
    Ultra,
}
