using System.Diagnostics.Tracing;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;

namespace Super.Profiling;

/// <summary>
/// Records what the program is doing about a thousand times a second, using the sampling
/// profiler built into .NET, so nothing in a game has to be marked up. Native code shows
/// up as the C# call that entered it.
/// </summary>
internal sealed class CpuSampler
{
    private const string SampleProvider = "Microsoft-DotNETCore-SampleProfiler";
    private const string RuntimeProvider = "Microsoft-Windows-DotNETRuntime";
    private const int BufferMegabytes = 512;
    private const uint TimerMilliseconds = 1;
    private const int ProcessPowerThrottling = 4;
    private const uint IgnoreTimerResolution = 4;

    private readonly string _tracePath;
    private readonly EventPipeSession _session;
    private readonly Task _copying;

    private CpuSampler(string tracePath)
    {
        _tracePath = tracePath;
        var client = new DiagnosticsClient(Environment.ProcessId);
        _session = client.StartEventPipeSession(
            [
                new EventPipeProvider(SampleProvider, EventLevel.Informational),
                new EventPipeProvider(RuntimeProvider, EventLevel.Informational, (long)ClrTraceEventParser.Keywords.Default),
            ],
            requestRundown: true,
            circularBufferMB: BufferMegabytes);
        _copying = Task.Run(() =>
        {
            using var file = File.Create(tracePath);
            _session.EventStream.CopyTo(file);
        });
    }

    /// <summary>Starts recording into a .nettrace file, which tools like PerfView can open too.</summary>
    public static CpuSampler Start(string tracePath)
    {
        if (OperatingSystem.IsWindows())
        {
            UseFineTimer();
        }

        return new CpuSampler(tracePath);
    }

    /// <summary>
    /// Stops recording, and returns the stacks that went through <paramref name="method"/>,
    /// from just below it down to the running function. This leaves out other threads,
    /// and anything before or after the frames being measured.
    /// </summary>
    public IReadOnlyList<string[]> Stop(string method)
    {
        _session.Stop();
        _copying.Wait();
        _session.Dispose();
        if (OperatingSystem.IsWindows())
        {
            _ = timeEndPeriod(TimerMilliseconds);
        }

        string etlxPath = Path.ChangeExtension(_tracePath, ".etlx");
        TraceLog.CreateFromEventPipeDataFile(_tracePath, etlxPath);
        try
        {
            using var log = new TraceLog(etlxPath);
            return ReadStacks(log, method);
        }
        finally
        {
            File.Delete(etlxPath);
        }
    }

    /// <summary>
    /// Windows ticks every 15.6 ms unless asked otherwise, which leaves far too few samples.
    /// It also ignores the request from programs with no visible window, like a profile
    /// run, unless they opt out of that.
    /// </summary>
    private static unsafe void UseFineTimer()
    {
        var state = new ProcessPowerThrottlingState
        {
            Version = 1,
            ControlMask = IgnoreTimerResolution,
            StateMask = 0,
        };
        _ = SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, &state, (uint)sizeof(ProcessPowerThrottlingState));
        _ = timeBeginPeriod(TimerMilliseconds);
    }

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint milliseconds);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint milliseconds);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern unsafe int SetProcessInformation(nint process, int informationClass, void* information, uint size);

    private static List<string[]> ReadStacks(TraceLog log, string method)
    {
        var stacks = new List<string[]>();
        var names = new List<string>();
        foreach (var sample in log.Events)
        {
            if (sample.ProviderName != SampleProvider)
            {
                continue;
            }

            names.Clear();
            bool found = false;
            for (var stack = sample.CallStack(); stack is not null; stack = stack.Caller)
            {
                string name = NameOf(stack.CodeAddress);
                if (name.Contains(method, StringComparison.Ordinal))
                {
                    found = true;
                    break;
                }

                names.Add(name);
            }

            if (found && names.Count > 0)
            {
                names.Reverse();
                stacks.Add(names.ToArray());
            }
        }

        return stacks;
    }

    private static string NameOf(TraceCodeAddress address)
    {
        string name = address.FullMethodName;
        if (string.IsNullOrEmpty(name))
        {
            return $"{address.ModuleName}!?";
        }

        int arguments = name.IndexOf('(');
        return arguments > 0 ? name[..arguments] : name;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessPowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }
}
