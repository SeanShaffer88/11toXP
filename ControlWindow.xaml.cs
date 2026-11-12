using System.ComponentModel;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using Microsoft.Win32;
using LunaDesktop.State;
using LunaDesktop.Appearance;
using LunaDesktop.Shell;

namespace LunaDesktop;

public partial class ControlWindow : Window
{
    private readonly App app;
    private readonly bool preview;
    private string? image;
    private bool previewApplied;
    private string? message;
    private readonly ObservableCollection<Shortcut> launchItems = [];
    private readonly Dictionary<string, object> normalBrushes = [];
    private static readonly Shortcut[] ShortcutChoices =
    [
        new(QuickLaunchItem.FileExplorer, "File Explorer"),
        new(QuickLaunchItem.Settings, "Windows Settings"),
        new(QuickLaunchItem.XpExplorer, "XP Explorer")
    ];

    internal ControlWindow(App app, bool preview)
    {
        this.app = app;
        this.preview = preview;
        InitializeComponent();
        foreach (var key in new[]
        {
            "Panel", "Ink", "Rule", "StatusPanel", "CaptionInk", "Frame", "TitleFill", "ButtonFill",
            "ButtonEdge", "ButtonHover", "ButtonPressed", "CaptionClose", "DisabledInk"
        })
            normalBrushes.Add(key, Resources[key]);
        LaunchList.ItemsSource = launchItems;
        LoadPreferences(app.Coordinator?.State?.Preferences ?? ThemePreferences.Default);
        ApplyHighContrast();
        SystemParameters.StaticPropertyChanged += SystemParametersChanged;
        if (!preview && app.Coordinator?.State?.WallpaperSelection is { } wallpaper)
        {
            image = wallpaper.Requested;
            WallpaperOptIn.IsChecked = true;
        }
        if (!preview && app.StateDirectory is { } directory)
            Loaded += (_, _) => WindowPosition.Restore(this, directory);
        RefreshStatus();
    }

    internal void RefreshStatus()
    {
        var coordinator = app.Coordinator;
        var state = coordinator?.State;
        var active = preview ? previewApplied : state?.Phase == ThemePhase.Active;
        ApplyAction.IsEnabled = preview ? !previewApplied : coordinator is not null && state is null;
        RepairAction.IsEnabled = active;
        RestoreAction.IsEnabled = preview ? previewApplied : state is not null;
        RestoreAndExitAction.IsEnabled = preview || coordinator is not null;
        Components.IsEnabled = TaskbarSettings.IsEnabled = preview || (coordinator is not null && (state is null || active));
        WallpaperOptIn.IsEnabled = preview || (coordinator is not null && (state is null || active));
        WallpaperPicker.IsEnabled = WallpaperOptIn.IsEnabled && WallpaperOptIn.IsChecked == true;
        ShellValue.Text = preview && active ? "XP taskbar preview" : coordinator?.ShellStatus ?? "Windows taskbar";
        AppearanceValue.Text = preview && active ? "XP-style preview" : coordinator?.AppearanceStatus ?? "Windows settings";
        WallpaperValue.Text = coordinator?.WallpaperStatus ?? "Unmanaged";
        StartupValue.Text = coordinator?.StartupStatus ?? "Off";
        CompatibilityValue.Text = preview ? "Preview" : coordinator?.CompatibilityStatus ?? "Restore record unavailable";
        StatusMessage.Text = app.StartupError ?? message ?? (preview
            ? "Preview mode. Try the controls without changing Windows."
            : active ? "The XP appearance is active."
            : state is not null ? "Restore previous settings before applying again."
            : "Apply the XP appearance to this Windows account.");
        (UIElementAutomationPeer.FromElement(StatusMessage) as AutomationPeer)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        UpdateWallpaperText();
    }

    internal bool PerformOperation(Action operation, string success)
    {
        app.ClearStartupError();
        try
        {
            operation();
            message = success;
            RefreshStatus();
            return true;
        }
        catch (Exception error)
        {
            message = error.Message;
            RefreshStatus();
            return false;
        }
    }

    private void ApplyClick(object sender, RoutedEventArgs e) => PerformOperation(() =>
    {
        var selected = SelectedImage();
        if (preview)
            previewApplied = true;
        else
            app.Coordinator?.Apply(selected, SelectedPreferences());
        app.UpdateXpExplorerVariant(SelectedVariant);
    }, preview ? "Preview applied." : "XP appearance applied.");

