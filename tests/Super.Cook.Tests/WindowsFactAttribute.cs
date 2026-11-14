namespace Super.Cook.Tests;

/// <summary>A fact that only Windows can check. Elsewhere it shows as skipped, so it never passes without running.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Only Windows can check this.";
        }
    }
}
