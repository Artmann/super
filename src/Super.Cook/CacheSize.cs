using System.Globalization;

namespace Super.Cook;

/// <summary>
/// Reads and writes sizes such as <c>4GB</c>, <c>500MB</c>, <c>1.5GB</c> or plain bytes, for the cook cache's limit.
/// Units are powers of 1024, as Windows shows sizes, so <c>KB</c> and <c>KiB</c> mean the same. <c>none</c> or <c>0</c>
/// means no limit, which reads as 0.
/// </summary>
public static class CacheSize
{
    /// <summary>The limit when nothing sets one: 4 GiB.</summary>
    public const long Default = 4L << 30;

    private static readonly (string Unit, long Bytes)[] Units =
    [
        ("TIB", 1L << 40), ("GIB", 1L << 30), ("MIB", 1L << 20), ("KIB", 1L << 10),
        ("TB", 1L << 40), ("GB", 1L << 30), ("MB", 1L << 20), ("KB", 1L << 10),
        ("T", 1L << 40), ("G", 1L << 30), ("M", 1L << 20), ("K", 1L << 10),
        ("B", 1L),
    ];

    /// <summary>Reads <paramref name="text"/>, or says what's wrong with it in <paramref name="error"/>.</summary>
    /// <param name="bytes">The size in bytes, 0 for no limit.</param>
    public static bool TryParse(string text, out long bytes, out string? error)
    {
        bytes = 0;
        error = null;
        string trimmed = text.Trim();
        if (trimmed.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string upper = trimmed.ToUpperInvariant();
        long unit = 1;
        foreach (var (name, size) in Units)
        {
            if (upper.EndsWith(name, StringComparison.Ordinal))
            {
                unit = size;
                upper = upper[..^name.Length].TrimEnd();
                break;
            }
        }

        if (upper.Length == 0
            || !decimal.TryParse(upper, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal number)
            || number * unit > long.MaxValue)
        {
            error = $"\"{text}\" isn't a size. Give a number with KB, MB, GB or TB, such as 4GB, 500MB or 1.5GB, a number of bytes, or none for no limit.";
            return false;
        }

        bytes = (long)decimal.Round(number * unit);
        return true;
    }

    /// <summary>Reads <paramref name="text"/>.</summary>
    /// <exception cref="FormatException">It isn't a size; the message says why.</exception>
    public static long Parse(string text)
    {
        return TryParse(text, out long bytes, out string? error) ? bytes : throw new FormatException(error);
    }

    /// <summary>
    /// A size for people, such as <c>1.5 GB</c> or <c>320 KB</c>, in the invariant culture. Below 10 a second decimal
    /// shows when it isn't 0, so a cook of 1.02 GB over a limit of 1.0 GB doesn't read as 1.0 GB over 1.0 GB.
    /// </summary>
    public static string Format(long bytes)
    {
        if (bytes < 1024)
        {
            return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        }

        double value = bytes;
        string[] names = ["KB", "MB", "GB", "TB"];
        int index = -1;
        while (value >= 1024 && index < names.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return value.ToString(value < 10 ? "0.0#" : "0", CultureInfo.InvariantCulture) + " " + names[index];
    }
}
