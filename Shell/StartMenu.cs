using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LunaDesktop.Shell;

internal sealed class StartMenu : Window
{
    private const int MaximumShortcuts = 2048;
    private const int MaximumFolderDepth = 12;
    private const double CommandHeight = 37;
    private const double DividerVerticalMargin = 5;
    private readonly LunaVisuals visuals;
    private readonly DockPanel root = new();
    private readonly ScrollViewer menuBody = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
    };
    private readonly Button firstAction;
    private readonly Grid mainPage = new();
    private bool programsVisible;
    private bool dismissing;

    internal event Action? EscapeDismissed;

    internal static double PreferredHeight(int pinCount) =>
        335 + pinCount * CommandHeight + (pinCount > 0 ? 1 + 2 * DividerVerticalMargin : 0);

    internal StartMenu(Window owner, LunaVisuals visuals, Action openControl, Action? showWindowsTaskbar,
        Action<string?>? openXpExplorer = null, bool useXpExplorer = false, IReadOnlyList<QuickLaunchEntry>? pins = null)
    {
        Owner = owner;
        this.visuals = visuals;
        Title = "11toXP Start Menu";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = true;
        Topmost = true;
        FontFamily = new FontFamily("Tahoma");
        Background = visuals.MenuLeft;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        KeyboardNavigation.SetTabNavigation(root, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetDirectionalNavigation(root, KeyboardNavigationMode.Cycle);

        var frame = new Border
        {
            Child = root,
            BorderBrush = visuals.MenuEdge,
            BorderThickness = new Thickness(2),
            Background = visuals.MenuLeft
        };
        Content = frame;
        AddHeader();
        AddFooter(openControl);

        mainPage.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.05, GridUnitType.Star) });
        mainPage.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = new StackPanel { Margin = new Thickness(5, 7, 5, 7) };
        var right = new StackPanel { Background = visuals.MenuRight, Margin = new Thickness(0, 0, 0, 0) };
        Grid.SetColumn(right, 1);
        mainPage.Children.Add(left);
        mainPage.Children.Add(right);

        if (openXpExplorer is not null && useXpExplorer)
        {
            firstAction = AddCommand(left, "XP Explorer", () => openXpExplorer(null), "folder", emphasized: true);
            AddCommand(left, "Windows File Explorer", () => LaunchExplorer(), "folder");
        }
        else
        {
            firstAction = AddCommand(left, "File Explorer", () => LaunchExplorer(), "folder", emphasized: true);
            if (openXpExplorer is not null)
                AddCommand(left, "XP Explorer", () => openXpExplorer(null), "folder");
        }
        AddCommand(left, "Settings", () => LaunchFile("ms-settings:"), "settings");
        AddCommand(left, "Run…", () => InvokeShell("FileRun"), "run");
        AddDivider(left);
        if (pins is { Count: > 0 })
        {
            foreach (var pin in pins)
            {
                var button = AddCommand(left, pin.Label, () => LaunchShortcut(pin), "shortcut", emphasized: true);
                AutomationProperties.SetAutomationId(button, "StartMenuPin");
            }
            AddDivider(left);
        }
        AddCommand(left, "All Programs  ›", ShowPrograms, "programs", closeBeforeAction: false, emphasized: true);

        Action<string?>? openFolder = useXpExplorer ? openXpExplorer : null;
        AddFolder(right, "My Documents", Environment.SpecialFolder.MyDocuments, openFolder);
        AddFolder(right, "My Pictures", Environment.SpecialFolder.MyPictures, openFolder);
        AddFolder(right, "My Music", Environment.SpecialFolder.MyMusic, openFolder);
        AddCommand(right, "This PC", () => InvokeShell("Explore", 17), "computer", emphasized: true);
        AddDivider(right);
        if (showWindowsTaskbar is null)
            AddCommand(right, "Windows Taskbar Settings", () => LaunchFile("ms-settings:taskbar"), "settings");
        else
            AddCommand(right, "Show Windows Taskbar", showWindowsTaskbar, "settings");

        menuBody.Content = mainPage;
        root.Children.Add(menuBody);
        ContentRendered += (_, _) => firstAction.Focus();
        Deactivated += (_, _) => Dismiss();
        PreviewKeyDown += OnMenuKeyDown;
    }

    private void AddHeader()
    {
        var header = new DockPanel
        {
            Height = 61,
            Background = visuals.MenuHeader,
            LastChildFill = true
        };
        var avatar = new Border
        {
            Width = 41,
            Height = 41,
            Margin = new Thickness(10, 0, 9, 0),
            BorderThickness = new Thickness(2),
            BorderBrush = visuals.MenuHeaderText,
            CornerRadius = new CornerRadius(3),
            Background = visuals.HighContrast ? SystemColors.ControlBrush : LunaVisuals.Gradient((0, 0xA4DDFC), (1, 0x4C7CAA)),
            VerticalAlignment = VerticalAlignment.Center,
            Child = LunaVisuals.AppGlyph(visuals.HighContrast)
        };
        avatar.Child.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        avatar.Child.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        header.Children.Add(avatar);
        header.Children.Add(new TextBlock
        {
            Text = Environment.UserName,
            Foreground = visuals.MenuHeaderText,
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 10, 0)
        });
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
    }

    private void AddFooter(Action openControl)
    {
        var footer = new Border
        {
            Background = visuals.MenuHeader,
            Padding = new Thickness(8, 3, 8, 3)
        };
        Button settings = visuals.NewButton("11toXP Settings", "11toXP", visuals.MenuHeaderButtonStyle, Brushes.Transparent);
        settings.Height = 32;
        settings.MinWidth = 82;
        settings.HorizontalAlignment = HorizontalAlignment.Right;
        settings.Click += (_, _) => Execute(openControl, closeBeforeAction: true);
        footer.Child = settings;
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
    }

    private void AddFolder(Panel parent, string label, Environment.SpecialFolder folder, Action<string?>? openXpFolder)
    {
        AddCommand(parent, label, () =>
        {
            string path = Environment.GetFolderPath(folder);
            if (string.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException("Windows did not provide a location for this folder.");
            if (openXpFolder is not null)
                openXpFolder(path);
            else
                LaunchFile(path);
        }, "folder", emphasized: true);
    }

    private Button AddCommand(Panel parent, string name, Action action, string icon,
        bool closeBeforeAction = true, bool emphasized = false)
    {
        var content = new DockPanel { LastChildFill = true };
        var glyph = new Border { Width = 21, Child = CreateGlyph(icon), Margin = new Thickness(0, 0, 6, 0) };
        content.Children.Add(glyph);
        content.Children.Add(new TextBlock
        {
            Text = name,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontWeight = emphasized ? FontWeights.Bold : FontWeights.Normal
        });
        Button button = visuals.NewButton(name, content, visuals.MenuButtonStyle, Brushes.Transparent);
        button.MinHeight = CommandHeight;
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        button.ToolTip = name;
        button.Click += (_, _) => Execute(action, closeBeforeAction);
        parent.Children.Add(button);
        return button;
    }

    private UIElement CreateGlyph(string kind)
    {
        (string shape, uint color) = kind switch
        {
            "folder" => ("M 1,6 L 8,6 10,9 19,9 19,20 1,20 Z", 0xEBC259u),
            "settings" => ("M 2,3 L 18,3 18,19 2,19 Z M 6,7 L 6,15 9,15 9,7 Z M 12,7 L 12,15 15,15 15,7 Z", 0x6698C4u),
            "run" => ("M 1,4 L 19,4 19,18 1,18 Z M 4,8 L 7,11 4,14 6,14 9,11 6,8 Z M 10,13 L 16,13 16,15 10,15 Z", 0x659AB3u),
            "computer" => ("M 1,2 L 19,2 19,15 12,15 12,18 16,18 16,20 4,20 4,18 8,18 8,15 1,15 Z", 0x7CADD9u),
            _ => ("M 2,4 L 14,4 14,16 2,16 Z M 6,7 L 8,7 8,9 6,9 Z M 10,7 L 12,7 12,9 10,9 Z M 6,11 L 8,11 8,13 6,13 Z M 10,11 L 12,11 12,13 10,13 Z", 0x6CB14Au)
        };
        return LunaVisuals.ShortcutGlyph(shape,
            visuals.HighContrast ? SystemColors.WindowTextBrush : LunaVisuals.Solid(color), visuals.MenuEdge);
    }

    private void AddDivider(Panel parent)
    {
        parent.Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(9, DividerVerticalMargin, 9, DividerVerticalMargin),
            Background = visuals.HighContrast ? SystemColors.WindowTextBrush : LunaVisuals.Solid(0xABC7E8)
        });
    }

    private void ShowPrograms()
    {
        if (programsVisible)
            return;
        programsVisible = true;

        var programsPage = new DockPanel { Margin = new Thickness(5) };
        Button back = visuals.NewButton("Back to Start", "‹  Back", visuals.MenuButtonStyle, Brushes.Transparent);
        back.Height = 33;
        back.HorizontalContentAlignment = HorizontalAlignment.Left;
        back.Click += (_, _) => ShowMain();
        DockPanel.SetDock(back, Dock.Top);
        programsPage.Children.Add(back);

        Button nativeApps = visuals.NewButton("Open Windows Apps", "Open Windows Apps",
            visuals.MenuButtonStyle, Brushes.Transparent);
        nativeApps.Height = 34;
        nativeApps.HorizontalContentAlignment = HorizontalAlignment.Left;
        nativeApps.Click += (_, _) => Execute(() => LaunchExplorer("shell:AppsFolder"), closeBeforeAction: true);
        DockPanel.SetDock(nativeApps, Dock.Bottom);
        programsPage.Children.Add(nativeApps);

        var search = new TextBox
        {
            Height = 30,
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(5, 0, 5, 4),
            Padding = new Thickness(4, 0, 4, 0),
            ToolTip = "Filter Programs"
        };
        AutomationProperties.SetName(search, "Filter Programs");
        DockPanel.SetDock(search, Dock.Top);
        programsPage.Children.Add(search);

        List<ProgramShortcut> shortcuts = ReadPrograms();
        var items = new ListBox
        {
            ItemsSource = shortcuts,
            DisplayMemberPath = nameof(ProgramShortcut.Name),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(5, 0, 5, 0)
        };
        AutomationProperties.SetName(items, "Programs");
        VirtualizingPanel.SetIsVirtualizing(items, true);
        VirtualizingPanel.SetVirtualizationMode(items, VirtualizationMode.Recycling);
        ScrollViewer.SetHorizontalScrollBarVisibility(items, ScrollBarVisibility.Disabled);
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(shortcuts);
        view.Filter = candidate => candidate is ProgramShortcut entry
            && entry.Name.Contains(search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase);
        search.TextChanged += (_, _) => view.Refresh();
        search.PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Down && items.Items.Count > 0)
            {
                items.SelectedIndex = 0;
                items.Focus();
                args.Handled = true;
            }
        };
        items.MouseDoubleClick += (_, args) =>
        {
            if (args.OriginalSource is DependencyObject element
                && ItemsControl.ContainerFromElement(items, element) is ListBoxItem)
                OpenSelection();
        };
        items.KeyDown += (_, args) =>
        {
            if (args.Key != Key.Enter)
                return;
            args.Handled = true;
            OpenSelection();
        };
        programsPage.Children.Add(items);
        menuBody.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        menuBody.Content = programsPage;
        search.Focus();

        void OpenSelection()
        {
            if (items.SelectedItem is ProgramShortcut entry)
                Execute(() => LaunchFile(entry.Path), closeBeforeAction: true);
        }
    }

    private void ShowMain()
    {
        programsVisible = false;
        menuBody.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        menuBody.Content = mainPage;
        firstAction.Focus();
    }

    private void OnMenuKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key != Key.Escape)
            return;
        args.Handled = true;
        if (programsVisible)
            ShowMain();
        else
        {
            Dismiss();
            EscapeDismissed?.Invoke();
        }
    }

    private void Execute(Action action, bool closeBeforeAction)
    {
        if (closeBeforeAction)
            Dismiss();
        try
        {
            action();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or
                                  COMException or UnauthorizedAccessException or SecurityException or TargetInvocationException)
        {
            MessageBox.Show(ex.InnerException?.Message ?? ex.Message, "11toXP", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    internal void Dismiss()
    {
        if (dismissing)
            return;
        dismissing = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs args)
    {
        dismissing = true;
        base.OnClosing(args);
    }

    private static List<ProgramShortcut> ReadPrograms()
    {
        var shortcuts = new Dictionary<string, ProgramShortcut>(StringComparer.CurrentCultureIgnoreCase);
        foreach (Environment.SpecialFolder folder in new[] { Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonPrograms })
        {
            string baseDirectory = Environment.GetFolderPath(folder);
            if (string.IsNullOrWhiteSpace(baseDirectory) || !Directory.Exists(baseDirectory))
                continue;
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                MaxRecursionDepth = MaximumFolderDepth,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System
            };
            try
            {
                foreach (string path in Directory.EnumerateFiles(baseDirectory, "*", options).Take(MaximumShortcuts))
                {
                    string extension = Path.GetExtension(path);
                    if (!extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
                        && !extension.Equals(".appref-ms", StringComparison.OrdinalIgnoreCase))
                        continue;
                    string name = Path.GetFileNameWithoutExtension(path);
                    shortcuts.TryAdd(name, new ProgramShortcut(name, path));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                // Entries already read from either Start Menu folder remain usable.
            }
        }
        return shortcuts.Values.OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    internal static void LaunchExplorer(string? location = null)
    {
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
            UseShellExecute = true
        };
        if (location is not null)
            start.ArgumentList.Add(location);
        Process.Start(start)?.Dispose();
    }

    internal static void LaunchFile(string path)
    {
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
    }

    internal static void LaunchShortcut(QuickLaunchEntry entry)
    {
        if (!entry.IsValid() || entry.ShortcutPath is not { } path)
            throw new InvalidOperationException("Choose a Windows shortcut (.lnk) on a local drive.");
        QuickLaunchEntry.RequireAvailableFile(path);
        LaunchFile(path);
    }

    private static void InvokeShell(string method, params object[] arguments)
    {
        Type shellType = Type.GetTypeFromProgID("Shell.Application", throwOnError: true)!;
        object shell = Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("Windows Shell automation is unavailable.");
        try
        {
            shellType.InvokeMember(method, BindingFlags.InvokeMethod, null, shell, arguments);
        }
        finally
        {
            Marshal.ReleaseComObject(shell);
        }
    }

    private sealed record ProgramShortcut(string Name, string Path)
    {
        public override string ToString() => Name;
    }
}
