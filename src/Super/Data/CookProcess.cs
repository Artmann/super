using System.ComponentModel;
using System.Diagnostics;

namespace Super;

/// <summary>Something cooking content while the game runs. Disposing it stops it.</summary>
internal interface ICookProcess : IDisposable
{
}

/// <summary>
/// <c>super-cook --watch</c>, started by a game run from source. It cooks the sources again on every
/// save, and the game hears the new manifest. Its output goes to the game's error stream, so cook
/// errors show up where the game's own messages do. It's stopped, with anything it started, when
/// it's disposed or the game exits. Its stdin is a pipe the game holds open and never writes to, so
/// if the game dies without either, the pipe closes, the tool sees its stdin end and stops by itself.
/// </summary>
internal sealed class CookProcess : ICookProcess
{
    private readonly Process _process;
    private int _stopped;

    public CookProcess(DevSettings dev, string outFolder)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in Arguments(dev, outFolder))
        {
            start.ArgumentList.Add(argument);
        }

        _process = new Process { StartInfo = start };
        _process.OutputDataReceived += Copy;
        _process.ErrorDataReceived += Copy;
        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        AppDomain.CurrentDomain.ProcessExit += OnExit;
    }

    /// <summary>What <c>dotnet</c> is given: the tool, then its watch-mode options.</summary>
    internal static IReadOnlyList<string> Arguments(DevSettings dev, string outFolder)
    {
        var arguments = new List<string> { dev.Tool, "--watch" };
        foreach (string root in dev.Roots)
        {
            arguments.Add("--content");
            arguments.Add(root);
            foreach (string pattern in dev.Only?.GetValueOrDefault(root) ?? [])
            {
                arguments.Add("--only");
                arguments.Add(pattern);
            }
        }

        arguments.AddRange(["--out", outFolder, "--target", dev.Target]);
        return arguments;
    }

    public void Dispose()
    {
        AppDomain.CurrentDomain.ProcessExit -= OnExit;
        Stop();
    }

    private void OnExit(object? sender, EventArgs e) => Stop();

    private void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 1)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(1000);
            }
        }
        catch (Exception problem) when (problem is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // It ended on its own while this was stopping it.
        }
        finally
        {
            _process.Dispose();
        }
    }

    private static void Copy(object sender, DataReceivedEventArgs line)
    {
        if (line.Data is not null)
        {
            Console.Error.WriteLine(line.Data);
        }
    }
}
