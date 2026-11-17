using System.Runtime.InteropServices;

namespace DockWebCalendar;

/// <summary>
/// Reports a mouse button pressed anywhere outside a window. Closing on focus loss alone
/// is not enough: Windows does not always let the panel take focus when it opens from
/// the Dock, and a window that never had focus never loses it.
/// </summary>
internal sealed class OutsideClickWatcher : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_XBUTTONDOWN = 0x020B;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public Native.POINT Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    private delegate IntPtr LowLevelMouseProc(int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hook, LowLevelMouseProc proc, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out Native.RECT rect);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Native.POINT point);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    private static readonly uint CurrentProcessId = (uint)Environment.ProcessId;

    private readonly IntPtr _window;
    private readonly Action<Native.POINT> _outsideClick;

    // Kept in a field so the garbage collector does not free it while Windows calls it.
    private readonly LowLevelMouseProc _proc;
    private IntPtr _hook;

    /// <param name="window">The window whose outside is watched.</param>
    /// <param name="outsideClick">
    /// Called with the pointer position. It runs inside the hook, so it should only queue work.
    /// </param>
    public OutsideClickWatcher(IntPtr window, Action<Native.POINT> outsideClick)
    {
        _window = window;
        _outsideClick = outsideClick;
        _proc = OnMouse;
    }

    /// <summary>Starts watching. Call on a thread that pumps messages, such as the UI thread.</summary>
    public void Start()
    {
        if (_hook == IntPtr.Zero)
        {
            _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
        }
    }

    public void Stop()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    public void Dispose() => Stop();

    private IntPtr OnMouse(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && (int)message is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN or WM_XBUTTONDOWN)
        {
            var point = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(data).Point;
            if (IsOutside(point))
            {
                _outsideClick(point);
            }
        }

        return CallNextHookEx(_hook, code, message, data);
    }

    private bool IsOutside(Native.POINT point)
    {
        if (GetWindowRect(_window, out var rect) &&
            point.X >= rect.Left && point.X < rect.Right && point.Y >= rect.Top && point.Y < rect.Bottom)
        {
            return false;
        }

        // A window of this process outside the panel's bounds still counts as inside.
        var under = WindowFromPoint(point);
        return under == IntPtr.Zero ||
            GetWindowThreadProcessId(under, out var processId) == 0 ||
            processId != CurrentProcessId;
    }
}
