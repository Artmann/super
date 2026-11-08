using Super.Materials;

/// <summary>
/// A stain for laying over a wall as a dithered decal: dark, damp-looking grime that is solid along one edge and fades out
/// in uneven streaks. The meshes it's used on run their V from 0 at the solid edge to 1 m, where it has faded out.
/// </summary>
public sealed class Grime : MaterialGraph
{
    /// <summary>
    /// Fades out at the sides too, for a streak that runs down from something: the mesh runs its U from 0 to 2 across
    /// the streak, however wide it is. Without this the grime runs on sideways, for a band along a wall.
    /// </summary>
    public bool Streak { get; init; }

    /// <summary>Orange-brown rust washed down from iron instead of dark grime.</summary>
    public bool Rust { get; init; }

    public override Material Build()
    {
        // 0 at the solid edge, rising to 1 a metre away; the graph is 2 m wide, so that's half the image.
        var away = Gradient.Linear(90f).Multiply(1.4f);

        // Water runs down a wall in streaks, so the fade is torn into vertical runs of different lengths.
        var streaks = Noise.Directional(scale: 24, angle: 90f, stretch: 10f, octaves: 3, seed: Seed).Remap(-0.3f, 0.3f);
        var ragged = Noise.Perlin(scale: 12, octaves: 4, seed: Seed + 1).Remap(-0.15f, 0.15f);
        var opacity = away.Add(streaks).Add(ragged).Levels(0.2f, 0.95f).Invert().Multiply(0.85f);
        if (Streak)
        {
            // A tent across the streak: 0 at both sides, 1 in the middle.
            opacity = opacity.Multiply(Gradient.Linear(0f).Add(ragged).Levels(0.05f, 0.6f));
        }

        var ramp = Rust ? Ramp.Of("#4a2412", "#6e3a1a", "#7d4a24") : Ramp.Of("#2a2620", "#3d372d", "#4a4235");
        var color = Noise.Perlin(scale: 32, octaves: 4, seed: Seed + 2).GradientMap(ramp)
            .Multiply(Noise.White(seed: Seed + 3).Blur(0.0005f).Levels(0.2f, 0.8f).Remap(0.85f, 1.1f));

        return new Material
        {
            Width = 2f,
            Depth = 0.002f,
            Color = color,
            Opacity = opacity,
            Roughness = Noise.Perlin(scale: 16, octaves: 3).Remap(Rust ? 0.75f : 0.6f, 0.9f),
        };
    }
}
