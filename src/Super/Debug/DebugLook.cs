using System.Numerics;

namespace Super;

/// <summary>
/// How a debug shape is drawn. <see cref="Channel"/> names the group it belongs to, such as "ai" or "physics", so it can
/// be shown and hidden with the others; a shape with no channel always shows. <see cref="Thickness"/> is in window
/// pixels, and 0 means <see cref="DefaultThickness"/>. Where the world is in front of it, a shape shows faintly, unless
/// it's <see cref="OnTop"/>, when it shows in full. A channel's name alone converts to a look, so
/// <c>debug.Line(a, b, Color.Red, "ai")</c> works.
/// </summary>
public readonly record struct DebugLook(string? Channel = null, float Thickness = 0f, bool OnTop = false)
{
    /// <summary>The thickness in window pixels of a shape that doesn't ask for one.</summary>
    public const float DefaultThickness = 2f;

    /// <summary>The thickest a shape may be, in window pixels.</summary>
    public const float MaxThickness = 64f;

    /// <summary>How much of a shape shows where the world is in front of it, when it isn't on top.</summary>
    internal const float HiddenShare = 0.25f;

    public static implicit operator DebugLook(string channel) => new(channel);

    /// <summary>The thickness drawn, in window pixels.</summary>
    internal float Width => Thickness > 0f ? Thickness : DefaultThickness;

    /// <summary>Throws when the thickness can't be drawn. Only builds the message when it throws.</summary>
    internal void Check()
    {
        if (!(Thickness >= 0f && Thickness <= MaxThickness))
        {
            throw new ArgumentOutOfRangeException(
                nameof(Thickness), Thickness, $"A debug shape's thickness is in window pixels, from 0 for the default to {MaxThickness}.");
        }
    }
}

/// <summary>One line drawn for debugging, from <see cref="From"/> to <see cref="To"/>, as a test can find it.</summary>
public readonly record struct DebugLine(Vector3 From, Vector3 To, Color Color, DebugLook Look);

/// <summary>A word or two drawn for debugging at a place in the world, as a test can find it.</summary>
public readonly record struct DebugLabel(Vector3 Position, string Text, Color Color, string? Channel);
