using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Super;

/// <summary>
/// Keeps frames to a steady rate. Each frame gets a slot one period after the last, so a frame
/// that runs a little long is made up by a shorter wait, and the rate holds on average.
/// </summary>
internal sealed class FrameLimiter(TimeProvider clock)
{
    private long _deadline;
    private int _rate;

    /// <summary>
    /// How long to wait, from now, before starting the next frame at this many frames a second.
    /// 0 is no limit. A frame more than one period late starts the count again rather than
    /// rushing the frames after it to catch up.
    /// </summary>
    public TimeSpan Next(int framesPerSecond)
    {
        long now = clock.GetTimestamp();
        if (framesPerSecond <= 0)
        {
            _rate = 0;
            return TimeSpan.Zero;
        }

        long period = clock.TimestampFrequency / framesPerSecond;
        long slot = _deadline + period;
        bool restart = framesPerSecond != _rate || now - slot > period;
        _rate = framesPerSecond;
        _deadline = restart ? now + period : slot;
        return now >= _deadline ? TimeSpan.Zero : clock.GetElapsedTime(now, _deadline);
    }

    /// <summary>
    /// Waits about this long. The operating system can only wake a sleeping thread so precisely,
    /// so the last couple of milliseconds are spent checking the clock instead.
    /// </summary>
    public static void Wait(TimeSpan time)
    {
        long end = Stopwatch.GetTimestamp() + (long)(time.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), end) > SpinTime)
        {
            Thread.Sleep(1);
        }

        while (Stopwatch.GetTimestamp() < end)
        {
            Thread.SpinWait(32);
        }
    }

    /// <summary>
    /// Windows normally wakes a sleeping thread only every 15.6 milliseconds, too coarse for a
    /// frame of 16.7. This asks for 1 until the returned object is disposed.
    /// </summary>
    public static IDisposable FineSleep() => OperatingSystem.IsWindows() ? new WindowsTimer() : new Nothing();

    private static readonly TimeSpan SpinTime = TimeSpan.FromMilliseconds(2);

    private sealed class WindowsTimer : IDisposable
    {
        private bool _disposed;

        public WindowsTimer() => _ = timeBeginPeriod(1);

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _ = timeEndPeriod(1);
            }
        }

        [DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint milliseconds);

        [DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint milliseconds);
    }

    private sealed class Nothing : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
