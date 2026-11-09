using System.Runtime.InteropServices;

namespace Super.Cook;

/// <summary>
/// Makes a second name for a file that's already on disk, so the same bytes aren't written and kept twice. It only works
/// inside one volume, and some file systems can't do it at all; then it says no and the caller writes the bytes instead.
/// </summary>
internal static class HardLink
{
    /// <summary>Makes <paramref name="link"/> name the same file as <paramref name="existing"/>. False when it couldn't.</summary>
    public static bool TryCreate(string existing, string link)
    {
        try
        {
            return OperatingSystem.IsWindows()
                ? CreateHardLinkW(link, existing, IntPtr.Zero)
                : Link(existing, link) == 0;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string fileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link([MarshalAs(UnmanagedType.LPUTF8Str)] string existing, [MarshalAs(UnmanagedType.LPUTF8Str)] string link);
}
