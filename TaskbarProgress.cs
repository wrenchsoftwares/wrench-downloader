using System;
using System.Runtime.InteropServices;

namespace WrenchDownloader;

/// <summary>
/// Windows taskbar progress (the same green bar File Explorer shows when
/// copying). Aggregate download progress is mirrored onto the app's taskbar
/// button: normal while downloading, paused when the queue is paused,
/// error while failures need attention, hidden when idle.
/// </summary>
public static class TaskbarProgress
{
    [Flags]
    public enum TaskbarState
    {
        NoProgress = 0,
        Indeterminate = 0x1,
        Normal = 0x2,
        Error = 0x4,
        Paused = 0x8
    }

    // ITaskbarList3 vtable must be declared in order up to the used methods.
    [ComImport]
    [Guid("56FDF344-FD6D-11d0-BFC2-00C04FC995C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        void HrInit();
        void AddTabWindow(IntPtr hwnd);
        void DeleteTabWindow(IntPtr hwnd);
        void ActivateTabWindow(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);
        void SetProgressValue(IntPtr hwnd, ulong ullCompleted, ulong ullTotal);
        void SetProgressState(IntPtr hwnd, TaskbarState tbpFlags);
    }

    [DllImport("ole32.dll", PreserveSig = true)]
    private static extern int CoCreateInstance(
        ref Guid rclsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out ITaskbarList3 ppv);

    private static ITaskbarList3? _taskbar;
    private static bool _initialized;

    private static bool EnsureInitialized()
    {
        if (_initialized) return _taskbar != null;
        _initialized = true;
        try
        {
            Guid clsid = new("56FDF342-FD6D-11d0-BFC2-00C04FC995C1");
            Guid iid = new("56FDF344-FD6D-11d0-BFC2-00C04FC995C1");
            int hr = CoCreateInstance(ref clsid, IntPtr.Zero, 1 /* CLSCTX_INPROC_SERVER */, ref iid, out ITaskbarList3? taskbar);
            if (hr == 0 && taskbar != null)
            {
                taskbar.HrInit();
                _taskbar = taskbar;
            }
            else
            {
                _taskbar = null;
            }
        }
        catch
        {
            _taskbar = null;
        }
        return _taskbar != null;
    }

    /// <summary>completed/total in the same units (e.g. permille for smoothness).</summary>
    public static void Update(IntPtr hwnd, ulong completed, ulong total, TaskbarState state)
    {
        try
        {
            if (hwnd == IntPtr.Zero || !EnsureInitialized() || _taskbar == null)
                return;
            if (total == 0) total = 1;
            if (completed > total) completed = total;
            _taskbar.SetProgressState(hwnd, state);
            if (state is TaskbarState.Normal or TaskbarState.Paused or TaskbarState.Error)
                _taskbar.SetProgressValue(hwnd, completed, total);
        }
        catch { }
    }

    public static void Reset(IntPtr hwnd)
    {
        try
        {
            if (hwnd == IntPtr.Zero || !EnsureInitialized() || _taskbar == null)
                return;
            _taskbar.SetProgressState(hwnd, TaskbarState.NoProgress);
        }
        catch { }
    }
}
