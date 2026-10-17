using System.Windows;
using System.Windows.Media;

namespace SysTune.App.Controls;

/// <summary>
/// Allocation-light sparkline drawn directly in OnRender (no visual tree per point).
/// Values are plotted oldest → newest, right-aligned to <see cref="Capacity"/> slots.
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(double[]), typeof(Sparkline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender, OnBrushChanged));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(Sparkline), new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>When true the maximum follows the data (used for network rates).</summary>
    public static readonly DependencyProperty AutoScaleProperty = DependencyProperty.Register(
        nameof(AutoScale), typeof(bool), typeof(Sparkline), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CapacityProperty = DependencyProperty.Register(
        nameof(Capacity), typeof(int), typeof(Sparkline), new FrameworkPropertyMetadata(60, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowFillProperty = DependencyProperty.Register(
        nameof(ShowFill), typeof(bool), typeof(Sparkline), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    private Pen? _pen;
    private Brush? _fill;

    public double[]? Values
    {
        get => (double[]?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public bool AutoScale
    {
        get => (bool)GetValue(AutoScaleProperty);
        set => SetValue(AutoScaleProperty, value);
    }

    public int Capacity
    {
        get => (int)GetValue(CapacityProperty);
        set => SetValue(CapacityProperty, value);
    }

    public bool ShowFill
    {
        get => (bool)GetValue(ShowFillProperty);
        set => SetValue(ShowFillProperty, value);
    }

    private static void OnBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var s = (Sparkline)d;
        s._pen = null;
        s._fill = null;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var values = Values;
        var w = ActualWidth;
        var h = ActualHeight;
        if (values is null || values.Length < 2 || w <= 0 || h <= 0) return;

        var max = Maximum;
        if (AutoScale)
        {
            max = 1;
            foreach (var v in values) max = Math.Max(max, v);
            max *= 1.15;
        }
        if (max <= 0) max = 1;

        var cap = Math.Max(Capacity, values.Length);
        var step = w / (cap - 1);
        var x0 = w - (values.Length - 1) * step;
        const double inset = 1.0;
        var usable = h - inset * 2;

        if (_pen is null)
        {
            _pen = new Pen(Stroke.CloneCurrentValue(), 1.4) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            _pen.Freeze();
            if (Stroke is SolidColorBrush scb)
            {
                var c = scb.Color;
                _fill = new LinearGradientBrush(Color.FromArgb(70, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B), 90);
                _fill.Freeze();
            }
        }

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            for (var i = 0; i < values.Length; i++)
            {
                var y = inset + usable - Math.Clamp(values[i] / max, 0, 1) * usable;
                var p = new Point(x0 + i * step, y);
                if (i == 0) ctx.BeginFigure(p, false, false);
                else ctx.LineTo(p, true, true);
            }
        }
        line.Freeze();

        if (ShowFill && _fill is not null)
        {
            var area = new StreamGeometry();
            using (var ctx = area.Open())
            {
                ctx.BeginFigure(new Point(x0, h), true, true);
                for (var i = 0; i < values.Length; i++)
                {
                    var y = inset + usable - Math.Clamp(values[i] / max, 0, 1) * usable;
                    ctx.LineTo(new Point(x0 + i * step, y), false, false);
                }
                ctx.LineTo(new Point(w, h), false, false);
            }
            area.Freeze();
            dc.DrawGeometry(_fill, null, area);
        }
        dc.DrawGeometry(null, _pen, line);
    }
}
