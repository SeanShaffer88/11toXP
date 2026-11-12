using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace LunaDesktop.Shell;

internal sealed class LunaVisuals
{
    private static readonly FontFamily Tahoma = new("Tahoma");
    internal bool HighContrast { get; } = SystemParameters.HighContrast;
    internal Brush Bar { get; }
    internal Brush BarText { get; }
    internal Brush Tray { get; }
    internal Brush MenuHeader { get; }
    internal Brush MenuLeft { get; }
    internal Brush MenuRight { get; }
    internal Brush MenuText { get; }
    internal Brush MenuEdge { get; }
    internal Brush Start { get; }
    internal Brush Task { get; }
    internal Brush ActiveTask { get; }
    internal Brush TaskEdge { get; }
    internal Style TaskButtonStyle { get; }
    internal Style ActiveTaskButtonStyle { get; }
    internal Style StartButtonStyle { get; }
    internal Style MenuButtonStyle { get; }

    internal LunaVisuals()
    {
        if (HighContrast)
        {
            Bar = Tray = MenuHeader = MenuLeft = MenuRight = SystemColors.WindowBrush;
            BarText = MenuText = SystemColors.WindowTextBrush;
            MenuEdge = TaskEdge = SystemColors.WindowTextBrush;
            Start = Task = SystemColors.ControlBrush;
            ActiveTask = SystemColors.HighlightBrush;
            TaskButtonStyle = ButtonStyle(SystemColors.ControlTextBrush, SystemColors.HighlightBrush,
                SystemColors.HighlightTextBrush, SystemColors.ControlDarkBrush, 2);
            ActiveTaskButtonStyle = ButtonStyle(SystemColors.HighlightTextBrush, SystemColors.HighlightBrush,
                SystemColors.HighlightTextBrush, SystemColors.ControlDarkBrush, 2);
            StartButtonStyle = TaskButtonStyle;
            MenuButtonStyle = ButtonStyle(MenuText, SystemColors.HighlightBrush,
                SystemColors.HighlightTextBrush, SystemColors.WindowTextBrush, 1);
            return;
        }

        Bar = Gradient((0, 0x72A8F5), (.12, 0x2D74DC), (.27, 0x2462C8), (.91, 0x245BCD), (1, 0x15409D));
        BarText = Brushes.White;
        Tray = Gradient((0, 0x4EBAF1), (.18, 0x177CC2), (.93, 0x116DAF), (1, 0x0D5190));
        MenuHeader = Gradient((0, 0x3789E5), (.22, 0x246CC7), (1, 0x084DA9));
        MenuLeft = Brushes.White;
        MenuRight = Solid(0xD3E7FF);
        MenuText = Solid(0x142743);
        MenuEdge = Solid(0x0F449D);
        Start = Gradient((0, 0x70A45C), (.13, 0x4A8C35), (.24, 0x347D28), (.85, 0x307626), (1, 0x1A4C14));
        Task = Gradient((0, 0x4285DE), (.15, 0x3475D1), (1, 0x2058B5));
        ActiveTask = Gradient((0, 0x163D94), (.4, 0x1B4DAA), (1, 0x235FC4));
        TaskEdge = Solid(0x194CA9);
        TaskButtonStyle = ButtonStyle(BarText, Solid(0x1955AC), Brushes.White, Brushes.White, 3);
        ActiveTaskButtonStyle = TaskButtonStyle;
        StartButtonStyle = ButtonStyle(BarText, Solid(0x2B7023), Brushes.White, Brushes.White, 8);
        MenuButtonStyle = ButtonStyle(MenuText, Solid(0x255DB0), Brushes.White, Solid(0x15418D), 0);
    }

    internal Button NewButton(string name, object content, Style style, Brush background, Brush? border = null)
    {
        var button = new Button
        {
            Content = content,
            Style = style,
            Background = background,
            BorderBrush = border ?? Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 1, 6, 1)
        };
        AutomationProperties.SetName(button, name);
        return button;
    }

    internal static UIElement AppGlyph(bool monochrome)
    {
        var glyph = new Grid { Width = 18, Height = 18, Margin = new Thickness(0, 0, 5, 0) };
        uint[] shades = [0xF7BC40, 0x78C557, 0x60A9EE, 0xE76E45];
        for (int index = 0; index < shades.Length; index++)
        {
            var pane = new Rectangle
            {
                Width = 7,
                Height = 7,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Fill = monochrome ? SystemColors.ControlTextBrush : Solid(shades[index]),
                HorizontalAlignment = index % 2 == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                VerticalAlignment = index < 2 ? VerticalAlignment.Top : VerticalAlignment.Bottom,
                Margin = new Thickness(1)
            };
            glyph.Children.Add(pane);
        }
        return glyph;
    }

    internal static UIElement ShortcutGlyph(string geometry, Brush fill, Brush? stroke = null)
    {
        var path = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(geometry),
            Fill = fill,
            Stroke = stroke,
            StrokeThickness = .75,
            Stretch = Stretch.Uniform,
            Width = 17,
            Height = 15,
            VerticalAlignment = VerticalAlignment.Center
        };
        return path;
    }

    internal static Brush Solid(uint rgb)
    {
        var brush = new SolidColorBrush(Rgb(rgb));
        brush.Freeze();
        return brush;
    }

    internal static Brush Gradient(params (double Offset, uint Rgb)[] stops)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(.5, 0),
            EndPoint = new Point(.5, 1)
        };
        foreach ((double offset, uint rgb) in stops)
            brush.GradientStops.Add(new GradientStop(Rgb(rgb), offset));
        brush.Freeze();
        return brush;
    }

    private static Color Rgb(uint rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    private static Style ButtonStyle(Brush text, Brush hover, Brush hoverText, Brush focus, double cornerRadius)
    {
        var border = new FrameworkElementFactory(typeof(Border), "Chrome");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(cornerRadius));
        border.SetValue(Border.SnapsToDevicePixelsProperty, true);
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
        presenter.SetValue(ContentPresenter.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty,
            new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, hover, "Chrome"));
        hoverTrigger.Setters.Add(new Setter(Control.ForegroundProperty, hoverText));
        template.Triggers.Add(hoverTrigger);
        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Border.BackgroundProperty, hover, "Chrome"));
        pressed.Setters.Add(new Setter(Control.ForegroundProperty, hoverText));
        pressed.Setters.Add(new Setter(Border.BorderBrushProperty, focus, "Chrome"));
        template.Triggers.Add(pressed);
        var focusTrigger = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focusTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, focus, "Chrome"));
        focusTrigger.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(2), "Chrome"));
        template.Triggers.Add(focusTrigger);

        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        style.Setters.Add(new Setter(Control.ForegroundProperty, text));
        style.Setters.Add(new Setter(Control.FontFamilyProperty, Tahoma));
        style.Setters.Add(new Setter(Control.FontSizeProperty, 13d));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
        return style;
    }
}
