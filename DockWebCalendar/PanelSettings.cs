using System.Globalization;
using System.Text.Json;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace DockWebCalendar;

/// <summary>
/// The extension's options, shown on its settings page in Command Palette and kept in
/// %LocalAppData%\DockWebCalendar\settings.json.
/// </summary>
internal sealed class PanelSettings
{
    private const string KeepBrowserKey = "keepBrowser";
    private const string Never = "never";

    private static readonly int[] MinuteChoices = [5, 10, 30, 60];

    private readonly string _path;
    private readonly Settings _settings = new();
    private readonly ChoiceSetSetting _keepBrowser;

    public PanelSettings(string path)
    {
        _path = path;

        var choices = new List<ChoiceSetSetting.Choice> { new("Close it right away (no memory used, slower to open)", "0") };
        choices.AddRange(MinuteChoices.Select(minutes => new ChoiceSetSetting.Choice(
            minutes == 60 ? "Keep it for 1 hour" : $"Keep it for {minutes} minutes",
            minutes.ToString(CultureInfo.InvariantCulture))));
        choices.Add(new ChoiceSetSetting.Choice("Keep it until Command Palette exits (fastest, about 650 MB)", Never));

        _keepBrowser = new ChoiceSetSetting(
            KeepBrowserKey,
            "After the panel closes",
            "What happens to the browser that shows Google Calendar. Keeping it makes the panel open at once; closing it frees its memory, and the next open takes a few seconds.",
            choices)
        {
            Value = "0",
        };
        _settings.Add(_keepBrowser);

        try
        {
            if (File.Exists(_path))
            {
                _settings.Update(File.ReadAllText(_path));
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            // Keep the defaults when the settings file cannot be read.
        }

        _settings.SettingsChanged += (_, _) => Persist();
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DockWebCalendar", "settings.json");

    public Settings ToolkitSettings => _settings;

    /// <summary>
    /// How long the browser is kept after the panel closes: zero closes it at once, and
    /// null keeps it for as long as the process runs. A hand-edited number of minutes in
    /// the file is honoured; anything unreadable means "close at once".
    /// </summary>
    public TimeSpan? KeepBrowserFor
    {
        get
        {
            var value = _keepBrowser.Value;
            if (string.Equals(value, Never, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes) && minutes > 0 && minutes <= 24 * 60
                ? TimeSpan.FromMinutes(minutes)
                : TimeSpan.Zero;
        }
    }

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, _settings.ToJson());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The choice still applies for this run.
        }
    }
}
