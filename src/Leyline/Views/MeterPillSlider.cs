using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Leyline.Services;
using Windows.Foundation;

namespace Leyline.Views;

/// <summary>
/// Custom WinUI 3 Pill Slider with integrated stereo L/R green level meters,
/// subtle unity tick mark, and circular thumb matching the reference screenshots 1:1.
/// </summary>
public sealed class MeterPillSlider : Grid
{
    private readonly Border _pillTrack;
    private readonly Canvas _meterCanvas;
    private readonly Border _meterBarLeft;
    private readonly Border _meterBarRight;
    private readonly Border _unityTick;
    private readonly Ellipse _thumb;
    private bool _isDragging;

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(
            nameof(Value),
            typeof(double),
            typeof(MeterPillSlider),
            new PropertyMetadata(0.85, OnVisualStateChanged));

    public static readonly DependencyProperty MeterLeftProperty =
        DependencyProperty.Register(
            nameof(MeterLeft),
            typeof(double),
            typeof(MeterPillSlider),
            new PropertyMetadata(0.0, OnVisualStateChanged));

    public static readonly DependencyProperty MeterRightProperty =
        DependencyProperty.Register(
            nameof(MeterRight),
            typeof(double),
            typeof(MeterPillSlider),
            new PropertyMetadata(0.0, OnVisualStateChanged));

    public static readonly DependencyProperty IsMutedThemeProperty =
        DependencyProperty.Register(
            nameof(IsMutedTheme),
            typeof(bool),
            typeof(MeterPillSlider),
            new PropertyMetadata(false, OnVisualStateChanged));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, Math.Clamp(value, 0.0, 1.0));
    }

    public double MeterLeft
    {
        get => (double)GetValue(MeterLeftProperty);
        set => SetValue(MeterLeftProperty, Math.Clamp(value, 0.0, 1.0));
    }

    public double MeterRight
    {
        get => (double)GetValue(MeterRightProperty);
        set => SetValue(MeterRightProperty, Math.Clamp(value, 0.0, 1.0));
    }

    public bool IsMutedTheme
    {
        get => (bool)GetValue(IsMutedThemeProperty);
        set => SetValue(IsMutedThemeProperty, value);
    }

    public MeterPillSlider()
    {
        Height = 22;
        MinWidth = 54;
        Background = ThemeService.CreateBrush("#00000000");

        _meterBarLeft = new Border
        {
            Height = 3.2,
            CornerRadius = new CornerRadius(1.6),
            Background = ThemeService.CreateBrush("#2BD968")
        };

        _meterBarRight = new Border
        {
            Height = 3.2,
            CornerRadius = new CornerRadius(1.6),
            Background = ThemeService.CreateBrush("#2BD968")
        };

        _unityTick = new Border
        {
            Width = 1.5,
            Height = 7.0,
            Background = ThemeService.CreateBrush("#99D1D1D6"),
            Visibility = Visibility.Collapsed
        };

        _meterCanvas = new Canvas
        {
            Height = 11,
            VerticalAlignment = VerticalAlignment.Center
        };
        _meterCanvas.Children.Add(_meterBarLeft);
        _meterCanvas.Children.Add(_meterBarRight);
        _meterCanvas.Children.Add(_unityTick);

        _pillTrack = new Border
        {
            Height = 11,
            CornerRadius = new CornerRadius(5.5),
            Background = ThemeService.CreateBrush("#38383E"),
            VerticalAlignment = VerticalAlignment.Center,
            Child = _meterCanvas
        };

        _thumb = new Ellipse
        {
            Width = 11.5,
            Height = 11.5,
            Fill = ThemeService.CreateBrush("#B6A8FF"),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };

        Children.Add(_pillTrack);
        Children.Add(_thumb);

        SizeChanged += (_, _) => UpdateVisuals();
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCanceled += OnPointerReleased;
        PointerWheelChanged += OnPointerWheelChanged;
        ThemeService.ThemeChanged += _ => UpdateVisuals();
    }

    private static void OnVisualStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MeterPillSlider slider)
            slider.UpdateVisuals();
    }

    private void UpdateVisuals()
    {
        double w = ActualWidth;
        if (w <= 12) return;

        bool isLight = ThemeService.CurrentIsLightMode;
        bool muted = IsMutedTheme;

        if (muted)
        {
            _pillTrack.Background = ThemeService.CreateBrush(isLight ? "#E6B8B5" : "#5A3333");
        }
        else
        {
            _pillTrack.Background = ThemeService.CreateBrush(isLight ? "#CBD0E2" : "#38383E");
        }

        _thumb.Fill = ThemeService.CreateBrush(isLight ? "#4355B9" : "#B6A8FF");
        var greenBrush = ThemeService.CreateBrush(isLight ? "#1E8E3E" : "#2BD968");
        _meterBarLeft.Background = greenBrush;
        _meterBarRight.Background = greenBrush;

        double thumbSize = _thumb.Width;
        double usable = Math.Max(0.0, w - thumbSize);
        double val = Math.Clamp(Value, 0.0, 1.0);
        double thumbX = val * usable;

        _thumb.Margin = new Thickness(thumbX, 0, 0, 0);

        double innerMax = Math.Max(0.0, w - 6.0);
        double lWidth = Math.Clamp(MeterLeft, 0.0, 1.0) * innerMax;
        double rWidth = Math.Clamp(MeterRight, 0.0, 1.0) * innerMax;

        _meterBarLeft.Width = lWidth;
        Canvas.SetLeft(_meterBarLeft, 3.0);
        Canvas.SetTop(_meterBarLeft, 1.8);

        _meterBarRight.Width = rWidth;
        Canvas.SetLeft(_meterBarRight, 3.0);
        Canvas.SetTop(_meterBarRight, 6.0);

        if (MeterLeft > 0.01 || MeterRight > 0.01)
        {
            _unityTick.Visibility = Visibility.Visible;
            Canvas.SetLeft(_unityTick, Math.Max(3.0, w * 0.52));
            Canvas.SetTop(_unityTick, 2.0);
        }
        else
        {
            _unityTick.Visibility = Visibility.Collapsed;
        }
    }

    private void SetValueFromPointer(Point pt)
    {
        double w = ActualWidth;
        double thumbSize = _thumb.Width;
        double usable = Math.Max(1.0, w - thumbSize);
        double ratio = (pt.X - (thumbSize * 0.5)) / usable;
        Value = Math.Clamp(ratio, 0.0, 1.0);
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        PointerPoint pp = e.GetCurrentPoint(this);
        if (pp.Properties.IsLeftButtonPressed)
        {
            _isDragging = true;
            CapturePointer(e.Pointer);
            SetValueFromPointer(pp.Position);
            e.Handled = true;
        }
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging) return;
        PointerPoint pp = e.GetCurrentPoint(this);
        SetValueFromPointer(pp.Position);
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_isDragging)
        {
            _isDragging = false;
            ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        int delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
        double step = delta > 0 ? 0.03 : -0.03;
        Value = Math.Clamp(Value + step, 0.0, 1.0);
        e.Handled = true;
    }
}
