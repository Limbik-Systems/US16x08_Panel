using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace US16x08_AbletonPanel.Controls;

public sealed class AbletonFader : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(
            nameof(Value),
            typeof(double),
            typeof(AbletonFader),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentBrushProperty =
        DependencyProperty.Register(
            nameof(AccentBrush),
            typeof(Brush),
            typeof(AbletonFader),
            new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x00)), FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public Brush AccentBrush
    {
        get => (Brush)GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    private bool _isDragging;
    private Point _dragStart;
    private double _dragStartNorm;

    private static readonly Brush SlotBrush = new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x12));
    private static readonly Pen SlotBorderPen = new(new SolidColorBrush(Color.FromRgb(0x32, 0x32, 0x32)), 1.0);
    private static readonly Pen TickPen = new(new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)), 1.0);
    private static readonly Pen ZeroDbPen = new(new SolidColorBrush(Color.FromRgb(0x90, 0x90, 0x90)), 1.2);
    private static readonly Brush CapNormalBrush = new SolidColorBrush(Color.FromRgb(0x3B, 0x3B, 0x3B));
    private static readonly Brush CapHoverBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0x4C, 0x4C));
    private static readonly Pen CapBorderPen = new(new SolidColorBrush(Color.FromRgb(0x78, 0x78, 0x78)), 1.0);
    private static readonly Pen CapLinePen = new(Brushes.White, 1.6);

    static AbletonFader()
    {
        // Default size as metadata, not as local values in the constructor: a local value outranks sizes set
        // inside a DataTemplate, which pinned every channel-strip fader at 148 px while the master stretched.
        WidthProperty.OverrideMetadata(typeof(AbletonFader), new FrameworkPropertyMetadata(30.0));
        HeightProperty.OverrideMetadata(typeof(AbletonFader), new FrameworkPropertyMetadata(148.0));
    }

    public AbletonFader()
    {
        Cursor = Cursors.Hand;
    }

    // Piecewise perceptual audio fader taper (-90 dB to +6 dB, with 0 dB at 0.78 height)
    private static double DbToNormalized(double db)
    {
        db = Math.Clamp(db, -90.0, 6.0);
        if (db >= 0.0)
            return 0.78 + (db / 6.0) * 0.22;
        if (db >= -18.0)
            return 0.42 + ((db + 18.0) / 18.0) * 0.36;
        if (db >= -48.0)
            return 0.14 + ((db + 48.0) / 30.0) * 0.28;
        return ((db + 90.0) / 42.0) * 0.14;
    }

    private static double NormalizedToDb(double norm)
    {
        norm = Math.Clamp(norm, 0.0, 1.0);
        if (norm >= 0.78)
            return ((norm - 0.78) / 0.22) * 6.0;
        if (norm >= 0.42)
            return -18.0 + ((norm - 0.42) / 0.36) * 18.0;
        if (norm >= 0.14)
            return -48.0 + ((norm - 0.14) / 0.28) * 30.0;
        return -90.0 + (norm / 0.14) * 42.0;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ClickCount == 2)
        {
            Value = 0.0; // Reset to 0.0 dB unity gain
            e.Handled = true;
            return;
        }

        _isDragging = true;
        _dragStart = e.GetPosition(this);
        _dragStartNorm = DbToNormalized(Value);
        CaptureMouse();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_isDragging) return;

        double usableHeight = Math.Max(40.0, ActualHeight - 16.0);
        double dy = _dragStart.Y - e.GetPosition(this).Y;
        double scale = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? usableHeight * 3.0 : usableHeight;
        double newNorm = Math.Clamp(_dragStartNorm + (dy / scale), 0.0, 1.0);
        Value = NormalizedToDb(newNorm);
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
        double norm = DbToNormalized(Value);
        double step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 0.008 : 0.025;
        Value = NormalizedToDb(norm + (e.Delta > 0 ? step : -step));
        e.Handled = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        double h = ActualHeight;
        if (h < 24) return;

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        double topPad = 8.0;
        double botPad = 8.0;
        double trackH = h - topPad - botPad;
        double centerX = w * 0.5;

        // 1. Scale tick marks (+6, 0, -6, -12, -24, -48, -90)
        ReadOnlySpan<double> ticks = stackalloc double[] { 6, 0, -6, -12, -18, -24, -36, -48, -90 };
        foreach (double tickDb in ticks)
        {
            double tn = DbToNormalized(tickDb);
            double ty = topPad + (1.0 - tn) * trackH;
            Pen p = Math.Abs(tickDb) < 0.01 ? ZeroDbPen : TickPen;
            double len = Math.Abs(tickDb) < 0.01 ? 7.0 : 4.0;
            dc.DrawLine(p, new Point(centerX - 3 - len, ty), new Point(centerX - 3, ty));
            dc.DrawLine(p, new Point(centerX + 3, ty), new Point(centerX + 3 + len, ty));
        }

        // 2. Fader slot groove
        Rect slotRect = new(centerX - 2.5, topPad, 5.0, trackH);
        dc.DrawRoundedRectangle(SlotBrush, SlotBorderPen, slotRect, 2.0, 2.0);

        // 3. Active fill bar from bottom up to current fader position
        double norm = DbToNormalized(Value);
        double thumbY = topPad + (1.0 - norm) * trackH;
        double fillHeight = (topPad + trackH) - thumbY;
        if (fillHeight > 1.0)
        {
            Rect fillRect = new(centerX - 1.5, thumbY, 3.0, fillHeight);
            dc.DrawRoundedRectangle(AccentBrush, null, fillRect, 1.5, 1.5);
        }

        // 4. Ableton-style rectangular fader cap with crisp center indicator line
        double capW = Math.Min(w - 4.0, 20.0);
        double capH = 11.0;
        Rect capRect = new(centerX - capW * 0.5, thumbY - capH * 0.5, capW, capH);
        Brush capBrush = (IsMouseOver || _isDragging) ? CapHoverBrush : CapNormalBrush;
        dc.DrawRoundedRectangle(capBrush, CapBorderPen, capRect, 2.0, 2.0);
        dc.DrawLine(CapLinePen, new Point(capRect.Left + 2, thumbY), new Point(capRect.Right - 2, thumbY));
    }
}
