using System.Runtime.InteropServices;
using JPSoftworks.CommandPalette.Extensions.Toolkit;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace DockWebCalendar;

public static class Program
{
    internal const string ProductName = "Dock Web Calendar";

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

        // Started by hand: show the flyout without Command Palette.
        //   --pin    stay open when the window loses focus
        var host = new FlyoutHost(pinned: args.Contains("--pin"), exitWhenHidden: true);
        host.Start();
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
    private readonly FlyoutHost _host;
    private readonly ListItem _bandItem;
    private readonly WrappedDockItem _dockItem;
    private readonly ICommandItem[] _commands;
    private readonly Timer _dateTimer;

    public DockWebCalendarCommandsProvider()
    {
        Id = "DockWebCalendar";
        DisplayName = Program.ProductName;
        Icon = new IconInfo("");

        _host = new FlyoutHost(pinned: false, exitWhenHidden: false);
        _host.Start();

        var toggle = new ToggleCommand(_host);
        _bandItem = new ListItem(toggle) { Icon = new IconInfo(""), Title = DateText() };
        _dockItem = new WrappedDockItem([_bandItem], "dockwebcalendar.dock.calendar", Program.ProductName);
        _commands =
        [
            new CommandItem(toggle)
            {
                Title = Program.ProductName,
                Subtitle = "Google Calendar",
                Icon = new IconInfo(""),
            },
        ];

        // The band text is set in place; ItemsChanged would make Command Palette rebuild the whole Dock.
        _dateTimer = new Timer(_ => _bandItem.Title = DateText(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public override ICommandItem[] TopLevelCommands() => _commands;

    public override ICommandItem[]? GetDockBands() => [_dockItem];

    private static string DateText() => $"{DateTime.Now:M} ({DateTime.Now:ddd})";

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
        Name = Program.ProductName;
        Icon = new IconInfo("");
    }

    public override ICommandResult Invoke()
    {
        _host.Toggle();
        return CommandResult.KeepOpen();
    }
}
