using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using Microsoft.Win32;
using LunaDesktop.State;

namespace LunaDesktop;

public partial class ControlWindow : Window
{
    private readonly App app;
    private readonly bool preview;
    private string? image;
    private bool previewApplied;
    private string? message;

    internal ControlWindow(App app, bool preview)
    {
        this.app = app;
        this.preview = preview;
        InitializeComponent();
        ApplyHighContrast();
        SystemParameters.StaticPropertyChanged += SystemParametersChanged;
        if (!preview && app.Coordinator?.State?.Wallpaper is { } wallpaper)
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

    internal void PerformOperation(Action operation, string success)
    {
        app.ClearStartupError();
        try
        {
            operation();
            message = success;
        }
        catch (Exception error)
        {
            message = error.Message;
        }
        RefreshStatus();
    }

    private void ApplyClick(object sender, RoutedEventArgs e) => PerformOperation(() =>
    {
        var selected = SelectedImage();
        if (preview)
            previewApplied = true;
        else
            app.Coordinator?.Apply(selected);
    }, preview ? "Preview applied." : "XP appearance applied.");

    private void RepairClick(object sender, RoutedEventArgs e) => PerformOperation(() =>
    {
        var selected = SelectedImage();
        if (!preview)
            app.Coordinator?.Repair(selected);
    }, preview ? "Preview repaired." : "Repair completed.");

    private void RestoreClick(object sender, RoutedEventArgs e) => PerformOperation(() =>
    {
        if (preview)
            previewApplied = false;
        else
            app.Coordinator?.Restore();
        image = null;
        WallpaperOptIn.IsChecked = false;
    }, preview ? "Preview restored." : "Previous Windows settings restored.");

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

    private readonly Dictionary<string, object> normalBrushes = [];

    private void ApplyHighContrast()
    {
        if (SystemParameters.HighContrast)
        {
            Override("Panel", SystemColors.WindowBrush);
            Override("Ink", SystemColors.WindowTextBrush);
            Override("Rule", SystemColors.WindowTextBrush);
            Override("StatusPanel", SystemColors.WindowBrush);
            Override("CaptionInk", SystemColors.HighlightTextBrush);
            Override("Frame", SystemColors.WindowTextBrush);
            Override("TitleFill", SystemColors.HighlightBrush);
            Override("ButtonFill", SystemColors.ControlBrush);
            Override("ButtonEdge", SystemColors.WindowTextBrush);
            Override("ButtonHover", SystemColors.ControlBrush);
            Override("ButtonPressed", SystemColors.ControlBrush);
            Override("CaptionClose", SystemColors.HighlightBrush);
            Override("DisabledInk", SystemColors.GrayTextBrush);
        }
        else
        {
            foreach (var (key, brush) in normalBrushes)
                Resources[key] = brush;
        }
    }

    private void Override(string key, object value)
    {
        normalBrushes.TryAdd(key, Resources[key]);
        Resources[key] = value;
    }
}