    private void RepairClick(object sender, RoutedEventArgs e) => PerformOperation(() =>
    {
        var selected = SelectedImage();
        if (!preview)
            app.Coordinator?.Repair(selected, SelectedPreferences());
        app.UpdateXpExplorerVariant(SelectedVariant);
    }, preview ? "Preview repaired." : "Repair completed.");

    private void RestoreClick(object sender, RoutedEventArgs e) =>
        PerformOperation(RestoreTheme, preview ? "Preview restored." : "Previous Windows settings restored.");

    private void RestoreAndExitClick(object sender, RoutedEventArgs e)
    {
        if (PerformOperation(RestoreTheme, preview ? "Preview restored." : "Previous Windows settings restored."))
            app.Shutdown();
    }

    private void RestoreTheme()
    {
        if (preview)
            previewApplied = false;
        else
            app.Coordinator?.Restore();
        image = null;
        WallpaperOptIn.IsChecked = false;
    }

    private LunaVariant SelectedVariant => SilverVariant.IsChecked == true ? LunaVariant.Silver :
        OliveVariant.IsChecked == true ? LunaVariant.OliveGreen : LunaVariant.Blue;

    private ThemePreferences SelectedPreferences()
    {
        var appearance = AppearanceComponents.None;
        if (UseDesktopIcons.IsChecked == true)
            appearance |= AppearanceComponents.Icons;
        if (UseCursors.IsChecked == true)
            appearance |= AppearanceComponents.Cursors;
        if (UseSounds.IsChecked == true)
            appearance |= AppearanceComponents.Sounds;
        var grouping = GroupingChoice.SelectedIndex == 1 ? TaskGrouping.Always : TaskGrouping.Never;
        return new ThemePreferences(SelectedVariant, UseXpTaskbar.IsChecked == true, appearance,
            new TaskbarOptions(grouping, launchItems.Select(item => item.Id).ToArray(), UseXpExplorer.IsChecked == true));
    }

    private void LoadPreferences(ThemePreferences preferences)
    {
        switch (preferences.Variant)
        {
            case LunaVariant.Silver: SilverVariant.IsChecked = true; break;
            case LunaVariant.OliveGreen: OliveVariant.IsChecked = true; break;
            default: BlueVariant.IsChecked = true; break;
        }
        UseXpTaskbar.IsChecked = preferences.UseTaskbar;
        UseDesktopIcons.IsChecked = preferences.Appearance.HasFlag(AppearanceComponents.Icons);
        UseCursors.IsChecked = preferences.Appearance.HasFlag(AppearanceComponents.Cursors);
        UseSounds.IsChecked = preferences.Appearance.HasFlag(AppearanceComponents.Sounds);
        UseXpExplorer.IsChecked = preferences.Taskbar.UseXpExplorer;
        GroupingChoice.SelectedIndex = preferences.Taskbar.Grouping == TaskGrouping.Always ? 1 : 0;
        foreach (var id in preferences.Taskbar.QuickLaunch)
            launchItems.Add(ShortcutChoices.Single(item => item.Id == id));
        LaunchList.SelectedIndex = launchItems.Count > 0 ? 0 : -1;
        UpdateQuickLaunchControls();
    }

    private void VariantChanged(object sender, RoutedEventArgs e)
    {
        if (normalBrushes.Count != 0)
            ApplyHighContrast();
    }

    private void OpenXpExplorerClick(object sender, RoutedEventArgs e) =>
        PerformOperation(() => app.OpenXpExplorer(SelectedVariant), "XP Explorer opened.");

    private void QuickLaunchAddClick(object sender, RoutedEventArgs e)
    {
        if (AvailableShortcuts.SelectedItem is not Shortcut shortcut || launchItems.Contains(shortcut))
            return;
        launchItems.Add(shortcut);
        LaunchList.SelectedItem = shortcut;
        UpdateQuickLaunchControls();
    }

    private void QuickLaunchRemoveClick(object sender, RoutedEventArgs e)
    {
        var index = LaunchList.SelectedIndex;
        if (index < 0)
            return;
        launchItems.RemoveAt(index);
        LaunchList.SelectedIndex = Math.Min(index, launchItems.Count - 1);
        UpdateQuickLaunchControls();
    }

    private void QuickLaunchUpClick(object sender, RoutedEventArgs e) => MoveQuickLaunch(-1);
    private void QuickLaunchDownClick(object sender, RoutedEventArgs e) => MoveQuickLaunch(1);

