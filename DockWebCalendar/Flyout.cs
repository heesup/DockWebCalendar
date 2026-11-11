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

    // Panel widths in device-independent pixels; the height is the whole work area.
    private const double MinPanelWidth = 380;

    // Blank margin on each side of the page, so its content does not touch the panel
    // edges. The left one doubles as the resize grip.
    private const double SidePadding = 12;

    // Google Calendar's desktop layout needs about this many CSS pixels across before
    // its header and side menu stop being cut off. A narrower panel is zoomed out to fit.
    private const double PageLayoutWidth = 1040;

    // The week and day grids have a fixed height of about this many CSS pixels (24 hour
    // rows plus the header). Zoomed out further than the panel height allows, the grid
    // ends short of the bottom of the panel.
    private const double PageGridHeight = 1320;
    private const double MinZoom = 0.5;

    // A click on the Dock band first takes focus away (which hides the flyout) and then
    // asks to toggle it. Ignoring a toggle this soon after a hide keeps it closed.
    private static readonly TimeSpan ReopenGuard = TimeSpan.FromMilliseconds(400);

    // Cookies and the Google sign-in live here, separate from any installed browser.
    private static readonly string DataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DockWebCalendar");

    private static readonly string UserDataFolder = Path.Combine(DataFolder, "WebView2");
    private static readonly string WidthFile = Path.Combine(DataFolder, "panel-width.txt");

    private readonly bool _pinned;
    private readonly WebView2 _web = new();
    private Native.POINT _anchor;
    private DateTime _hiddenAt = DateTime.MinValue;
    private bool _browserStarted;
    private bool _signingIn;

    // Null until the user has resized the panel; the default then depends on the screen.
    private double? _panelWidth = LoadPanelWidth();

    public FlyoutWindow(bool pinned)
    {
        _pinned = pinned;
        Title = Program.ProductName;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;

        // The browser is a separate child window that swallows mouse input, so the
        // resize grip is a strip of the panel's own surface to its left.
        var grip = new Border
        {
            // Transparent, not null: an element without a background gets no mouse input.
            Background = Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.SizeWE,
            ToolTip = "Drag to resize",
        };
        grip.MouseLeftButtonDown += (_, args) => { grip.CaptureMouse(); args.Handled = true; };
        grip.MouseMove += (_, _) => { if (grip.IsMouseCaptured) { ResizeToPointer(); } };
        grip.MouseLeftButtonUp += (_, _) =>
        {
            if (grip.IsMouseCaptured)
            {
                grip.ReleaseMouseCapture();
                SavePanelWidth();
            }
        };

        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SidePadding) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SidePadding) });
        Grid.SetColumn(_web, 1);
        layout.Children.Add(grip);
        layout.Children.Add(_web);
        Content = layout;
        SizeChanged += (_, _) => ApplyZoom();
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

        // Google Calendar's page background, so the side margins blend into the page.
        Background = new SolidColorBrush(dark ? Color.FromRgb(0x1B, 0x1B, 0x1B) : Color.FromRgb(0xF8, 0xFA, 0xFD));

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
                ? System.Drawing.Color.FromArgb(0x1B, 0x1B, 0x1B)
                : System.Drawing.Color.FromArgb(0xF8, 0xFA, 0xFD);

            var environment = await CoreWebView2Environment.CreateAsync(null, UserDataFolder);
            await _web.EnsureCoreWebView2Async(environment);

            var core = _web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Auto;
            core.NewWindowRequested += OnNewWindowRequested;
            core.NavigationStarting += OnNavigationStarting;
            core.SourceChanged += OnSourceChanged;
            ApplyZoom();
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
        var panelWidth = _panelWidth ?? DefaultPanelWidth(work.Height / scale);
        var width = Math.Min((int)Math.Round(panelWidth * scale), work.Width);
        Native.MoveWindow(handle, work.Right - width, work.Top, width, work.Height);
    }

    /// <summary>
    /// The narrowest width at which the page both fits across and fills the panel from
    /// top to bottom: the zoom that makes the hour grid as tall as the panel, applied to
    /// the width the page layout needs.
    /// </summary>
    private static double DefaultPanelWidth(double panelHeight)
    {
        var zoom = Math.Clamp(panelHeight / PageGridHeight, MinZoom, 1.0);
        return (PageLayoutWidth * zoom) + (2 * SidePadding);
    }

    /// <summary>While the grip is dragged, the panel's left edge follows the pointer.</summary>
    private void ResizeToPointer()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var (_, work, scale) = Native.MonitorAt(_anchor);
        var minimum = (int)Math.Round(MinPanelWidth * scale);
        var width = Math.Clamp(work.Right - Native.CursorPosition().X, Math.Min(minimum, work.Width), work.Width);
        _panelWidth = width / scale;
        Native.MoveWindow(handle, work.Right - width, work.Top, width, work.Height);
    }

    /// <summary>
    /// Zooms the page out when the panel is narrower than the layout Google Calendar
    /// needs, as far as the panel's height allows.
    /// </summary>
    private void ApplyZoom()
    {
        if (_web.CoreWebView2 is null || ActualWidth <= 2 * SidePadding)
        {
            return;
        }

        // Never zoom out past the point where the hour grid stops reaching the bottom.
        // In a panel too narrow for that, Google Calendar switches to its own compact
        // layout (fewer header buttons, a week grid that scrolls sideways).
        var fitWidth = (ActualWidth - (2 * SidePadding)) / PageLayoutWidth;
        var fillHeight = ActualHeight / PageGridHeight;
        _web.ZoomFactor = Math.Clamp(Math.Max(fitWidth, fillHeight), MinZoom, 1.0);
    }

    private static double? LoadPanelWidth()
    {
        try
        {
            if (File.Exists(WidthFile) &&
                double.TryParse(File.ReadAllText(WidthFile), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var width) &&
                width >= MinPanelWidth && width <= 10000)
            {
                return width;
            }
        }
        catch (Exception)
        {
            // An unreadable file falls back to the default width.
        }

        return null;
    }

    private void SavePanelWidth()
    {
        try
        {
            Directory.CreateDirectory(DataFolder);
            File.WriteAllText(WidthFile, (_panelWidth ?? 0).ToString("0", System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception)
        {
            // The width is only a convenience; the panel still works at this size.
        }
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
