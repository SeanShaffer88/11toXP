using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LunaDesktop.Browser;

internal sealed partial class ExplorerWindow : Window
{
    private const string PlacementFile = "browser-window.json";
    private const int HistoryLimit = 64;
    private readonly List<string> history = [];
    private readonly SemaphoreSlim readers = new(2, 2);
    private readonly CancellationTokenSource lifetime = new();
    private readonly string? stateDirectory;
    private CancellationTokenSource treeCancellation = new();
    private CancellationTokenSource? navigation;
    private LunaVariant variant;
    private string? currentFolder;
    private string? firstLocation;
    private int historyIndex = -1;
    private long navigationGeneration;
    private bool navigationRequested;
    private bool openingEntry;
    private bool closed;

    internal ExplorerWindow(LunaVariant variant, string? stateDirectory)
    {
        this.stateDirectory = stateDirectory;
        InitializeComponent();
        SetVariant(variant);
        UpdateButtons();
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
    }

    internal void NavigateTo(string? path)
    {
        if (path is null || closed)
            return;
        navigationRequested = true;
        if (IsLoaded)
            _ = NavigateAsync(path, Navigation.New);
        else
            firstLocation = path;
    }

    internal void SetVariant(LunaVariant variant)
    {
        this.variant = variant;
        if (SystemParameters.HighContrast)
        {
            Resources["CaptionBrush"] = SystemColors.HighlightBrush;
            Resources["CaptionText"] = SystemColors.HighlightTextBrush;
            Resources["FrameBrush"] = SystemColors.WindowTextBrush;
            Resources["SurfaceBrush"] = SystemColors.ControlBrush;
            Resources["SurfaceText"] = SystemColors.ControlTextBrush;
            Resources["SidebarBrush"] = SystemColors.WindowBrush;
            Resources["TaskCardBrush"] = SystemColors.ControlBrush;
            Resources["DividerBrush"] = SystemColors.WindowTextBrush;
            Resources["FolderGlyphFill"] = SystemColors.WindowBrush;
            Resources["FileGlyphFill"] = SystemColors.WindowBrush;
            Resources["FolderGlyphStroke"] = SystemColors.WindowTextBrush;
            return;
        }

        var (top, bottom, border, surface, sidebar, divider) = variant switch
        {
            LunaVariant.Silver => (0x627491, 0x394761, 0x49597A, 0xECECF4, 0x7D8AA5, 0xA0ABBC),
            LunaVariant.OliveGreen => (0x526238, 0x394623, 0x596840, 0xF0EEDD, 0x738657, 0xA5AC8E),
            _ => (0x287CE3, 0x084CA5, 0x1A55AC, 0xECE9D8, 0x466BB2, 0xABA796)
        };
        var caption = new LinearGradientBrush(ToColor(top), ToColor(bottom), 90);
        caption.Freeze();
        Resources["CaptionBrush"] = caption;
        Resources["CaptionText"] = Brushes.White;
        Resources["FrameBrush"] = Solid(border);
        Resources["SurfaceBrush"] = Solid(surface);
        Resources["SurfaceText"] = Solid(0x172435);
        Resources["SidebarBrush"] = Solid(sidebar);
        Resources["TaskCardBrush"] = Solid(0xF1F4FA);
        Resources["DividerBrush"] = Solid(divider);
        Resources["FolderGlyphFill"] = Solid(0xFFDA73);
        Resources["FileGlyphFill"] = Solid(0xFCFCFF);
        Resources["FolderGlyphStroke"] = Solid(0x684A1A);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (stateDirectory is not null)
            WindowPosition.Restore(this, stateDirectory, PlacementFile);
        if (firstLocation is { } path)
        {
            firstLocation = null;
            NavigateTo(path);
        }
        _ = LoadRootsAsync(openDefault: true);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (stateDirectory is not null)
            WindowPosition.Save(this, stateDirectory, PlacementFile);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        closed = true;
        lifetime.Cancel();
        lifetime.Dispose();
        navigation?.Cancel();
        treeCancellation.Cancel();
        treeCancellation.Dispose();
        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
    }

