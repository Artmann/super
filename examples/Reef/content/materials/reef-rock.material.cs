using Super.Materials;

/// <summary>
/// Old reef limestone, 2 m across: pale, pitted rock bored full of holes, crusted over in patches with rough pink
/// coralline algae and furred with short olive turf, and dotted with the small round cups of young corals.
/// </summary>
public sealed class ReefRock : MaterialGraph
{
    public override Material Build()
    {
        var (lumps, holes, grain, rock) = Rock();

        // Coralline crust: smooth-edged pink patches that sit a little proud of the rock and fill its smaller holes.
        var crustMask = Noise.Perlin(scale: 5, octaves: 4, seed: 6).Warp(Noise.Perlin(scale: 3, octaves: 2, seed: 7), amount: 0.01f)
            .Levels(0.58f, 0.64f);
        // Turf: short olive fur on the rest, thick in some places and gone in others.
        var turfMask = Noise.Perlin(scale: 7, octaves: 4, seed: 8).Levels(0.52f, 0.7f).Multiply(crustMask.Invert());
        var fur = Noise.White(seed: 9).Blur(0.0005f);
        var cups = Cups();
        var cupMask = cups.Levels(0.05f, 0.15f);

        var height = rock
            .Blend(rock.Add(0.06f).Blend(lumps.Multiply(0.7f).Add(0.08f), opacity: 0.6f), mask: crustMask)
            .Blend(rock.Add(fur.Multiply(0.05f)), mask: turfMask)
            .Blend(rock.Add(cups.Multiply(0.12f)), mask: cupMask);

        var stone = lumps.Add(grain.Multiply(4f)).GradientMap(Ramp.Of("#8a8170", "#a99f8a", "#c4bba3", "#d6cdb6"))
            .Blend(Rgb.Of("#4b4538"), mask: holes.Levels(0.2f, 0.8f), opacity: 0.8f);
        var crust = Noise.Perlin(scale: 30, octaves: 3, seed: 11).GradientMap(Ramp.Of("#a87476", "#c4908c", "#d8b0a6"));
        var turf = fur.Multiply(0.6f).Add(Noise.Perlin(scale: 12, octaves: 3, seed: 12).Multiply(0.4f))
            .GradientMap(Ramp.Of("#55543a", "#716b45", "#8a8456"));
        var cupColor = cups.FloodFill(threshold: 0.05f).RandomGray(seed: 13)
            .GradientMap(Ramp.Of("#c98a3a", "#7fa04a", "#a46bb0", "#d6b25a"))
            .Blend(Rgb.Of("#2b2418"), mask: cups.Levels(0.25f, 0.35f).Invert());

        return new Material
        {
            Width = 2f,
            Depth = 0.05f,
            Occlusion = 0.9f,
            Height = height,
            Color = stone.Blend(crust, mask: crustMask).Blend(turf, mask: turfMask).Blend(cupColor, mask: cupMask),
            Roughness = Gray.Of(0.85f).Blend(Gray.Of(0.6f), mask: crustMask).Blend(Gray.Of(0.95f), mask: turfMask),
        };
    }

    /// <summary>The bare rock: broad lumps with sharper knobs, bored with holes of a few sizes, and fine grain.</summary>
    private static (Gray Lumps, Gray Holes, Gray Grain, Gray Rock) Rock()
    {
        var lumps = Noise.Perlin(scale: 4, octaves: 5, seed: 1);
        var knobs = Noise.Cells(scale: 18, output: CellOutput.F1, seed: 2).Invert().Levels(0.3f, 1f).Multiply(0.25f);
        var hole = Shape.Of(ShapeKind.Bell).Levels(0f, 0.6f);
        var holes = hole.Splatter(count: 900, sizeMin: 0.004f, sizeMax: 0.012f, seed: 3)
            .Max(hole.Splatter(count: 80, sizeMin: 0.015f, sizeMax: 0.03f, seed: 4));
        var grain = Noise.White(seed: 5).Blur(0.0008f).Remap(-0.03f, 0.03f);
        var rock = lumps.Multiply(0.7f).Add(knobs).Add(grain).Subtract(holes.Multiply(0.35f));
        return (lumps, holes, grain, rock);
    }

    /// <summary>Young corals: little round cups, each with a dark middle.</summary>
    private static Gray Cups()
    {
        var cup = Pixel.Gray(p =>
        {
            var d = (p.Uv - new System.Numerics.Vector2(0.5f)).Length() * 2f;
            if (d >= 1f)
            {
                return 0f;
            }

            return d < 0.45f ? 0.3f : 1f - (d - 0.45f) / 0.55f * 0.4f;
        });
        return cup.Splatter(count: 60, sizeMin: 0.01f, sizeMax: 0.022f, seed: 10);
    }
}
