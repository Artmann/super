namespace Super.Cook;

/// <summary>How the file system compares paths.</summary>
internal static class PathCase
{
    /// <summary>Windows' and macOS' file systems ignore case by default; Linux's don't.</summary>
    public static readonly StringComparer PathComparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
