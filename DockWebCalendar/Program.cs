using System.Runtime.InteropServices;
using JPSoftworks.CommandPalette.Extensions.Toolkit;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace DockWebCalendar;

public static class Program
{
    // The extension, the command its band runs, and the band as listed under Edit Dock.
    internal const string ProductName = "Dock Web Calendar";
    internal const string CommandName = "Google Calendar";
    internal const string BandName = "Date (Google Calendar)";

    [MTAThread]
    public static int Main(string[] args)
    {
        // Command Palette starts the packaged exe as a COM server.
        if (args.Any(arg => arg.Equals("-RegisterProcessAsComServer", StringComparison.OrdinalIgnoreCase)))
        {
            ExtensionHostRunner.RunAsync(
                args,
                new ExtensionHostRunnerParameters
                {
                    PublisherMoniker = "DockWebCalendar",
                    ProductMoniker = "DockWebCalendar",
                    ExtensionFactories = new()
                    {
                        new DelegateExtensionFactory(disposed => new DockWebCalendarExtension(disposed)),
                    },
                }).GetAwaiter().GetResult();
            return 0;
        }

        // Development run: a throwaway panel that ends the process when it closes.
        //   --pin    stay open when the window loses focus
        if (args.Contains("--pin"))
        {
            var pinned = new FlyoutHost(pinned: true, exitWhenHidden: true);
            pinned.Start();
            pinned.Toggle();
            pinned.WaitForExit();
            return 0;
        }

        // Started as an app, for example from an icon pinned to the Dock or Start. If the
        // extension (or an earlier launch) is running, it owns the panel: toggle that one.
        if (ToggleSignal.TrySignalRunningInstance())
        {
            return 0;
        }

        // Otherwise this process becomes the one that stays and owns the panel.
        var settings = new PanelSettings(PanelSettings.DefaultPath);
        var host = new FlyoutHost(pinned: false, exitWhenHidden: false, () => settings.KeepBrowserFor);
        host.Start();
        ToggleSignal.Listen(host.Toggle);
        host.Toggle();
        host.WaitForExit();
        return 0;
    }
}

[Guid("b37e2a94-6c1d-4f0b-8a53-2e9d7c4f1a06")]
public sealed partial class DockWebCalendarExtension : IExtension, IDisposable
{
    private readonly ManualResetEvent _disposed;
    private readonly DockWebCalendarCommandsProvider _provider = new();

    public DockWebCalendarExtension(ManualResetEvent disposed)
    {
        _disposed = disposed;
    }

    public object? GetProvider(ProviderType providerType) =>
        providerType == ProviderType.Commands ? _provider : null;

    public void Dispose()
    {
        _provider.Dispose();
        _disposed.Set();
    }
}

public sealed partial class DockWebCalendarCommandsProvider : CommandProvider, IDisposable
{
    private readonly PanelSettings _settings;
    private readonly FlyoutHost _host;
    private readonly ListItem _bandItem;
    private readonly WrappedDockItem _dockItem;
    private readonly Timer _dateTimer;

    public DockWebCalendarCommandsProvider()
    {
        Id = "DockWebCalendar";
        DisplayName = Program.ProductName;
        Icon = new IconInfo("");

        _settings = new PanelSettings(PanelSettings.DefaultPath);
        Settings = _settings.ToolkitSettings;

        _host = new FlyoutHost(pinned: false, exitWhenHidden: false, () => _settings.KeepBrowserFor);
        _host.Start();

        ToggleSignal.Listen(_host.Toggle);

        var toggle = new ToggleCommand(_host);

        // Settings live in the band's context menu (right-click it on the Dock).
        IContextItem[] contextCommands =
        [
            new CommandContextItem(_settings.ToolkitSettings.SettingsPage)
            {
                Title = "Settings",
                Subtitle = "Keep or close the browser after the panel closes",
                Icon = new IconInfo(""),
            },
        ];
        // The band is text only. Its command carries no icon either, because Command Palette
        // falls back to the command's icon when the item has none.
        _bandItem = new ListItem(toggle) { Title = DateText(), MoreCommands = contextCommands };
        _dockItem = new WrappedDockItem([_bandItem], "dockwebcalendar.dock.calendar", Program.BandName);

        // The band text is set in place; ItemsChanged would make Command Palette rebuild the whole Dock.
        _dateTimer = new Timer(_ => _bandItem.Title = DateText(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    // The extension is its Dock band only: nothing is listed in the palette.
    public override ICommandItem[] TopLevelCommands() => [];

    public override ICommandItem[]? GetDockBands() => [_dockItem];

    private static string DateText() => ShortDate(DateTime.Now);

    /// <summary>
    /// The date as short as the culture allows, because the Dock cuts long band titles
    /// off: the culture's month-and-day pattern with the month abbreviated, then the
    /// weekday. "Nov 10 (Tue)" in English, "11월 10일 (화)" in Korean.
    /// </summary>
    internal static string ShortDate(DateTime date)
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        var monthDay = culture.DateTimeFormat.MonthDayPattern.Replace("MMMM", "MMM");
        return $"{date.ToString(monthDay, culture)} ({date.ToString("ddd", culture)})";
    }

    public override void Dispose()
    {
        _dateTimer.Dispose();
        _host.Shutdown();
        base.Dispose();
    }
}

internal sealed partial class ToggleCommand : InvokableCommand
{
    private readonly FlyoutHost _host;

    public ToggleCommand(FlyoutHost host)
    {
        _host = host;
        Id = "dockwebcalendar.command.toggle";
        Name = Program.CommandName;
    }

    public override ICommandResult Invoke()
    {
        _host.Toggle();
        return CommandResult.KeepOpen();
    }
}