    private void MoveQuickLaunch(int direction)
    {
        var from = LaunchList.SelectedIndex;
        var to = from + direction;
        if (from < 0 || to < 0 || to >= launchItems.Count)
            return;
        launchItems.Move(from, to);
        LaunchList.SelectedIndex = to;
        UpdateQuickLaunchButtons();
    }

    private void QuickLaunchSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        UpdateQuickLaunchButtons();

    private void UpdateQuickLaunchControls()
    {
        var available = ShortcutChoices.Where(item => !launchItems.Contains(item)).ToArray();
        AvailableShortcuts.ItemsSource = available;
        AvailableShortcuts.SelectedIndex = available.Length > 0 ? 0 : -1;
        AddShortcut.IsEnabled = AvailableShortcuts.IsEnabled = available.Length > 0;
        UpdateQuickLaunchButtons();
    }

    private void UpdateQuickLaunchButtons()
    {
        if (MoveShortcutDown is null)
            return;
        var selected = LaunchList.SelectedIndex;
        RemoveShortcut.IsEnabled = selected >= 0;
        MoveShortcutUp.IsEnabled = selected > 0;
        MoveShortcutDown.IsEnabled = selected >= 0 && selected < launchItems.Count - 1;
    }

    private string? SelectedImage() => WallpaperOptIn.IsChecked != true ? null :
        image ?? throw new InvalidOperationException("Choose an image first.");

    private void BrowseClick(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = "Choose Wallpaper",
            Filter = "Wallpaper Images (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp",
            CheckFileExists = true
        };
        if (picker.ShowDialog(this) == true)
        {
            image = picker.FileName;
            UpdateWallpaperText();
        }
    }

    private void WallpaperChoiceChanged(object sender, RoutedEventArgs e)
    {
        if (WallpaperPicker is null)
            return;
        WallpaperPicker.IsEnabled = WallpaperOptIn.IsChecked == true;
        UpdateWallpaperText();
    }

    private void UpdateWallpaperText()
    {
        WallpaperFile.Text = WallpaperOptIn.IsChecked == true
            ? image is null ? "Choose a picture to use." : Path.GetFileName(image)
            : app.Coordinator?.State?.Wallpaper is not null ? "Repair restores the previous wallpaper." : "Keep my current wallpaper.";
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!preview && app.StateDirectory is { } directory)
            WindowPosition.Save(this, directory);
        if (!preview && app.Coordinator?.State is not null && !Dispatcher.HasShutdownStarted)
        {
            e.Cancel = true;
            Hide();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        SystemParameters.StaticPropertyChanged -= SystemParametersChanged;
        app.CloseControl(preview);
    }

    private void SystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast))
            Dispatcher.BeginInvoke(ApplyHighContrast);
    }

    private void ApplyHighContrast()
    {
        if (SystemParameters.HighContrast)
        {
            Resources["Panel"] = SystemColors.WindowBrush;
            Resources["Ink"] = SystemColors.WindowTextBrush;
            Resources["Rule"] = SystemColors.WindowTextBrush;
            Resources["StatusPanel"] = SystemColors.WindowBrush;
            Resources["CaptionInk"] = SystemColors.HighlightTextBrush;
            Resources["Frame"] = SystemColors.WindowTextBrush;
            Resources["TitleFill"] = SystemColors.HighlightBrush;
            Resources["ButtonFill"] = SystemColors.ControlBrush;
            Resources["ButtonEdge"] = SystemColors.WindowTextBrush;
            Resources["ButtonHover"] = SystemColors.ControlBrush;
            Resources["ButtonPressed"] = SystemColors.ControlBrush;
            Resources["CaptionClose"] = SystemColors.HighlightBrush;
            Resources["DisabledInk"] = SystemColors.GrayTextBrush;
        }
        else
        {
            foreach (var (key, brush) in normalBrushes)
                Resources[key] = brush;
            if (SelectedVariant != LunaVariant.Blue)
            {
                var palette = new LunaVisuals(SelectedVariant);
                Resources["TitleFill"] = palette.MenuHeader;
                Resources["Frame"] = palette.MenuEdge;
                Resources["ButtonEdge"] = palette.TaskEdge;
                Resources["ButtonHover"] = palette.MenuRight;
            }
        }
    }

    private sealed record Shortcut(QuickLaunchItem Id, string Label);
}
