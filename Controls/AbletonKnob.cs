using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace US16x08_AbletonPanel.Controls;

public sealed class AbletonKnob : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(
            nameof(Value),
            typeof(double),
            typeof(AbletonKnob),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MinimumProperty =
        DependencyProperty.Register(
            nameof(Minimum),
            typeof(double),
            typeof(AbletonKnob),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty =
        DependencyProperty.Register(
            nameof(Maximum),
            typeof(double),
            typeof(AbletonKnob),
            new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DefaultValueProperty =
        DependencyProperty.Register(
            nameof(DefaultValue),
            typeof(double),
            typeof(AbletonKnob),
            new FrameworkPropertyMetadata(0.0));

    public static readonly DependencyProperty IsBipolarProperty =
        DependencyProperty.Register(
            nameof(IsBipolar),
            typeof(bool),
            typeof(AbletonKnob),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsLogarithmicProperty =
        DependencyProperty.Register(
            nameof(IsLogarithmic),
            typeof(bool),
            typeof(AbletonKnob),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentBrushProperty =
        DependencyProperty.Register(
            nameof(AccentBrush),
            typeof(Brush),
            typeof(AbletonKnob),
            new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x00)), FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double DefaultValue
    {
        get => (double)GetValue(DefaultValueProperty);
        set => SetValue(DefaultValueProperty, value);
    }

    public bool IsBipolar
    {
        get => (bool)GetValue(IsBipolarProperty);
        set => SetValue(IsBipolarProperty, value);
    }

    public bool IsLogarithmic
    {
        get => (bool)GetValue(IsLogarithmicProperty);
        set => SetValue(IsLogarithmicProperty, value);
    }

    public Brush AccentBrush
    {
        get => (Brush)GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    private bool _isDragging;
    private Point _dragStartPoint;
    private double _dragStartNormalized;

    private static readonly Brush TrackBgBrush = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x16));
    private static readonly BrushDialBodyBrush DialBody = new();
    private static readonly Pen TrackPen = new(new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14)), 3.2)
    {
        StartLineCap = PenLineCap.Round,
        EndLineCap = PenLineCap.Round
    };
    private static readonly Pen BorderPen = new(new SolidColorBrush(Color.FromRgb(0x42, 0x42, 0x42)), 1.0);
    private static readonly Pen PointerPen = new(Brushes.White, 1.8)
    {
        StartLineCap = PenLineCap.Round,
        EndLineCap = PenLineCap.Round
    };

    private sealed class BrushDialBodyBrush
    {
        public readonly Brush Normal = new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B));
        public readonly Brush Hover = new SolidColorBrush(Color.FromRgb(0x35, 0x35, 0x35));
    }

    public AbletonKnob()
    {
        Width = 38;
        Height = 38;
        Cursor = Cursors.SizeNS;
        Focusable = true;
    }

    private double ValueToNormalized(double val)
    {
        double min = Minimum;
        double max = Maximum;
        if (max <= min) return 0.0;
        double clamped = Math.Clamp(val, min, max);
        if (IsLogarithmic && min > 0)
        {
            return (Math.Log(clamped) - Math.Log(min)) / (Math.Log(max) - Math.Log(min));
        }
        return (clamped - min) / (max - min);
    }

    private double NormalizedToValue(double norm)
    {
        double min = Minimum;
        double max = Maximum;
        norm = Math.Clamp(norm, 0.0, 1.0);
        if (IsLogarithmic && min > 0)
        {
            return Math.Exp(Math.Log(min) + norm * (Math.Log(max) - Math.Log(min)));
        }
        return min + norm * (max - min);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ClickCount == 2)
        {
            Value = DefaultValue;
            e.Handled = true;
            return;
        }

        _isDragging = true;
        _dragStartPoint = e.GetPosition(this);
        _dragStartNormalized = ValueToNormalized(Value);
        CaptureMouse();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_isDragging) return;

        Point current = e.GetPosition(this);
        double deltaY = _dragStartPoint.Y - current.Y;
        double sensitivity = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 450.0 : 130.0;
        double newNorm = Math.Clamp(_dragStartNormalized + (deltaY / sensitivity), 0.0, 1.0);
        Value = NormalizedToValue(newNorm);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_isDragging)
        {
            _isDragging = false;
            ReleaseMouseCapture();
            InvalidateVisual();
            e.Handled = true;
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        double step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 0.01 : 0.04;
        double norm = ValueToNormalized(Value);
        Value = NormalizedToValue(norm + (e.Delta > 0 ? step : -step));
        e.Handled = true;
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        double h = ActualHeight;
        double size = Math.Min(w, h);
        if (size < 10) return;

        Point center = new(w * 0.5, h * 0.5);
        double outerRadius = (size * 0.5) - 2.5;
        double bodyRadius = outerRadius - 3.5;

        // Transparent hit-test backing
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        // 1. Background 270-degree arc (from 135 deg to 405 deg in screen coordinates)
        DrawArc(dc, TrackPen, center, outerRadius, 135.0, 405.0);

        // 2. Active value arc
        double norm = ValueToNormalized(Value);
        Pen activePen = new(AccentBrush, 3.2)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };

        if (IsBipolar)
        {
            double midAngle = 270.0;
            double valAngle = 135.0 + (norm * 270.0);
            if (Math.Abs(valAngle - midAngle) > 1.5)
            {
                DrawArc(dc, activePen, center, outerRadius, Math.Min(midAngle, valAngle), Math.Max(midAngle, valAngle));
            }
        }
        else
        {
            double endAngle = 135.0 + (norm * 270.0);
            if (endAngle > 136.5)
            {
                DrawArc(dc, activePen, center, outerRadius, 135.0, endAngle);
            }
        }

        // 3. Inner knob body
        Brush bodyFill = (IsMouseOver || _isDragging) ? DialBody.Hover : DialBody.Normal;
        dc.DrawEllipse(bodyFill, BorderPen, center, bodyRadius, bodyRadius);

        // 4. Pointer line from 28% radius to body edge
        double angleDeg = 135.0 + (norm * 270.0);
        double angleRad = angleDeg * Math.PI / 180.0;
        Point pStart = new(
            center.X + Math.Cos(angleRad) * (bodyRadius * 0.25),
            center.Y + Math.Sin(angleRad) * (bodyRadius * 0.25));
        Point pEnd = new(
            center.X + Math.Cos(angleRad) * (bodyRadius - 1.2),
            center.Y + Math.Sin(angleRad) * (bodyRadius - 1.2));

        dc.DrawLine(PointerPen, pStart, pEnd);
    }

    private static void DrawArc(DrawingContext dc, Pen pen, Point center, double radius, double startAngleDeg, double endAngleDeg)
    {
        double sweep = endAngleDeg - startAngleDeg;
        if (sweep <= 0.5) return;

        double startRad = startAngleDeg * Math.PI / 180.0;
        double endRad = endAngleDeg * Math.PI / 180.0;

        Point startPt = new(center.X + radius * Math.Cos(startRad), center.Y + radius * Math.Sin(startRad));
        Point endPt = new(center.X + radius * Math.Cos(endRad), center.Y + radius * Math.Sin(endRad));

        StreamGeometry geom = new();
        using (StreamGeometryContext ctx = geom.Open())
        {
            ctx.BeginFigure(startPt, false, false);
            ctx.ArcTo(
                endPt,
                new Size(radius, radius),
                0,
                sweep > 180.0,
                SweepDirection.Clockwise,
                true,
                false);
        }
        geom.Freeze();
        dc.DrawGeometry(null, pen, geom);
    }
}
