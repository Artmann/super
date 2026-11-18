namespace Super.Cook.Tests;

/// <summary>Fresh folders under the system temp folder, deleted with the test class.</summary>
public abstract class TempFolders : IDisposable
{
    private readonly List<string> _folders = [];

    public void Dispose()
    {
        foreach (string folder in _folders)
        {
            Directory.Delete(folder, true);
        }

        GC.SuppressFinalize(this);
    }

    protected string Temp()
    {
        string folder = Path.Combine(Path.GetTempPath(), "super-cook-" + Guid.NewGuid().ToString("N"));
        _folders.Add(folder);
        Directory.CreateDirectory(folder);
        return folder;
    }
}
