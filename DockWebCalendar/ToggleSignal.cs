namespace DockWebCalendar;

/// <summary>
/// Lets a second launch of the exe reach the instance that is already running. Pinning
/// the app itself to the Dock or Start starts a new process on every click; that process
/// signals the running one to toggle its window and exits, so there is only ever one window.
/// </summary>
internal static class ToggleSignal
{
    private const int AnyProcess = -1;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    private const string Name = @"Local\DockWebCalendar.Toggle";

    /// <summary>Asks a running instance to toggle its window. False when there is none.</summary>
    public static bool TrySignalRunningInstance()
    {
        if (!EventWaitHandle.TryOpenExisting(Name, out var signal))
        {
            return false;
        }

        using (signal)
        {
            // This process was started by the user's click, so Windows lets it take the
            // foreground. Hand that right to the running instance before asking it to show.
            AllowSetForegroundWindow(AnyProcess);
            signal.Set();
        }

        return true;
    }

    /// <summary>Calls <paramref name="toggle"/> whenever another launch signals this instance.</summary>
    public static void Listen(Action toggle)
    {
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, Name);
        var thread = new Thread(() =>
        {
            while (true)
            {
                signal.WaitOne();
                toggle();
            }
        })
        {
            IsBackground = true,
            Name = "DockWebCalendar toggle signal",
        };
        thread.Start();
    }
}