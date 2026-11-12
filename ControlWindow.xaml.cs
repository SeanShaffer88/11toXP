using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using LunaDesktop.Diagnostics;
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
    private ThemePreferences? previewPreferences;
    private string? message;
    private TaskbarConfirmationWindow? taskbarConfirmation;
    private bool loadingDisplays;
    private bool hasDisplaySelection;
    private readonly HashSet<string> selectedDisplayIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<QuickLaunchEntry> launchItems = [];
    private readonly ObservableCollection<QuickLaunchEntry> pinItems = [];
    private readonly AppearanceProfileStore? profileStore;
    private AppearanceProfile[]? profiles;
    private readonly Dictionary<string, object> normalBrushes = [];
    private static readonly QuickLaunchEntry[] ShortcutChoices =
    [
        new(QuickLaunchItem.FileExplorer, null),
        new(QuickLaunchItem.Settings, null),
        new(QuickLaunchItem.XpExplorer, null)
    ];

    internal ControlWindow(App app, bool preview)
    {
        this.app = app;
        this.preview = preview;
        if (!preview && app.StateDirectory is { } profileDirectory)
            profileStore = new AppearanceProfileStore(profileDirectory);
        InitializeComponent();
        foreach (var key in new[]
        {
            "Panel", "Ink", "Rule", "StatusPanel", "CaptionInk", "Frame", "TitleFill", "ButtonFill",
            "ButtonEdge", "ButtonHover", "ButtonPressed", "CaptionClose", "DisabledInk"
        })
            normalBrushes.Add(key, Resources[key]);
        LaunchList.ItemsSource = launchItems;
        PinList.ItemsSource = pinItems;
        LoadPreferences(app.Coordinator?.State?.Preferences ?? (preview ? ThemePreferences.Default : app.SavedPreferences));
        ApplyHighContrast();
        SystemParameters.StaticPropertyChanged += SystemParametersChanged;
        app.TaskbarDisplaysChanged += RefreshDisplayChoices;
        Activated += (_, _) => RefreshTaskbarFocus();
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
        if (!preview && taskbarConfirmation is { IsVisible: true } dialog && coordinator?.IsTaskbarPreviewActive == false)
            dialog.Close();
        var state = coordinator?.State;
        var active = preview ? previewApplied : state?.Phase == ThemePhase.Active;
        ApplyAction.IsEnabled = preview ? !previewApplied : coordinator is not null && state is null;
        RepairAction.IsEnabled = active;
        RestoreAction.IsEnabled = preview ? previewApplied : state is not null;
        RestoreAndExitAction.IsEnabled = preview || coordinator is not null;
        RefreshTaskbarFocus();
        Components.IsEnabled = TaskbarSettings.IsEnabled = ProfileSettings.IsEnabled =
            preview || (coordinator is not null && (state is null || active));
        WallpaperOptIn.IsEnabled = preview || (coordinator is not null && (state is null || active));
        WallpaperPicker.IsEnabled = WallpaperOptIn.IsEnabled && WallpaperOptIn.IsChecked == true;
        ShellValue.Text = preview && active ? "XP taskbar preview" : coordinator?.ShellStatus ?? "Windows taskbar";
        AppearanceValue.Text = preview && active ? "XP-style preview" : coordinator?.AppearanceStatus ?? "Windows settings";
        WallpaperValue.Text = coordinator?.WallpaperStatus ?? "Unmanaged";
        StartupValue.Text = coordinator?.StartupStatus ?? "Off";
        CompatibilityValue.Text = preview ? "Preview" : coordinator?.CompatibilityStatus ?? "Restore record unavailable";
        var status = app.StartupError ?? message ?? (preview
            ? "Preview mode. Try the controls without changing Windows."
            : active ? "The XP appearance is active."
            : state is not null ? "Restore previous settings before applying again."
            : "Apply the XP appearance to this Windows account.");
        AnnounceStatus(status);
        UpdateWallpaperText();
    }

    private void AnnounceStatus(string status)
    {
        if (StatusMessage.Text != status)
        {
            StatusMessage.Text = status;
            UIElementAutomationPeer.CreatePeerForElement(StatusMessage)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }

    private void StatusDetailsExpanded(object sender, RoutedEventArgs e)
    {
        if (sender is Expander { Content: FrameworkElement details } expander)
            Dispatcher.InvokeAsync(() =>
            {
                if (expander.IsExpanded)
                    details.BringIntoView();
            }, DispatcherPriority.Loaded);
    }

    internal bool PerformOperation(Action operation, string success) =>
        PerformOperation(() =>
        {
            operation();
            return success;
        });

    private bool PerformOperation(Func<string> operation)
    {
        app.ClearStartupError();
        try
        {
            message = operation();
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

    private void ApplyClick(object sender, RoutedEventArgs e) => ApplyOrRepair(repair: false);
    private void RepairClick(object sender, RoutedEventArgs e) => ApplyOrRepair(repair: true);

    private void ApplyOrRepair(bool repair) => PerformOperation(() =>
    {
        const string reverted = "Taskbar preview reverted. No settings were saved.";
        var selectedImage = SelectedImage();
        var options = SelectedPreferences();
        if (preview)
        {
            if (PreviewLayoutChanged(options) && !ConfirmTaskbarLayout())
                return reverted;
            previewApplied = true;
            previewPreferences = options;
            return repair ? "Preview repaired." : "Preview applied.";
        }

        var coordinator = app.Coordinator ?? throw new InvalidOperationException("The restore record is unavailable.");
        void ApplySettings()
        {
            if (repair)
                coordinator.Repair(selectedImage, options);
            else
                coordinator.Apply(selectedImage, options);
            app.UpdateXpExplorerVariant(options.Variant);
        }

        if (coordinator.NeedsTaskbarPreview(options))
        {
            if (!ConfirmTaskbarLayout(coordinator, options, ApplySettings))
                return reverted;
        }
        else
            ApplySettings();
        return repair ? "Repair completed." : "XP appearance applied.";
    });

    internal void RequestTaskbarResize(TaskbarButtonSize size)
    {
        var coordinator = app.Coordinator;
        if (preview || app.IsSessionEnding || taskbarConfirmation is not null || coordinator is null ||
            coordinator.IsTaskbarPreviewActive || coordinator.State is not { Phase: ThemePhase.Active } state ||
            state.Preferences.Taskbar.IsLocked || state.Preferences.Taskbar.ButtonSize == size)
            return;

        if (!IsVisible)
            Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        var options = state.Preferences with { Taskbar = state.Preferences.Taskbar with { ButtonSize = size } };
        PerformOperation(() =>
        {
            if (!ConfirmTaskbarLayout(coordinator, options, () => coordinator.KeepTaskbarResize(size), resizeOnly: true))
                return "Taskbar preview reverted. No settings were saved.";
            TaskbarButtonSizeChoice.SelectedIndex = size == TaskbarButtonSize.Small ? 1 : 0;
            return "Taskbar size saved.";
        });
    }

    private bool PreviewLayoutChanged(ThemePreferences options) =>
        previewPreferences is not { } previous ? options.UseTaskbar :
        (previous.UseTaskbar || options.UseTaskbar) &&
        (previous.UseTaskbar != options.UseTaskbar || previous.Variant != options.Variant ||
         previous.KeepWindowsTrayVisible != options.KeepWindowsTrayVisible ||
         !previous.Taskbar.HasSameLayout(options.Taskbar));

    private bool ConfirmTaskbarLayout(ThemeCoordinator coordinator, ThemePreferences options, Action apply, bool resizeOnly = false)
    {
        coordinator.BeginTaskbarPreview(options);
        try
        {
            if (!ConfirmTaskbarLayout(resizeOnly) || app.IsSessionEnding)
                return false;
            coordinator.CommitTaskbarPreview(apply);
            return true;
        }
        finally
        {
            if (coordinator.IsTaskbarPreviewActive)
                coordinator.CancelTaskbarPreview();
        }
    }

    private bool ConfirmTaskbarLayout(bool resizeOnly = false)
    {
        var dialog = new TaskbarConfirmationWindow(this, Stopwatch.GetTimestamp(), resizeOnly);
        taskbarConfirmation = dialog;
        try { return dialog.ShowDialog() == true; }
        finally { taskbarConfirmation = null; }
    }

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
        {
            previewApplied = false;
            previewPreferences = null;
        }
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
        var builtIns = launchItems.Select(item => item.BuiltIn).OfType<QuickLaunchItem>().ToArray();
        var taskbar = new TaskbarOptions(grouping, builtIns, UseXpExplorer.IsChecked == true)
        {
            OrderedQuickLaunch = launchItems.Count == builtIns.Length ? null : [.. launchItems],
            StartMenuPins = pinItems.Count == 0 ? null : [.. pinItems],
            ButtonSize = TaskbarButtonSizeChoice.SelectedIndex == 1 ? TaskbarButtonSize.Small : TaskbarButtonSize.Large,
            ShowLabels = ShowTaskbarLabels.IsChecked == true,
            IsLocked = LockTaskbar.IsChecked == true,
            DisplayIds = SelectedDisplays()
        };
        return new ThemePreferences(SelectedVariant, UseXpTaskbar.IsChecked == true, appearance, taskbar)
        {
            StartWithWindows = StartWithWindows.IsChecked == true,
            KeepWindowsTrayVisible = KeepWindowsTrayVisible.IsChecked == true
        };
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
        StartWithWindows.IsChecked = preferences.StartWithWindows;
        KeepWindowsTrayVisible.IsChecked = preferences.KeepWindowsTrayVisible;
        GroupingChoice.SelectedIndex = preferences.Taskbar.Grouping == TaskGrouping.Always ? 1 : 0;
        TaskbarButtonSizeChoice.SelectedIndex = preferences.Taskbar.ButtonSize == TaskbarButtonSize.Small ? 1 : 0;
        ShowTaskbarLabels.IsChecked = preferences.Taskbar.ShowLabels;
        LockTaskbar.IsChecked = preferences.Taskbar.IsLocked;
        LoadDisplayChoices(preferences.Taskbar.DisplayIds);
        launchItems.Clear();
        foreach (var shortcut in preferences.Taskbar.EffectiveQuickLaunch)
            launchItems.Add(shortcut);
        LaunchList.SelectedIndex = launchItems.Count > 0 ? 0 : -1;
        UpdateQuickLaunchControls();
        pinItems.Clear();
        foreach (var pin in preferences.Taskbar.StartMenuPins ?? [])
            pinItems.Add(pin);
        PinList.SelectedIndex = pinItems.Count > 0 ? 0 : -1;
        UpdatePinControls();
    }

    private void AppearanceProfilesExpanded(object sender, RoutedEventArgs e)
    {
        if (profiles is not null)
            return;
        try
        {
            SetProfiles(profileStore?.Load() ?? [], "");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            SaveProfile.IsEnabled = LoadProfile.IsEnabled = DeleteProfile.IsEnabled = false;
            AnnounceStatus(error.Message);
        }
    }

    private void SetProfiles(AppearanceProfile[] values, string name)
    {
        profiles = values;
        ProfileName.ItemsSource = values.Select(profile => profile.Name).ToArray();
        ProfileName.Text = name;
        SaveProfile.IsEnabled = true;
        LoadProfile.IsEnabled = DeleteProfile.IsEnabled = values.Length > 0;
    }

    private AppearanceProfile? FindProfile(string name) =>
        profiles?.FirstOrDefault(profile => string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase));

    private AppearanceProfile SelectedProfile() => FindProfile(ProfileName.Text.Trim()) ??
        throw new InvalidOperationException("Choose a saved appearance profile.");

    private void LoadProfileClick(object sender, RoutedEventArgs e) => PerformOperation(() =>
        LoadPreferences(SelectedProfile().ToPreferences(StartWithWindows.IsChecked == true)),
        "Appearance profile loaded. Use Apply or Repair to save your changes.");

    private void SaveProfileClick(object sender, RoutedEventArgs e) => PerformOperation(() =>
    {
        var profile = AppearanceProfile.Capture(ProfileName.Text, SelectedPreferences());
        var saved = profiles ?? throw new InvalidOperationException("Appearance profiles could not be read.");
        var existing = FindProfile(profile.Name);
        if (existing is null && saved.Length >= AppearanceProfileStore.MaximumProfiles)
            throw new InvalidOperationException($"Save up to {AppearanceProfileStore.MaximumProfiles} appearance profiles.");
        if (existing is not null && MessageBox.Show(this, $"Replace the appearance profile \"{existing.Name}\"?",
                "11toXP", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return "Appearance profile kept.";
        AppearanceProfile[] updated = existing is null ? [.. saved, profile] :
            saved.Select(item => item == existing ? profile : item).ToArray();
        profileStore?.Save(updated);
        SetProfiles(updated, profile.Name);
        return "Appearance profile saved.";
    });

    private void DeleteProfileClick(object sender, RoutedEventArgs e) => PerformOperation(() =>
    {
        var selected = SelectedProfile();
        var updated = profiles!.Where(profile => profile != selected).ToArray();
        profileStore?.Save(updated);
        SetProfiles(updated, "");
    }, "Appearance profile deleted.");

    private string[]? SelectedDisplays()
    {
        if (UsePrimaryDisplay.IsChecked == true)
            return null;
        if (selectedDisplayIds.Count == 0)
        {
            TaskbarDisplays.IsExpanded = true;
            throw new InvalidOperationException("Select a display for the XP taskbar.");
        }
        return selectedDisplayIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private void LoadDisplayChoices(string[]? ids)
    {
        loadingDisplays = true;
        try
        {
            selectedDisplayIds.Clear();
            if (ids is not null)
                selectedDisplayIds.UnionWith(ids);
            hasDisplaySelection = ids is not null;
            UsePrimaryDisplay.IsChecked = ids is null;
        }
        finally { loadingDisplays = false; }
        RefreshDisplayChoices();
    }

    private void DisplayModeChanged(object sender, RoutedEventArgs e)
    {
        if (!loadingDisplays)
            RefreshDisplayChoices();
    }

    private void RefreshDisplayChoices()
    {
        if (TaskbarDisplayChoices is null)
            return;
        string? focusedId = Keyboard.FocusedElement is CheckBox focused && TaskbarDisplayChoices.Children.Contains(focused)
            ? focused.Tag as string : null;
        TaskbarDisplayChoices.Children.Clear();
        bool manual = UsePrimaryDisplay.IsChecked != true;
        TaskbarDisplayChoices.Visibility = manual ? Visibility.Visible : Visibility.Collapsed;
        if (!manual)
            return;

        ShellDisplay[] displays;
        bool enumerationFailed = false;
        try { displays = app.GetTaskbarDisplays(); }
        catch (InvalidOperationException)
        {
            displays = [];
            enumerationFailed = true;
            TaskbarDisplayChoices.Children.Add(new TextBlock
            {
                Text = "Windows could not list the connected displays.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(15, 3, 0, 3)
            });
        }
        if (!hasDisplaySelection && displays.FirstOrDefault(display => display.IsPrimary) is { } primary)
        {
            selectedDisplayIds.Add(primary.Id);
            hasDisplaySelection = true;
        }

        var connected = displays.Select(display => display.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < displays.Length; index++)
            AddDisplayChoice(displays[index].Id, displays[index].Label, $"TaskbarDisplay{index + 1}");
        var disconnected = selectedDisplayIds.Where(id => !connected.Contains(id)).ToArray();
        for (int index = 0; index < disconnected.Length; index++)
            AddDisplayChoice(disconnected[index], $"Saved display {index + 1} (disconnected)", $"TaskbarDisconnectedDisplay{index + 1}");
        if (!enumerationFailed && displays.Length == 0 && disconnected.Length == 0)
            TaskbarDisplayChoices.Children.Add(new TextBlock { Text = "No displays available.", Margin = new Thickness(15, 3, 0, 3) });
        if (focusedId is not null)
        {
            var previous = TaskbarDisplayChoices.Children.OfType<CheckBox>().FirstOrDefault(choice =>
                string.Equals(choice.Tag as string, focusedId, StringComparison.OrdinalIgnoreCase));
            (previous ?? UsePrimaryDisplay).Focus();
        }
    }

    private void AddDisplayChoice(string id, string label, string automationId)
    {
        var choice = new CheckBox
        {
            Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap },
            Tag = id,
            IsChecked = selectedDisplayIds.Contains(id),
            MinHeight = 26,
            Margin = new Thickness(15, 0, 0, 0)
        };
        AutomationProperties.SetName(choice, label);
        AutomationProperties.SetAutomationId(choice, automationId);
        choice.Checked += (_, _) => selectedDisplayIds.Add(id);
        choice.Unchecked += (_, _) => selectedDisplayIds.Remove(id);
        TaskbarDisplayChoices.Children.Add(choice);
    }

    private void VariantChanged(object sender, RoutedEventArgs e)
    {
        if (normalBrushes.Count != 0)
            ApplyHighContrast();
    }

    private void OpenXpExplorerClick(object sender, RoutedEventArgs e) =>
        PerformOperation(() => app.OpenXpExplorer(SelectedVariant), "XP Explorer opened.");

    private void ExportDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (DiagnosticReport.Export(this, app.Coordinator, app.StartupError is not null))
                AnnounceStatus("Diagnostics exported.");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or
                                      InvalidOperationException or Win32Exception)
        {
            AnnounceStatus("Diagnostics could not be saved. Choose another location and try again.");
        }
    }

    private void RefreshTaskbarFocus() =>
        FocusTaskbarAction.IsEnabled = !preview && app.CanFocusXpTaskbar;

    private void FocusTaskbarClick(object sender, RoutedEventArgs e)
    {
        FocusTaskbarAction.Focus();
        if (!app.TryFocusXpTaskbar())
        {
            app.ClearStartupError();
            message = "The XP taskbar could not receive keyboard focus.";
            RefreshStatus();
        }
    }

    private void QuickLaunchAddClick(object sender, RoutedEventArgs e)
    {
        if (AvailableShortcuts.SelectedItem is not QuickLaunchEntry shortcut)
            return;
        AddQuickLaunchItem(shortcut);
    }

    private void QuickLaunchAddFileClick(object sender, RoutedEventArgs e)
    {
        if (launchItems.Count < TaskbarOptions.MaximumQuickLaunchItems)
            ChooseShortcut("Add Quick Launch Shortcut", AddQuickLaunchItem);
    }

    private void ChooseShortcut(string title, Action<QuickLaunchEntry> add)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = "Windows shortcuts (*.lnk)|*.lnk",
            DereferenceLinks = false,
            CheckFileExists = true
        };
        try
        {
            if (dialog.ShowDialog(this) == true)
                add(QuickLaunchEntry.FromFile(dialog.FileName));
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or Win32Exception)
        {
            AnnounceStatus(error.Message);
        }
    }

    private void AddQuickLaunchItem(QuickLaunchEntry shortcut)
    {
        if (launchItems.Count >= TaskbarOptions.MaximumQuickLaunchItems)
            return;
        if (launchItems.Any(item => item.SameAs(shortcut)))
        {
            AnnounceStatus("This shortcut is already in Quick Launch.");
            return;
        }
        launchItems.Add(shortcut);
        LaunchList.SelectedItem = shortcut;
        LaunchList.ScrollIntoView(shortcut);
        UpdateQuickLaunchControls();
        AnnounceStatus("Shortcut added. Use Apply or Repair to save your changes.");
    }

    private void QuickLaunchRemoveClick(object sender, RoutedEventArgs e)
    {
        RemoveSelectedShortcut(launchItems, LaunchList);
        UpdateQuickLaunchControls();
    }

    private void QuickLaunchUpClick(object sender, RoutedEventArgs e) => MoveQuickLaunch(-1);
    private void QuickLaunchDownClick(object sender, RoutedEventArgs e) => MoveQuickLaunch(1);

    private void MoveQuickLaunch(int direction)
    {
        MoveSelectedShortcut(launchItems, LaunchList, direction);
        UpdateQuickLaunchButtons();
    }

    private void QuickLaunchSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        UpdateQuickLaunchButtons();

    private void UpdateQuickLaunchControls()
    {
        var available = ShortcutChoices.Where(item => !launchItems.Contains(item)).ToArray();
        AvailableShortcuts.ItemsSource = available;
        AvailableShortcuts.SelectedIndex = available.Length > 0 ? 0 : -1;
        bool hasRoom = launchItems.Count < TaskbarOptions.MaximumQuickLaunchItems;
        AddShortcutFile.IsEnabled = hasRoom;
        AddShortcut.IsEnabled = AvailableShortcuts.IsEnabled = hasRoom && available.Length > 0;
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

    private void StartMenuPinAddClick(object sender, RoutedEventArgs e)
    {
        if (pinItems.Count < TaskbarOptions.MaximumStartMenuPins)
            ChooseShortcut("Add Start Menu Shortcut", AddStartMenuPin);
    }

    private void AddStartMenuPin(QuickLaunchEntry shortcut)
    {
        if (pinItems.Any(pin => pin.SameAs(shortcut)))
        {
            AnnounceStatus("This shortcut is already pinned to the Start menu.");
            return;
        }
        pinItems.Add(shortcut);
        PinList.SelectedItem = shortcut;
        PinList.ScrollIntoView(shortcut);
        UpdatePinControls();
        AnnounceStatus("Shortcut pinned. Use Apply or Repair to save your changes.");
    }

    private void StartMenuPinRemoveClick(object sender, RoutedEventArgs e)
    {
        RemoveSelectedShortcut(pinItems, PinList);
        UpdatePinControls();
    }

    private void StartMenuPinUpClick(object sender, RoutedEventArgs e) => MoveStartMenuPin(-1);
    private void StartMenuPinDownClick(object sender, RoutedEventArgs e) => MoveStartMenuPin(1);
    private void StartMenuPinSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdatePinControls();

    private void MoveStartMenuPin(int direction)
    {
        MoveSelectedShortcut(pinItems, PinList, direction);
        UpdatePinControls();
    }

    private void UpdatePinControls()
    {
        if (MovePinDown is null)
            return;
        int selected = PinList.SelectedIndex;
        AddPin.IsEnabled = pinItems.Count < TaskbarOptions.MaximumStartMenuPins;
        RemovePin.IsEnabled = selected >= 0;
        MovePinUp.IsEnabled = selected > 0;
        MovePinDown.IsEnabled = selected >= 0 && selected < pinItems.Count - 1;
    }

    private static void RemoveSelectedShortcut(ObservableCollection<QuickLaunchEntry> entries, ListBox list)
    {
        int index = list.SelectedIndex;
        if (index < 0)
            return;
        entries.RemoveAt(index);
        list.SelectedIndex = Math.Min(index, entries.Count - 1);
    }

    private static void MoveSelectedShortcut(ObservableCollection<QuickLaunchEntry> entries, ListBox list, int direction)
    {
        int from = list.SelectedIndex;
        int to = from + direction;
        if (from < 0 || to < 0 || to >= entries.Count)
            return;
        entries.Move(from, to);
        list.SelectedIndex = to;
        list.ScrollIntoView(entries[to]);
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
        if (!preview && !app.IsSessionEnding && app.Coordinator?.State is not null && !Dispatcher.HasShutdownStarted)
        {
            e.Cancel = true;
            Hide();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        SystemParameters.StaticPropertyChanged -= SystemParametersChanged;
        app.TaskbarDisplaysChanged -= RefreshDisplayChoices;
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
}
