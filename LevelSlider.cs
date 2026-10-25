using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace NotchApp;

// Slider vertical da Central de Controle: arrasta em qualquer ponto, a barra branca sobe de baixo.
public sealed class LevelSlider : Grid
{
    static readonly Brush Dark = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E));
    readonly ScaleTransform fill = new(1, 0);
    readonly ScaleTransform press = new(1, 1);
    readonly TextBlock icon;
    double value;

    public event Action<double>? UserChanged;
    public bool Dragging { get; private set; }

    public LevelSlider()
    {
        Cursor = Cursors.Hand;
        Background = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2E));
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = press;
        Children.Add(new Border { Background = Brushes.White, RenderTransformOrigin = new Point(0.5, 1), RenderTransform = fill });
        icon = new TextBlock
        {
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 20,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 16),
            IsHitTestVisible = false,
        };
        Children.Add(icon);
        SizeChanged += (_, _) => Clip = new RectangleGeometry(new Rect(RenderSize), 22, 22);
    }

    public string Glyph
    {
        get => icon.Text;
        set => icon.Text = value;
    }

    public double Value
    {
        get => value;
        set => Set(value, animate: true);
    }

    void Set(double v, bool animate)
    {
        value = Math.Clamp(v, 0, 1);
        if (animate && Settings.Motion)
            fill.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(value, TimeSpan.FromMilliseconds(240)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        else
        {
            fill.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            fill.ScaleY = value;
        }
        // o ícone fica escuro quando a barra branca passa por baixo dele
        icon.Foreground = value > 0.24 ? Dark : Brushes.White;
    }

    void Press(double to)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(180)) { EasingFunction = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut } };
        press.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        press.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        CaptureMouse();
        Dragging = true;
        Press(1.05);
        FromPointer(e);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (Dragging) FromPointer(e);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) => ReleaseMouseCapture();

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        Dragging = false;
        Press(1);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        Set(value + Math.Sign(e.Delta) * 0.05, animate: true);
        UserChanged?.Invoke(value);
        e.Handled = true;
    }

    void FromPointer(MouseEventArgs e)
    {
        Set(1 - e.GetPosition(this).Y / ActualHeight, animate: false);
        UserChanged?.Invoke(value);
    }
}