    private async Task LoadRootsAsync(bool openDefault)
    {
        var token = treeCancellation.Token;
        try
        {
            var roots = await RunReaderAsync(FolderReader.Roots, token);
            if (closed || token.IsCancellationRequested)
                return;
            Folders.Items.Clear();
            foreach (var root in roots)
                Folders.Items.Add(CreateTreeFolder(root.Name, root.Path));
            var places = roots.Where(root => root.Kind == BrowserLocationKind.KnownFolder).ToArray();
            Places.ItemsSource = places;
            PlacesCard.Visibility = places.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            if (openDefault && !navigationRequested)
            {
                if (roots.Count == 0)
                    ShowStatus("No local folders are available. Enter a folder path to browse.");
                else
                    NavigateTo(roots[0].Path);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (FolderReader.IsFolderError(error))
        {
            if (!closed && !token.IsCancellationRequested)
                ShowStatus($"Could not list local folders. {error.Message}");
        }
    }

    private TreeViewItem CreateTreeFolder(string name, string path)
    {
        var item = new TreeViewItem { Header = name, Tag = path, ToolTip = path };
        item.Items.Add(new TreeViewItem { Header = "Loading…", IsEnabled = false });
        item.Expanded += ExpandFolder;
        return item;
    }

    private async void ExpandFolder(object sender, RoutedEventArgs e)
    {
        if (sender is not TreeViewItem { Tag: string path } item || !ReferenceEquals(item, e.OriginalSource) || closed)
            return;
        item.Expanded -= ExpandFolder;
        var token = treeCancellation.Token;
        try
        {
            var children = await RunReaderAsync(ct => FolderReader.Read(path, foldersOnly: true, ct), token);
            if (closed || token.IsCancellationRequested)
                return;
            item.Items.Clear();
            foreach (var child in children.Entries)
                item.Items.Add(CreateTreeFolder(child.Name, child.Path));
            if (children.Limited)
                item.Items.Add(new TreeViewItem { Header = "More folders in Windows Explorer", IsEnabled = false });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (FolderReader.IsFolderError(error))
        {
            if (closed || token.IsCancellationRequested)
                return;
            item.Items.Clear();
            item.Items.Add(new TreeViewItem { Header = "Folder unavailable", ToolTip = error.Message, IsEnabled = false });
        }
    }

    private async Task NavigateAsync(string target, Navigation direction, int destinationIndex = -1)
    {
        if (closed)
            return;
        navigationGeneration++;
        navigation?.Cancel();
        using var request = new CancellationTokenSource();
        navigation = request;
        ShowStatus("Loading Folder…");
        try
        {
            var result = await RunReaderAsync(ct =>
            {
                var path = FolderReader.GetLocalPath(target);
                return (Path.TrimEndingDirectorySeparator(path), FolderReader.Read(path, foldersOnly: false, ct));
            }, request.Token);
            if (closed || request.IsCancellationRequested)
                return;

            if (direction == Navigation.History)
                historyIndex = destinationIndex;
            else if (direction == Navigation.New)
                Remember(result.Item1);

            currentFolder = result.Item1;
            Address.Text = currentFolder;
            Details.ItemsSource = result.Item2.Entries;
            if (result.Item2.Entries.Count > 0)
                Details.ScrollIntoView(result.Item2.Entries[0]);
            UpdateButtons();
            ShowStatus(result.Item2.Limited
                ? $"{result.Item2.Entries.Count:N0} items shown. Open in Windows Explorer for the complete folder."
                : $"{result.Item2.Entries.Count:N0} {(result.Item2.Entries.Count == 1 ? "item" : "items")}");

            if (direction == Navigation.Refresh)
            {
                treeCancellation.Cancel();
                treeCancellation.Dispose();
                treeCancellation = new CancellationTokenSource();
                _ = LoadRootsAsync(openDefault: false);
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception error) when (FolderReader.IsFolderError(error))
        {
            if (!closed && !request.IsCancellationRequested)
            {
                var fallback = currentFolder is null ? "" : " The Windows Explorer button opens the folder still shown here.";
                ShowStatus($"Could not open the folder. {error.Message}{fallback}");
            }
        }
        finally
        {
            if (ReferenceEquals(navigation, request))
                navigation = null;
        }
    }

    private async Task<T> RunReaderAsync<T>(Func<CancellationToken, T> read, CancellationToken token)
    {
        await readers.WaitAsync(token).ConfigureAwait(false);
        try { return await Task.Run(() => read(token), token).ConfigureAwait(false); }
        finally { readers.Release(); }
    }

    private void Remember(string path)
    {
        if (historyIndex >= 0 && string.Equals(history[historyIndex], path, StringComparison.OrdinalIgnoreCase))
            return;
        history.RemoveRange(historyIndex + 1, history.Count - historyIndex - 1);
        history.Add(path);
        if (history.Count > HistoryLimit)
            history.RemoveAt(0);
        historyIndex = history.Count - 1;
    }

    private void UpdateButtons()
    {
        BackAction.IsEnabled = historyIndex > 0;
        ForwardAction.IsEnabled = historyIndex + 1 < history.Count;
        UpAction.IsEnabled = currentFolder is not null && Path.GetDirectoryName(currentFolder) is not null;
        RefreshAction.IsEnabled = currentFolder is not null;
        NativeExplorer.IsEnabled = currentFolder is not null;
    }

    private void ShowStatus(string text)
    {
        Status.Text = text;
        UIElementAutomationPeer.FromElement(Status)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void BackClick(object sender, RoutedEventArgs e) => MoveHistory(-1);
    private void ForwardClick(object sender, RoutedEventArgs e) => MoveHistory(1);

    private void MoveHistory(int offset)
    {
        var index = historyIndex + offset;
        if (index >= 0 && index < history.Count)
            _ = NavigateAsync(history[index], Navigation.History, index);
    }

    private void UpClick(object sender, RoutedEventArgs e)
    {
        if (currentFolder is not null)
            NavigateTo(Path.GetDirectoryName(currentFolder));
    }

    private void RefreshClick(object sender, RoutedEventArgs e)
    {
        if (currentFolder is not null)
            _ = NavigateAsync(currentFolder, Navigation.Refresh);
    }

    private void FolderSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeViewItem { Tag: string path })
            NavigateTo(path);
    }

    private void PlaceClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path })
            NavigateTo(path);
    }

