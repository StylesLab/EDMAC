using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;

namespace EDMAC;

public sealed class PlaybackPerformanceScope : IDisposable
{
    private const uint TimerPeriodMilliseconds = 1;
    private readonly Process currentProcess;
    private readonly ProcessPriorityClass originalPriorityClass;
    private readonly GCLatencyMode originalLatencyMode;
    private readonly bool timerPeriodChanged;
    private bool disposed;

    private PlaybackPerformanceScope()
    {
        currentProcess = Process.GetCurrentProcess();
        originalPriorityClass = currentProcess.PriorityClass;
        originalLatencyMode = GCSettings.LatencyMode;

        TrySetProcessPriority(ProcessPriorityClass.AboveNormal);
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

        timerPeriodChanged = OperatingSystem.IsWindows() &&
            TimeBeginPeriod(TimerPeriodMilliseconds) == 0;
    }

    public static PlaybackPerformanceScope Start()
    {
        return new PlaybackPerformanceScope();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        if (timerPeriodChanged)
        {
            TimeEndPeriod(TimerPeriodMilliseconds);
        }

        GCSettings.LatencyMode = originalLatencyMode;
        TrySetProcessPriority(originalPriorityClass);
        currentProcess.Dispose();
    }

    private void TrySetProcessPriority(ProcessPriorityClass priorityClass)
    {
        try
        {
            currentProcess.PriorityClass = priorityClass;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            PlatformNotSupportedException or
            System.ComponentModel.Win32Exception)
        {
        }
    }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint period);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint period);
}
