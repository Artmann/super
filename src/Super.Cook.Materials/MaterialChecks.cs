using System.Globalization;
using Super.Materials;

namespace Super.Cook.Materials;

/// <summary>
/// Values that no real surface has. The colour is judged by the bytes it is written as, so "sRGB 30" is the byte 30.
/// Metalness and roughness are judged clamped to 0..1, as they're written. Pixels that aren't numbers are left out.
/// Sums add pixels in index order, so the result never depends on threads.
/// </summary>
internal static class MaterialChecks
{
    private const int DarkestByte = 30;
    private const int BrightestByte = 240;
    private const float MetalLow = 0.1f;
    private const float MetalHigh = 0.9f;
    private const double MinRoughnessSpread = 0.02;

    public static IEnumerable<string> Problems(MaterialImages images, string name)
    {
        var problems = new List<string>();
        CheckColor(images.Color, name, problems);
        CheckMetallic(images.Metallic, name, problems);
        CheckRoughness(images.Roughness, name, problems);
        return problems;
    }

    private static void CheckColor(float[] color, string name, List<string> problems)
    {
        int counted = 0;
        int dark = 0;
        int bright = 0;
        for (int i = 0; i + 2 < color.Length; i += 3)
        {
            if (float.IsNaN(color[i]) || float.IsNaN(color[i + 1]) || float.IsNaN(color[i + 2]))
            {
                continue;
            }

            int r = ToByte(color[i]);
            int g = ToByte(color[i + 1]);
            int b = ToByte(color[i + 2]);
            counted++;
            if (Math.Max(r, Math.Max(g, b)) < DarkestByte)
            {
                dark++;
            }

            if (Math.Min(r, Math.Min(g, b)) > BrightestByte)
            {
                bright++;
            }
        }

        // More than a tenth, worked out in whole numbers.
        if (dark * 10L > counted)
        {
            problems.Add($"{name}: {Percent(dark, counted)} of the base colour is darker than sRGB {DarkestByte}. Nothing real is that dark. Charcoal is about 50.");
        }

        if (bright * 10L > counted)
        {
            problems.Add($"{name}: {Percent(bright, counted)} of the base colour is brighter than sRGB {BrightestByte}. Fresh snow is about 240.");
        }
    }

    private static void CheckMetallic(float[] metallic, string name, List<string> problems)
    {
        int counted = 0;
        int between = 0;
        foreach (float raw in metallic)
        {
            if (float.IsNaN(raw))
            {
                continue;
            }

            float value = Math.Clamp(raw, 0f, 1f);
            counted++;
            if (value >= MetalLow && value <= MetalHigh)
            {
                between++;
            }
        }

        if (between * 2L > counted)
        {
            problems.Add($"{name}: most of the metalness is between 0.1 and 0.9. Surfaces are metal or not, and in-between values look like plastic.");
        }
    }

    private static void CheckRoughness(float[] roughness, string name, List<string> problems)
    {
        int counted = 0;
        double sum = 0;
        foreach (float raw in roughness)
        {
            if (!float.IsNaN(raw))
            {
                counted++;
                sum += Math.Clamp(raw, 0f, 1f);
            }
        }

        if (counted == 0)
        {
            return;
        }

        double mean = sum / counted;
        double squares = 0;
        foreach (float raw in roughness)
        {
            if (!float.IsNaN(raw))
            {
                double difference = Math.Clamp(raw, 0f, 1f) - mean;
                squares += difference * difference;
            }
        }

        // The population deviation: divided by the pixel count, not one less.
        if (Math.Sqrt(squares / counted) < MinRoughnessSpread)
        {
            problems.Add($"{name}: the roughness hardly varies. Real surfaces have smudges and wear, so vary it by a few percent at least.");
        }
    }

    private static int ToByte(float value) => Math.Clamp((int)MathF.Round(value * 255f, MidpointRounding.AwayFromZero), 0, 255);

    private static string Percent(int count, int total)
    {
        double percent = Math.Round(100.0 * count / total, MidpointRounding.AwayFromZero);
        return percent.ToString("0", CultureInfo.InvariantCulture) + "%";
    }
}
