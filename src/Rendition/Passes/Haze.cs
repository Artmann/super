using System.Numerics;

namespace Rendition.Passes;

/// <summary>Packs where the haze is measured from and what it fades into, for the frame block.</summary>
internal static class Haze
{
    /// <summary>
    /// The origin is the camera unless the view gives one; its w is 1 for a given origin, which
    /// the haze is measured from across the ground. The colour's a is 1 when the view gives a
    /// colour, and 0 when things fade into the background behind them.
    /// </summary>
    public static (Vector4 Origin, Vector4 Color) Pack(Vector3 camera, Vector3? origin, Vector3 color) =>
        (new Vector4(origin ?? camera, origin.HasValue ? 1f : 0f), new Vector4(color, color != Vector3.Zero ? 1f : 0f));
}
