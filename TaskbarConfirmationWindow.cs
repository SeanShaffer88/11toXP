using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;

namespace LunaDesktop;

internal sealed class TaskbarConfirmationWindow : Window
{
    private static readonly TimeSpan PreviewDuration = TimeSpan.FromSeconds(15);
    private readonly long startedAt;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(125) };
    private readonly TextBlock countdown = new() { Margin = new Thickness(0, 10, 0, 14) };

    internal TaskbarConfirmationWindow(ControlWindow owner, long startedAt, bool resizeOnly)
    {
        this.startedAt = startedAt;
        Owner = owner;
        Title = "Keep Taskbar Changes";
        Width = 430;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = owner.FontFamily;
        FontSize = owner.FontSize;
        Resources.MergedDictionaries.Add(owner.Resources);
        SetResourceReference(BackgroundProperty, "Panel");
        SetResourceReference(ForegroundProperty, "Ink");

        var content = new StackPanel { Margin = new Thickness(18, 15, 18, 16) };
        content.Children.Add(new TextBlock
        {
            Text = "Keep this taskbar layout? The previous layout returns when time runs out.",
            TextWrapping = TextWrapping.Wrap
        });
        if (!resizeOnly)
            content.Children.Add(new TextBlock
            {
                Text = "Keep also applies your other selected settings.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 5, 0, 0)
            });
        AutomationProperties.SetAutomationId(countdown, "TaskbarPreviewCountdown");
        content.Children.Add(countdown);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var keep = new Button { Content = "_Keep", MinWidth = 86, Margin = new Thickness(0, 0, 6, 0) };
        var undo = new Button { Content = "_Undo", MinWidth = 86, IsCancel = true };
        AutomationProperties.SetAutomationId(keep, "KeepTaskbarChanges");
        AutomationProperties.SetAutomationId(undo, "UndoTaskbarChanges");
        keep.Click += (_, _) => DialogResult = TimeRemaining > TimeSpan.Zero;
        actions.Children.Add(keep);
        actions.Children.Add(undo);
        content.Children.Add(actions);
        Content = content;

        timer.Tick += (_, _) => UpdateCountdown();
        Loaded += (_, _) =>
        {
            undo.Focus();
            if (UpdateCountdown())
                timer.Start();
        };
        Closed += (_, _) => timer.Stop();
    }

    private TimeSpan TimeRemaining => PreviewDuration - Stopwatch.GetElapsedTime(startedAt);

    private bool UpdateCountdown()
    {
        var remaining = (int)Math.Ceiling(TimeRemaining.TotalSeconds);
        if (remaining <= 0)
        {
            DialogResult = false;
            return false;
        }
        countdown.Text = remaining == 1 ? "1 second remaining" : $"{remaining} seconds remaining";
        return true;
    }
}
