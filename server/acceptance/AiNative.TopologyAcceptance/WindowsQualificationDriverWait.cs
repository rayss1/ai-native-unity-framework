using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

// Explicit Windows acceptance experiment; no global timer-resolution, priority or power-policy changes.
internal sealed class WindowsQualificationDriverWait : IQualificationDriverWait
{
    private readonly CancellationToken cancellation;
    private readonly TimerHandle timer;
    private readonly WaitHandle[] handles;

    internal WindowsQualificationDriverWait(CancellationToken cancellation)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("High-resolution input waiting requires Windows.");
        this.cancellation = cancellation;
        cancellation.ThrowIfCancellationRequested();
        // Auto-reset, unnamed and non-inheritable; TIMER_MODIFY_STATE | SYNCHRONIZE.
        SafeWaitHandle native = CreateWaitableTimerExW(IntPtr.Zero, null, 0x00000002, 0x00100002);
        if (native.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            native.Dispose();
            throw new Win32Exception(error, "High-resolution input timer creation failed.");
        }
        timer = new TimerHandle(native);
        try { handles = [cancellation.WaitHandle, timer]; }
        catch { timer.Dispose(); throw; }
    }

    public void Wait()
    {
        cancellation.ThrowIfCancellationRequested();
        long due = -10_000; // One-shot relative 1 ms, in 100 ns units; no APC or coalescing allowance.
        if (!SetWaitableTimerEx(timer.SafeWaitHandle, ref due, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "High-resolution input timer arming failed.");
        int signaled = WaitHandle.WaitAny(handles);
        cancellation.ThrowIfCancellationRequested();
        if (signaled != 1) throw new InvalidOperationException("Unexpected input timer wait result.");
    }

    // The owner has exited its wait before disposal. The cancellation handle belongs to its token source.
    public void Dispose() => timer.Dispose();

    private sealed class TimerHandle : WaitHandle
    {
        internal TimerHandle(SafeWaitHandle handle) => SafeWaitHandle = handle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimerEx(SafeWaitHandle timer, ref long dueTime, int period,
        IntPtr completion, IntPtr argument, IntPtr wakeContext, uint tolerableDelay);
}
