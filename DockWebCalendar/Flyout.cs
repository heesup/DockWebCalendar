using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;

namespace DockWebCalendar;

/// <summary>
/// Runs the flyout window on its own STA thread. The extension's COM calls arrive on
/// thread-pool threads, which cannot own WPF windows.
/// </summary>
internal sealed class FlyoutHost
{
    private readonly bool _pinned;
    private readonly bool _exitWhenHidden;
    private readonly ManualResetEventSlim _ready = new();
    private Thread? _thread;
    private Dispatcher? _dispatcher;
    private FlyoutWindow? _window;

    /// <param name="pinned">Stay open when the window loses focus.</param>
    /// <param name="exitWhenHidden">Standalone run: closing the flyout ends the process.</param>
    public FlyoutHost(bool pinned, bool exitWhenHidden)
    {
        _pinned = pinned;
        _exitWhenHidden = exitWhenHidden;
    }

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = !_exitWhenHidden, Name = "DockWebCalendar UI" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
    }

    /// <summary>Shows the flyout next to the mouse pointer, or hides it when it is open.</summary>
    public void Toggle()
    {
        var cursor = Native.CursorPosition();
        _dispatcher?.BeginInvoke(() => _window?.Toggle(cursor));
    }

    public void WaitForExit() => _thread?.Join();

    public void Shutdown() => _dispatcher?.BeginInvokeShutdown(DispatcherPriority.Normal);

    private void Run()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        _dispatcher = Dispatcher.CurrentDispatcher;
        _window = new FlyoutWindow(_pinned);
        if (_exitWhenHidden)
        {
            _window.Hidden += (_, _) => application.Shutdown();
        }

        _ready.Set();
        application.Run();
    }
}

/// <summary>
/// A borderless side panel holding one WebView2 that shows Google Calendar. The browser is
/// created on first use and then kept, so later clicks open on the page as it was left.
/// </summary>
internal sealed class FlyoutWindow : Window
{
    private const string CalendarUrl = "https://calendar.google.com/calendar/u/0/r";
    private const string SignInUrl = "https://accounts.google.com/ServiceLogin?service=cl&continue=https%3A%2F%2Fcalendar.google.com%2Fcalendar%2Fu%2F0%2Fr";

    // Width of the side panel in device-independent pixels; the height is the whole work area.
    private const double PanelWidth = 520;

    // A click on the Dock band first takes focus away (which hides the flyout) and then
    // asks to toggle it. Ignoring a toggle this soon after a hide keeps it closed.
    private static readonly TimeSpan ReopenGuard = TimeSpan.FromMilliseconds(400);

    // Cookies and the Google sign-in live here, separate from any installed browser.
    private static readonly string UserDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DockWebCalendar", "WebView2");

    private readonly bool _pinned;
    private readonly WebView2 _web = new();
    private Native.POINT _anchor;
    private DateTime _hiddenAt = DateTime.MinValue;
    private bool _browserStarted;
    private bool _signingIn;

    public FlyoutWindow(bool pinned)
    {
        _pinned = pinned;
        Title = Program.ProductName;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Content = _web;
    }

    /// <summary>Raised after the flyout has been hidden.</summary>
    public event EventHandler? Hidden;

    public void Toggle(Native.POINT cursor)
    {
        if (IsVisible)
        {
            HideFlyout();
            return;
        }

        if (DateTime.UtcNow - _hiddenAt < ReopenGuard)
        {
            return;
        }

        _anchor = cursor;
        ShowFlyout();
    }

    private void ShowFlyout()
    {
        var dark = IsDarkTheme();
        Background = new SolidColorBrush(dark ? Color.FromRgb(0x1F, 0x1F, 0x1F) : Colors.White);

        var handle = new WindowInteropHelper(this).EnsureHandle();
        Native.ApplyFlyoutChrome(handle, dark);

        // Placed twice: showing the window on a monitor with another scale resizes it.
        Place(handle);
        Show();
        Place(handle);
        Native.ForceForeground(handle);
        Activate();
        _web.Focus();

        if (!_browserStarted)
        {
            _browserStarted = true;
            _ = StartBrowserAsync(dark);
        }
    }

    private void HideFlyout()
    {
        if (!IsVisible)
        {
            return;
        }

        Hide();
        _hiddenAt = DateTime.UtcNow;
        Hidden?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);

        // Signing in takes focus away more than once: a passkey or Windows Hello prompt,
        // a password manager, a phone-code app. Hiding then would abandon the sign-in,
        // so the panel stays until it is toggled from the Dock or closed with Alt+F4.
        if (!_pinned && !_signingIn)
        {
            HideFlyout();
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Alt+F4 hides the panel; the browser and its session are kept for the next click.
        e.Cancel = true;
        HideFlyout();
    }

    private void OnSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs args)
    {
        _signingIn = _web.Source?.Host == "accounts.google.com";

        // A topmost panel could cover the system's own sign-in prompts.
        Topmost = !_signingIn;
    }

    private async Task StartBrowserAsync(bool dark)
    {
        try
        {
            // Painted while the page loads, instead of a white flash in dark mode.
            _web.DefaultBackgroundColor = dark
                ? System.Drawing.Color.FromArgb(0x1F, 0x1F, 0x1F)
                : System.Drawing.Color.White;

            var environment = await CoreWebView2Environment.CreateAsync(null, UserDataFolder);
            await _web.EnsureCoreWebView2Async(environment);

            var core = _web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Auto;
            core.NewWindowRequested += OnNewWindowRequested;
            core.NavigationStarting += OnNavigationStarting;
            core.SourceChanged += OnSourceChanged;
            core.Navigate(CalendarUrl);
        }
        catch (Exception exception)
        {
            // Most likely the WebView2 runtime is missing or its data folder is locked.
            _browserStarted = false;
            Content = new TextBlock
            {
                Text = "Google Calendar could not be loaded.\n\n" + exception.Message,
                Foreground = dark ? Brushes.White : Brushes.Black,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(32),
            };
        }
    }

    /// <summary>
    /// Signed out, Google Calendar redirects to a Workspace marketing page. Go straight
    /// to the sign-in form instead, set to come back to the calendar afterwards.
    /// </summary>
    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) && uri.Host == "workspace.google.com")
        {
            args.Cancel = true;
            _web.CoreWebView2.Navigate(SignInUrl);
        }
    }

    /// <summary>
    /// Google's own sign-in and account pages stay in the flyout; every other link that
    /// wants a new window (a Meet call, an attachment, a map) opens in the default browser.
    /// </summary>
    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return;
        }

        if (uri.Host is "accounts.google.com" or "calendar.google.com")
        {
            _web.CoreWebView2.Navigate(args.Uri);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(args.Uri) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // No default browser: nothing useful to do.
        }
    }

    /// <summary>
    /// Docks the window to the right edge of the monitor the pointer is on, filling the
    /// work area from top to bottom, like the macOS Notification Centre sidebar.
    /// </summary>
    private void Place(IntPtr handle)
    {
        var (_, work, scale) = Native.MonitorAt(_anchor);
        var width = Math.Min((int)Math.Round(PanelWidth * scale), work.Width);
        Native.MoveWindow(handle, work.Right - width, work.Top, width, work.Height);
    }

    private static bool IsDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
