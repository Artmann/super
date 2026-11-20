using System.Globalization;
using System.Numerics;
using Rendition;

namespace Super;

/// <summary>Refuses a lens flare that can't be drawn, with a message saying which setting and why.</summary>
internal static class LensFlareChecks
{
    /// <summary>The intensity, every element and every light, and no more of either than fit.</summary>
    public static void Settings(in LensFlareSettings settings, int otherLights, string parameter)
    {
        if (settings.Intensity is { } intensity && !(float.IsFinite(intensity) && intensity >= 0f))
        {
            throw new ArgumentOutOfRangeException(
                parameter,
                string.Create(CultureInfo.InvariantCulture,
                    $"The lens flare's intensity must be 0 or more, with 1 as made, but it is {intensity}."));
        }

        if (settings.Elements.Length > LensFlareSettings.MaxElements)
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture,
                    $"A lens flare has at most {LensFlareSettings.MaxElements} elements, but this one has {settings.Elements.Length}."),
                parameter);
        }

        foreach (var element in settings.Elements.Span)
        {
            Element(element, parameter);
        }

        Count(settings.Lights.Length + otherLights, parameter);
        foreach (var light in settings.Lights.Span)
        {
            Light(light, parameter);
        }
    }

    /// <summary>A light's place, colour and radius.</summary>
    public static void Light(in LensFlareLight light, string parameter)
    {
        if (!Finite(light.Position))
        {
            throw new ArgumentOutOfRangeException(
                parameter, string.Create(CultureInfo.InvariantCulture, $"A flaring light must be at a place in the world, but it is at {light.Position}."));
        }

        if (!Colour(light.Color))
        {
            throw new ArgumentOutOfRangeException(
                parameter,
                string.Create(CultureInfo.InvariantCulture, $"A flaring light's colour must be 0 or more in red, green and blue, but it is {light.Color}."));
        }

        if (!(float.IsFinite(light.Radius) && light.Radius >= 0f))
        {
            throw new ArgumentOutOfRangeException(
                parameter,
                string.Create(CultureInfo.InvariantCulture, $"A flaring light's radius must be 0 or more metres, but it is {light.Radius}."));
        }
    }

    /// <summary>That <paramref name="lights"/> named lights fit besides the sun.</summary>
    public static void Count(int lights, string parameter)
    {
        if (lights > LensFlareSettings.MaxLights)
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture,
                    $"At most {LensFlareSettings.MaxLights} lights flare besides the sun, but this frame names {lights}."),
                parameter);
        }
    }

    private static void Element(in LensFlareElement element, string parameter)
    {
        if (!Enum.IsDefined(element.Shape))
        {
            throw new ArgumentOutOfRangeException(
                parameter, string.Create(CultureInfo.InvariantCulture, $"A lens flare element's shape must be a LensFlareShape, but it is {(int)element.Shape}."));
        }

        if (!(float.IsFinite(element.Size) && element.Size > 0f))
        {
            throw new ArgumentOutOfRangeException(
                parameter,
                string.Create(CultureInfo.InvariantCulture, $"A lens flare element's size must be above 0 screen heights, but it is {element.Size}."));
        }

        if (!float.IsFinite(element.Position))
        {
            throw new ArgumentOutOfRangeException(
                parameter,
                string.Create(CultureInfo.InvariantCulture,
                    $"A lens flare element's position must be a number, 0 on the light and 1 in the middle of the screen, but it is {element.Position}."));
        }

        if (!Colour(element.Color))
        {
            throw new ArgumentOutOfRangeException(
                parameter,
                string.Create(CultureInfo.InvariantCulture, $"A lens flare element's colour must be 0 or more in red, green and blue, but it is {element.Color}."));
        }

        if (element.Sides is not 0 and < 3)
        {
            throw new ArgumentOutOfRangeException(
                parameter,
                string.Create(CultureInfo.InvariantCulture, $"A lens flare polygon has 3 sides or more, or 0 for six, but this one has {element.Sides}."));
        }
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool Colour(Vector3 value) => Finite(value) && value.X >= 0f && value.Y >= 0f && value.Z >= 0f;
}
