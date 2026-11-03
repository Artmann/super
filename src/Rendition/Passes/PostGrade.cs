using System.Numerics;

namespace Rendition.Passes;

/// <summary>Turns the game's <see cref="PostSettings"/> into what the tone-mapping pass reads.</summary>
internal static class PostGrade
{
    /// <summary>How much of the bloom every picture mixes in, before <see cref="PostSettings.Bloom"/> adds to it.</summary>
    public const float DefaultBloom = 0.05f;

    /// <summary>The lift, with the bloom's strength in w, and the gain as a multiplier for each channel.</summary>
    public static (Vector4 Lift, Vector4 Gain) Tint(in PostSettings post)
    {
        float bloom = Math.Clamp(DefaultBloom + post.Bloom, 0f, 1f);
        var gain = Vector3.Max(Vector3.One + post.Gain, Vector3.Zero);
        return (new Vector4(post.Lift, bloom), new Vector4(gain, 0f));
    }
}
