using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using OpenTK.Windowing.Desktop;

namespace SharpFAI.Player;

internal sealed class DisplayFrameLimit : IDisposable
{
    private const uint HighResolutionTimer = 0x2;
    private const uint TimerModifyStateAndSynchronize = 0x00100002;
    private static readonly long SpinThreshold = Stopwatch.Frequency / 2000;

    private SafeWaitHandle? _timer;
    private bool _timerChecked;
    private int _limit;
    private long _nextCheck;
    private long _framePeriod;
    private long _previousFrame;

    public void Update(GameWindow window, ApplicationSettings settings, bool settingsOrModeChanged)
    {
        long now = Stopwatch.GetTimestamp();
        if (settingsOrModeChanged || _nextCheck == 0 || settings.VSync && now >= _nextCheck)
            Configure(window, settings.GetFrameLimit(window), now);

        if (_framePeriod == 0)
            return;

        long deadline = _previousFrame + _framePeriod;
        long remaining = deadline - Stopwatch.GetTimestamp();
        if (remaining > SpinThreshold && !Sleep(remaining - SpinThreshold))
        {
            _timer?.Dispose();
            _timer = null;
            _framePeriod = 0;
            window.UpdateFrequency = _limit;
            return;
        }

        while ((now = Stopwatch.GetTimestamp()) < deadline)
            Thread.SpinWait(32);
        _previousFrame = now;
    }

    private void Configure(GameWindow window, int limit, long now)
    {
        // Read the live mode so moving between displays or changing their refresh rate updates the cap.
        _nextCheck = now + Stopwatch.Frequency;
        if (limit > 0 && !_timerChecked)
        {
            _timerChecked = true;
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17134))
            {
                var timer = CreateWaitableTimerExW(0, 0, HighResolutionTimer, TimerModifyStateAndSynchronize);
                if (timer.IsInvalid)
                    timer.Dispose();
                else
                {
                    _timer = timer;
                    // GameWindow.Run pins the render thread to CPU 0. Let Windows schedule it on any
                    // CPU allowed for this process so a timer wakeup can run when that core is busy.
                    if (GetProcessAffinityMask(GetCurrentProcess(), out nuint allowedCpus, out _) && allowedCpus != 0)
                        SetThreadAffinityMask(GetCurrentThread(), allowedCpus);
                }
            }
        }

        if (_limit != limit)
            _previousFrame = 0;
        _limit = limit;
        _framePeriod = _timer is not null && limit > 0 ? (long)Math.Ceiling(Stopwatch.Frequency / (double)limit) : 0;
        // OpenTK's Sleep/Yield loop can overshoot by several refresh periods on Windows.
        window.UpdateFrequency = _framePeriod > 0 ? 0 : limit;
    }

    private bool Sleep(long ticks)
    {
        long relative = -(long)Math.Ceiling(ticks * ((double)TimeSpan.TicksPerSecond / Stopwatch.Frequency));
        return SetWaitableTimer(_timer!, in relative, 0, 0, 0, false) && WaitForSingleObject(_timer!, 100) == 0;
    }

    public void Dispose() => _timer?.Dispose();

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern SafeWaitHandle CreateWaitableTimerExW(nint attributes, nint name, uint flags, uint access);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(SafeWaitHandle timer, in long dueTime, int period,
        nint completion, nint argument, [MarshalAs(UnmanagedType.Bool)] bool resume);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint timeout);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint GetCurrentThread();

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessAffinityMask(nint process, out nuint processMask, out nuint systemMask);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nuint SetThreadAffinityMask(nint thread, nuint mask);
}
