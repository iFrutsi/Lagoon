using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using NotchApp.Services;

namespace NotchApp;

/// <summary>Created only while open, then thrown away, so it costs nothing the rest of the time.</summary>
public partial class SettingsWindow : Window
{
    readonly AppSettings _settings;
    readonly Action _applyDock, _applyNotes;
    bool _loading = true;

    public SettingsWindow(AppSettings settings, Action applyDock, Action applyNotes)
    {
        _settings = settings;
        _applyDock = applyDock;
        _applyNotes = applyNotes;
        InitializeComponent();
        NotesSwitch.IsChecked = settings.NotchNotifications;
        DndSwitch.IsChecked = settings.DoNotDisturb;
        DndSwitch.IsEnabled = settings.NotchNotifications;
        DockSwitch.IsChecked = settings.DockEnabled;
        HideTaskbarSwitch.IsChecked = settings.HideTaskbar;
        HideTaskbarSwitch.IsEnabled = settings.DockEnabled;

        (settings.Theme == Theme.Glass ? ThemeGlass : ThemeBlack).IsChecked = true;
        StartupSwitch.IsChecked = AppSettings.StartWithWindows;
        WeatherCityBox.Text = settings.WeatherCity;
        foreach (var (city, _) in AppSettings.WorldCities) WorldCityBox.Items.Add(city);
        WorldCityBox.SelectedItem = settings.WorldCity;
        ClaudeSwitch.IsChecked = ClaudeAgents.IsConnected;
        _loading = false;

        // Dark title bar to match.
        SourceInitialized += (_, _) =>
        {
            int on = 1;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
        };
    }

    void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var theme = sender == ThemeGlass ? Theme.Glass : Theme.Black;
        _settings.Theme = theme;
        _settings.Save();
        ThemeManager.Apply(theme); // live: the notch changes immediately
    }

    void Startup_Click(object sender, RoutedEventArgs e) => AppSettings.StartWithWindows = StartupSwitch.IsChecked == true;

    void Dock_Click(object sender, RoutedEventArgs e)
    {
        _settings.DockEnabled = DockSwitch.IsChecked == true;
        _settings.HideTaskbar = HideTaskbarSwitch.IsChecked == true;
        HideTaskbarSwitch.IsEnabled = _settings.DockEnabled;
        _settings.Save();
        _applyDock();
    }

    void Notes_Click(object sender, RoutedEventArgs e)
    {
        _settings.NotchNotifications = NotesSwitch.IsChecked == true;
        _settings.DoNotDisturb = DndSwitch.IsChecked == true;
        DndSwitch.IsEnabled = _settings.NotchNotifications;
        _settings.Save();
        _applyNotes();
    }

    void WeatherCity_LostFocus(object sender, RoutedEventArgs e)
    {
        _settings.WeatherCity = WeatherCityBox.Text.Trim();
        _settings.Save();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        WeatherCity_LostFocus(this, new RoutedEventArgs()); // keep what was typed even without leaving the box
        base.OnClosing(e);
    }

    void WorldCity_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loading || WorldCityBox.SelectedItem is not string city) return;
        var match = AppSettings.WorldCities.First(c => c.City == city);
        _settings.WorldCity = match.City;
        _settings.WorldZone = match.Zone;
        _settings.Save();
    }

    void Claude_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ClaudeSwitch.IsChecked == true) ClaudeAgents.Connect();
            else ClaudeAgents.Disconnect();
            ClaudeHint.Text = ClaudeSwitch.IsChecked == true
                ? "Connected. New Claude Code sessions show up in the notch (restart open ones to pick it up)."
                : "Disconnected. The notch's hooks were removed from your Claude Code settings.";
        }
        catch (Exception ex)
        {
            ClaudeSwitch.IsChecked = ClaudeAgents.IsConnected;
            ClaudeHint.Text = "Couldn't update Claude Code settings: " + ex.Message;
        }
    }

    void Quit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
