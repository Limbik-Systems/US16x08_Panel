using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using US16x08_AbletonPanel.Models;

namespace US16x08_AbletonPanel.Controls;

public sealed class CompressorCurveControl : FrameworkElement
{
    public static readonly DependencyProperty TrackProperty =
        DependencyProperty.Register(
            nameof(Track),
            typeof(TrackChannelModel),
            typeof(CompressorCurveControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnTrackChanged));

    public TrackChannelModel? Track
    {
        get => (TrackChannelModel?)GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    private const double MinDb = -48.0;
    private const double MaxDb = 0.0;
    private bool _isDraggingThreshold;

    private static readonly Brush BgBrush = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14));
    private static readonly Pen BorderPen = new(new SolidColorBrush(Color.FromRgb(0x36, 0x36, 0x36)), 1.0);
    private static readonly Pen GridPen = new(new SolidColorBrush(Color.FromRgb(0x23, 0x23, 0x23)), 1.0);
    private static readonly Pen UnityDiagPen = new(new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)), 1.2) { DashStyle = DashStyles.Dash };
    private static readonly Pen ThresholdLinePen = new(new SolidColorBrush(Color.FromArgb(160, 0x00, 0xD4, 0xFF)), 1.2) { DashStyle = DashStyles.Dot };
    private static readonly Pen ActiveCurvePen = new(new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x00)), 2.2);
    private static readonly Pen BypassedCurvePen = new(new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60)), 1.8);
    private static readonly Brush KneePointBrush = new SolidColorBrush(Color.FromRgb(0x00, 0xD4, 0xFF));
    private static readonly Brush SignalBallBrush = new SolidColorBrush(Color.FromRgb(0x00, 0xE6, 0x76));
    private static readonly Brush LabelBrush = new SolidColorBrush(Color.FromRgb(0x6E, 0x6E, 0x6E));
    private static readonly Typeface UiTypeface = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    static CompressorCurveControl()
    {
        BgBrush.Freeze();
        BorderPen.Freeze();
        GridPen.Freeze();
        UnityDiagPen.Freeze();
        ThresholdLinePen.Freeze();
        ActiveCurvePen.Freeze();
        BypassedCurvePen.Freeze();
        KneePointBrush.Freeze();
        SignalBallBrush.Freeze();
        LabelBrush.Freeze();
    }

    public CompressorCurveControl()
    {
        ClipToBounds = true;
        Cursor = Cursors.Hand;
    }

    private static void OnTrackChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ctrl = (CompressorCurveControl)d;
        if (e.OldValue is TrackChannelModel oldTrack)
        {
            oldTrack.PropertyChanged -= ctrl.OnModelChanged;
            oldTrack.Compressor.PropertyChanged -= ctrl.OnModelChanged;
        }
        if (e.NewValue is TrackChannelModel newTrack)
        {
            newTrack.PropertyChanged += ctrl.OnModelChanged;
            newTrack.Compressor.PropertyChanged += ctrl.OnModelChanged;
        }
        ctrl.InvalidateVisual();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TrackChannelModel.PeakHoldLinear))
            return;
        Dispatcher.InvokeAsync(InvalidateVisual);
    }

    private static double DbToX(double db, double w)
    {
        double padL = 22.0;
        double padR = 8.0;
        double norm = (Math.Clamp(db, MinDb, MaxDb) - MinDb) / (MaxDb - MinDb);
        return padL + norm * Math.Max(10.0, w - padL - padR);
    }

    private static double DbToY(double db, double h)
    {
        double padT = 8.0;
        double padB = 18.0;
        double norm = (Math.Clamp(db, MinDb, MaxDb) - MinDb) / (MaxDb - MinDb);
        return padT + (1.0 - norm) * Math.Max(10.0, h - padT - padB);
    }

    private static double XToDb(double x, double w)
    {
        double padL = 22.0;
        double padR = 8.0;
        double norm = Math.Clamp((x - padL) / Math.Max(10.0, w - padL - padR), 0.0, 1.0);
        return MinDb + norm * (MaxDb - MinDb);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Track == null) return;
        _isDraggingThreshold = true;
        UpdateThresholdFromMouse(e.GetPosition(this));
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_isDraggingThreshold && Track != null)
        {
            UpdateThresholdFromMouse(e.GetPosition(this));
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_isDraggingThreshold)
        {
            _isDraggingThreshold = false;
            ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    private void UpdateThresholdFromMouse(Point pt)
    {
        if (Track == null) return;
        double db = XToDb(pt.X, ActualWidth);
        Track.Compressor.ThresholdDb = Math.Clamp(db, -32.0, 0.0);
    }

    private static double EvaluateCompOutDb(double inDb, CompressorModel comp)
    {
        if (!comp.IsEnabled)
            return inDb;

        double thr = comp.ThresholdDb;
        double ratio = comp.RatioValue;
        double outDb = inDb <= thr
            ? inDb
            : thr + (inDb - thr) / ratio;
        return Math.Clamp(outDb + comp.MakeupGainDb * 0.5, MinDb, MaxDb);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        double h = ActualHeight;
        if (w < 40 || h < 40) return;

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        dc.DrawRoundedRectangle(BgBrush, BorderPen, new Rect(0, 0, w, h), 3.0, 3.0);

        // Grid lines at -36, -24, -12, 0 dB
        ReadOnlySpan<double> steps = stackalloc double[] { -36, -24, -12, 0 };
        foreach (double s in steps)
        {
            double x = DbToX(s, w);
            double y = DbToY(s, h);
            dc.DrawLine(GridPen, new Point(x, 6), new Point(x, h - 16));
            dc.DrawLine(GridPen, new Point(20, y), new Point(w - 6, y));

            var ft = new FormattedText($"{s:0}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, UiTypeface, 8.0, LabelBrush, dpi);
            dc.DrawText(ft, new Point(2, y - ft.Height * 0.5));
        }

        // 1:1 Unity line
        dc.DrawLine(UnityDiagPen, new Point(DbToX(MinDb, w), DbToY(MinDb, h)), new Point(DbToX(MaxDb, w), DbToY(MaxDb, h)));

        if (Track == null) return;
        var comp = Track.Compressor;

        // Threshold vertical dashed line
        double thrX = DbToX(comp.ThresholdDb, w);
        dc.DrawLine(ThresholdLinePen, new Point(thrX, 6), new Point(thrX, h - 16));

        // Transfer curve polyline
        StreamGeometry curveGeom = new();
        using (var ctx = curveGeom.Open())
        {
            Point pStart = new(DbToX(MinDb, w), DbToY(EvaluateCompOutDb(MinDb, comp), h));
            Point pKnee = new(DbToX(comp.ThresholdDb, w), DbToY(EvaluateCompOutDb(comp.ThresholdDb, comp), h));
            Point pEnd = new(DbToX(MaxDb, w), DbToY(EvaluateCompOutDb(MaxDb, comp), h));

            ctx.BeginFigure(pStart, false, false);
            ctx.LineTo(pKnee, true, true);
            ctx.LineTo(pEnd, true, true);
        }
        curveGeom.Freeze();

        dc.DrawGeometry(null, comp.IsEnabled ? ActiveCurvePen : BypassedCurvePen, curveGeom);

        // Knee handle dot
        Point kneePt = new(DbToX(comp.ThresholdDb, w), DbToY(EvaluateCompOutDb(comp.ThresholdDb, comp), h));
        dc.DrawEllipse(KneePointBrush, new Pen(Brushes.White, 1.0), kneePt, 4.5, 4.5);

        // Live animated signal ball on the curve
        if (Track.PeakLevelLinear > 0.001)
        {
            double inDb = Math.Clamp(20.0 * Math.Log10(Track.PeakLevelLinear), MinDb, MaxDb);
            double outDb = EvaluateCompOutDb(inDb, comp);
            Point ballPt = new(DbToX(inDb, w), DbToY(outDb, h));
            dc.DrawEllipse(SignalBallBrush, new Pen(Brushes.White, 1.0), ballPt, 4.0, 4.0);
        }
    }
}
