using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ScreenGuide;

// Draw the angled surfaces without skewing their text or changing hit targets.
internal sealed class AngledBorder : Border
{
    public static readonly DependencyProperty ShadowBrushProperty = DependencyProperty.Register(
        nameof(ShadowBrush), typeof(Brush), typeof(AngledBorder),
        new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush ShadowBrush { get => (Brush)GetValue(ShadowBrushProperty); set => SetValue(ShadowBrushProperty, value); }
    public static readonly DependencyProperty ReserveShadowProperty = DependencyProperty.Register(
        nameof(ReserveShadow), typeof(bool), typeof(AngledBorder),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public bool ReserveShadow { get => (bool)GetValue(ReserveShadowProperty); set => SetValue(ReserveShadowProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 12 || h < 8) return;
        double offset = ReserveShadow ? 4 : 0;
        double slant = Math.Min(11, Math.Min((w - offset) / 5, (h - offset) / 2));
        Geometry Shape(double dx, double dy)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(slant + dx, dy), true, true);
                c.LineTo(new Point(w - offset + dx, dy), true, false);
                c.LineTo(new Point(w - offset - slant + dx, h - offset + dy), true, false);
                c.LineTo(new Point(dx, h - offset + dy), true, false);
            }
            g.Freeze(); return g;
        }
        if (offset > 0) dc.DrawGeometry(ShadowBrush, null, Shape(offset, offset));
        var pen = BorderThickness.Left > 0 && BorderBrush != null ? new Pen(BorderBrush, BorderThickness.Left) : null;
        dc.DrawGeometry(Background, pen, Shape(0, 0));
    }
}

internal static class ShellTheme
{
    internal static readonly SolidColorBrush Surface = ColorBrush(0x11, 0x11, 0x11);
    internal static readonly SolidColorBrush SurfaceBlue = ColorBrush(0x1E, 0x1E, 0x1E);
    internal static readonly SolidColorBrush Ink = ColorBrush(0xFA, 0xFA, 0xFA);
    internal static readonly SolidColorBrush Muted = ColorBrush(0xB1, 0xB1, 0xB1);
    internal static readonly SolidColorBrush Accent = ColorBrush(0xFF, 0xD4, 0x3B);
    internal static readonly SolidColorBrush Line = ColorBrush(0x43, 0x43, 0x43);
    private static readonly SolidColorBrush AccentHover = ColorBrush(0xFF, 0xE1, 0x6B);
    private static readonly SolidColorBrush AccentPressed = ColorBrush(0xE8, 0xBA, 0x19);
    private static readonly SolidColorBrush SoftHover = ColorBrush(0xFF, 0xF3, 0xC4);
    private static readonly SolidColorBrush SoftPressed = ColorBrush(0xF2, 0xE2, 0x9B);
    private static readonly SolidColorBrush QuietHover = ColorBrush(0x2B, 0x2B, 0x2B);
    private static readonly SolidColorBrush QuietPressed = ColorBrush(0x36, 0x36, 0x36);

    internal static Button RoundedButton(string text, bool primary = false, bool quiet = false)
    {
        var border = new FrameworkElementFactory(typeof(AngledBorder));
        border.Name = "surface";
        if (!quiet)
        {
            border.SetValue(AngledBorder.ShadowBrushProperty, primary ? Ink : Accent);
            border.SetValue(AngledBorder.ReserveShadowProperty, true);
        }
        Bind(border, Border.BackgroundProperty, nameof(Control.Background));
        Bind(border, Border.BorderBrushProperty, nameof(Control.BorderBrush));
        Bind(border, Border.BorderThicknessProperty, nameof(Control.BorderThickness));
        Bind(border, Border.PaddingProperty, nameof(Control.Padding));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        content.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
        Bind(content, ContentPresenter.ContentProperty, nameof(ContentControl.Content));
        Bind(content, ContentPresenter.ContentTemplateProperty, nameof(ContentControl.ContentTemplate));
        border.AppendChild(content);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        if (!quiet)
        {
            var recessed = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            recessed.Setters.Add(new Setter(AngledBorder.ShadowBrushProperty, Brushes.Transparent, "surface"));
            recessed.Setters.Add(new Setter(UIElement.RenderTransformProperty, new TranslateTransform(4, 4), "surface"));
            template.Triggers.Add(recessed);
        }
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        style.Setters.Add(new Setter(Control.BackgroundProperty, primary ? Accent : quiet ? Brushes.Transparent : Ink));
        style.Setters.Add(new Setter(Control.ForegroundProperty, quiet ? Ink : Surface));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, primary || quiet ? Brushes.Transparent : Line));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(primary || quiet ? 0 : 1)));
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, primary ? AccentHover : quiet ? QuietHover : SoftHover));
        style.Triggers.Add(hover);
        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Control.BackgroundProperty, primary ? AccentPressed : quiet ? QuietPressed : SoftPressed));
        style.Triggers.Add(pressed);
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45));
        style.Triggers.Add(disabled);
        return new Button
        {
            Content = text, Style = style, Focusable = false, IsTabStop = false,
            MinHeight = 32, FontSize = 12, Padding = new Thickness(12, 7, 12, 7),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
    }

    internal static Grid MakeOrb()
    {
        var orb = new Grid
        {
            Width = 48, Height = 48, Background = Brushes.Transparent,
            ToolTip = "Click to expand · click again for settings · drag to dock", ClipToBounds = false
        };
        orb.Children.Add(new Ellipse
        {
            Width = 42, Height = 42, Fill = Ink,
            IsHitTestVisible = false
        });
        var dots = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        for (int i = 0; i < 3; i++) dots.Children.Add(new Border
        {
            Width = 3, Height = 3, CornerRadius = new CornerRadius(1.5),
            Background = Surface, Margin = new Thickness(2, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center
        });
        orb.Children.Add(dots);
        return orb;
    }

    internal static void SetOrbVoiceState(Grid orb, bool enabled, bool listening)
    {
        ((Ellipse)orb.Children[0]).Fill = enabled ? Accent : Ink;
        var marks = (StackPanel)orb.Children[1];
        for (int i = 0; i < marks.Children.Count; i++)
            ((Border)marks.Children[i]).Height = enabled ? listening ? (i == 1 ? 14 : 8) : 7 : 3;
        orb.ToolTip = enabled ? listening ? "语音对话正在听 · Esc 停止 · 单击控制面板" : "语音对话处理中 · Esc 停止 · 单击控制面板" : "Click to expand · click again for settings · drag to dock";
    }

    private static SolidColorBrush ColorBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private static void Bind(FrameworkElementFactory factory, DependencyProperty property, string source)
        => factory.SetBinding(property, new Binding(source) { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
}
