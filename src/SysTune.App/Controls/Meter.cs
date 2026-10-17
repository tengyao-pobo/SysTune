using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SysTune.App.Controls;

/// <summary>Thin rounded progress meter (0-100) with a short eased animation between values.</summary>
public sealed class Meter : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(Meter), new PropertyMetadata(0.0, OnValueChanged));

    private static readonly DependencyProperty DisplayValueProperty = DependencyProperty.Register(
        "DisplayValue", typeof(double), typeof(Meter), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(Meter), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track), typeof(Brush), typeof(Meter), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };

    static Meter() => ((CubicEase)Ease).Freeze();

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public Brush Track
    {
        get => (Brush)GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var m = (Meter)d;
        var target = Math.Clamp((double)e.NewValue, 0, 100);
        if (!m.IsVisible || SystemParameters.ClientAreaAnimation == false)
        {
            m.BeginAnimation(DisplayValueProperty, null);
            m.SetValue(DisplayValueProperty, target);
            return;
        }
        var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(350)) { EasingFunction = Ease };
        anim.Freeze();
        m.BeginAnimation(DisplayValueProperty, anim, HandoffBehavior.SnapshotAndReplace);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var r = h / 2;
        dc.DrawRoundedRectangle(Track, null, new Rect(0, 0, w, h), r, r);
        var v = (double)GetValue(DisplayValueProperty) / 100.0;
        if (v <= 0) return;
        var fw = Math.Max(h, w * v);
        dc.DrawRoundedRectangle(Fill, null, new Rect(0, 0, fw, h), r, r);
    }
}