    private void AddressClick(object sender, RoutedEventArgs e) => NavigateTo(Address.Text.Trim());

    private void AddressKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            NavigateTo(Address.Text.Trim());
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5)
        {
            RefreshClick(sender, e);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.L)
        {
            Address.Focus();
            Address.SelectAll();
            e.Handled = true;
        }
    }

    private void DetailsDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject target &&
            ItemsControl.ContainerFromElement(Details, target) is ListViewItem row &&
            row.Content is BrowserEntry entry)
            _ = OpenEntryAsync(entry);
    }

    private void DetailsKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Details.SelectedItem is BrowserEntry entry)
        {
            e.Handled = true;
            _ = OpenEntryAsync(entry);
        }
    }

    private async Task OpenEntryAsync(BrowserEntry entry)
    {
        if (closed || openingEntry)
            return;
        if (entry.Link)
        {
            ShowStatus("Use Open in Windows Explorer to access this link or redirected item.");
            return;
        }
        if (entry.Directory)
        {
            NavigateTo(entry.Path);
            return;
        }

        var generation = navigationGeneration;
        var token = lifetime.Token;
        openingEntry = true;
        try
        {
            await RunReaderAsync(ct =>
            {
                FolderReader.ValidateDirectory(Path.GetDirectoryName(entry.Path)!, ct);
                if ((File.GetAttributes(entry.Path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                    throw new IOException("The item has changed. Refresh the folder, or use Windows Explorer.");
                return true;
            }, token);
            if (!closed && generation == navigationGeneration)
                ShellOpen(entry.Path, verb: null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (FolderReader.IsFolderError(error))
        {
            if (!closed && generation == navigationGeneration)
                ShowStatus($"Could not open the item. {error.Message}");
        }
        finally { openingEntry = false; }
    }

    private void NativeExplorerClick(object sender, RoutedEventArgs e)
    {
        if (currentFolder is not null)
            ShellOpen(currentFolder, verb: "explore");
    }

    private void ShellOpen(string path, string? verb)
    {
        try
        {
            var start = new ProcessStartInfo(path) { UseShellExecute = true };
            if (verb is not null)
                start.Verb = verb;
            Process.Start(start)?.Dispose();
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            ShowStatus($"Windows could not open the item. {error.Message}");
        }
    }

    private void MinimizeClick(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    private void MaximizeClick(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(this);
        else
            SystemCommands.MaximizeWindow(this);
    }

    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast))
            _ = Dispatcher.InvokeAsync(() => { if (!closed) SetVariant(variant); });
    }

    private static Color ToColor(int value) => Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);

    private static SolidColorBrush Solid(int value)
    {
        var brush = new SolidColorBrush(ToColor(value));
        brush.Freeze();
        return brush;
    }

    private enum Navigation { New, History, Refresh }
}
