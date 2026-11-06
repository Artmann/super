using System.Numerics;
using System.Runtime.InteropServices;

namespace Rendition.Passes;

/// <summary>What the finish pass reads to move between pictures. Must match Transition in Shaders/finish.frag.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct TransitionConstants
{
    /// <summary>rgb: the fade's colour; a: how far along, from 0 to 1.</summary>
    public Vector4 ColorAmount;

    /// <summary>xy: which way a wipe travels, one long; zw: where an iris opens from.</summary>
    public Vector4 Shape;

    /// <summary>x: the style as a number; y: the edge's softness.</summary>
    public Vector4 Style;

    public static unsafe uint Size => (uint)sizeof(TransitionConstants);

    /// <summary>Settles the game's values into ones the shader can use as they are.</summary>
    public static TransitionConstants From(in ScreenTransition transition)
    {
        var direction = transition.Direction;
        float length = direction.Length();
        direction = length > 1e-6f && float.IsFinite(length) ? direction / length : Vector2.UnitX;

        float amount = float.IsFinite(transition.Amount) ? Math.Clamp(transition.Amount, 0f, 1f) : 0f;
        float softness = float.IsFinite(transition.Softness) ? MathF.Max(transition.Softness, 0f) : 0f;
        var color = Vector3.Max(transition.Color, Vector3.Zero);

        return new TransitionConstants
        {
            ColorAmount = new Vector4(color, amount),
            Shape = new Vector4(direction, transition.Center.X, transition.Center.Y),
            Style = new Vector4((float)transition.Style, softness, 0f, 0f),
        };
    }
}
