using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using US16x08_AbletonPanel.Models;

namespace US16x08_AbletonPanel.Controls;

public sealed class EqCurveControl : FrameworkElement
{
    public static readonly DependencyProperty TrackProperty =
        DependencyProperty.Register(
            nameof(Track),
            typeof(TrackChannelModel),
            typeof(EqCurveControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnTrackChanged));

    public TrackChannelModel? Track
    {
        get => (TrackChannelModel?)GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    private const double MinDisplayFreq = 20.0;
    private const double MaxDisplayFreq = 20000.0;
    private const double MaxDisplayDb = 15.0;

    private int _draggingBandIndex = -1; // 1..4
    private int _hoverBandIndex = -1;

    private static readonly Brush BgBrush = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14));
    private static readonly Pen BorderPen = new(new SolidColorBrush(Color.FromRgb(0x36, 0x36, 0x36)), 1.0);
    private static readonly Pen GridPen = new(new SolidColorBrush(Color.FromRgb(0x23, 0x23, 0x23)), 1.0);
    private static readonly Pen ZeroAxisPen = new(new SolidColorBrush(Color.FromRgb(0x3E, 0x3E, 0x3E)), 1.2);
    private static readonly Brush LabelBrush = new SolidColorBrush(Color.FromRgb(0x6E, 0x6E, 0x6E));
    private static readonly Typeface UiTypeface = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    // Signature Ableton EQ Eight band colors
    private static readonly Color[] BandColors =
    {
        Color.FromRgb(0xFF, 0x52, 0x52), // Band 1: Low (Red-Coral)
        Color.FromRgb(0xFF, 0xB0, 0x00), // Band 2: Low-Mid (Ableton Amber)
        Color.FromRgb(0x00, 0xE6, 0x76), // Band 3: High-Mid (Emerald Green)
        Color.FromRgb(0x00, 0xD4, 0xFF)  // Band 4: High (Ableton Cyan)
    };

    private static readonly Pen CompositePen = new(new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x00)), 2.2);
    private static readonly Brush CompositeFillBrush = new SolidColorBrush(Color.FromArgb(46, 0xFF, 0xB0, 0x00));
    private static readonly Pen DisabledCompositePen = new(new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)), 1.8);

    static EqCurveControl()
    {
        BgBrush.Freeze();
        BorderPen.Freeze();
        GridPen.Freeze();
        ZeroAxisPen.Freeze();
        LabelBrush.Freeze();
        CompositePen.Freeze();
        CompositeFillBrush.Freeze();
        DisabledCompositePen.Freeze();
    }

    public EqCurveControl()
    {
        ClipToBounds = true;
        Cursor = Cursors.Cross;
    }

    private static void OnTrackChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ctrl = (EqCurveControl)d;
        if (e.OldValue is TrackChannelModel oldTrack)
            ctrl.Unsubscribe(oldTrack);
        if (e.NewValue is TrackChannelModel newTrack)
            ctrl.Subscribe(newTrack);
        ctrl.InvalidateVisual();
    }

    private void Subscribe(TrackChannelModel track)
    {
        track.PropertyChanged += OnModelPropertyChanged;
        track.LowBand.PropertyChanged += OnModelPropertyChanged;
        track.LowMidBand.PropertyChanged += OnModelPropertyChanged;
        track.HighMidBand.PropertyChanged += OnModelPropertyChanged;
        track.HighBand.PropertyChanged += OnModelPropertyChanged;
    }

    private void Unsubscribe(TrackChannelModel track)
    {
        track.PropertyChanged -= OnModelPropertyChanged;
        track.LowBand.PropertyChanged -= OnModelPropertyChanged;
        track.LowMidBand.PropertyChanged -= OnModelPropertyChanged;
        track.HighMidBand.PropertyChanged -= OnModelPropertyChanged;
        track.HighBand.PropertyChanged -= OnModelPropertyChanged;
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TrackChannelModel.PeakLevelLinear) or nameof(TrackChannelModel.PeakHoldLinear))
            return;
        Dispatcher.InvokeAsync(InvalidateVisual);
    }

    private static double FreqToX(double freq, double width)
    {
        double logMin = Math.Log10(MinDisplayFreq);
        double logMax = Math.Log10(MaxDisplayFreq);
        double norm = (Math.Log10(Math.Clamp(freq, MinDisplayFreq, MaxDisplayFreq)) - logMin) / (logMax - logMin);
        return 28.0 + norm * Math.Max(10.0, width - 38.0);
    }

    private static double XToFreq(double x, double width)
    {
        double norm = Math.Clamp((x - 28.0) / Math.Max(10.0, width - 38.0), 0.0, 1.0);
        double logMin = Math.Log10(MinDisplayFreq);
        double logMax = Math.Log10(MaxDisplayFreq);
        return Math.Pow(10.0, logMin + norm * (logMax - logMin));
    }

    private static double DbToY(double db, double height)
    {
        double topPad = 12.0;
        double botPad = 20.0;
        double plotH = Math.Max(10.0, height - topPad - botPad);
        double norm = (Math.Clamp(db, -MaxDisplayDb, MaxDisplayDb) + MaxDisplayDb) / (2.0 * MaxDisplayDb);
        return topPad + (1.0 - norm) * plotH;
    }

    private static double YToDb(double y, double height)
    {
        double topPad = 12.0;
        double botPad = 20.0;
        double plotH = Math.Max(10.0, height - topPad - botPad);
        double norm = 1.0 - Math.Clamp((y - topPad) / plotH, 0.0, 1.0);
        return -MaxDisplayDb + norm * (2.0 * MaxDisplayDb);
    }

    private EqBandModel? GetBand(int indexOneBased) => Track switch
    {
        null => null,
        _ => indexOneBased switch
        {
            1 => Track.LowBand,
            2 => Track.LowMidBand,
            3 => Track.HighMidBand,
            4 => Track.HighBand,
            _ => null
        }
    };

    private int HitTestNode(Point pt)
    {
        if (Track == null) return -1;
        double w = ActualWidth;
        double h = ActualHeight;

        for (int b = 1; b <= 4; b++)
        {
            var band = GetBand(b)!;
            Point nodePt = new(FreqToX(band.FrequencyHz, w), DbToY(band.GainDb, h));
            if ((pt - nodePt).Length <= 13.0)
                return b;
        }
        return -1;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Track == null) return;

        Point pt = e.GetPosition(this);
        int hit = HitTestNode(pt);
        if (hit > 0)
        {
            var band = GetBand(hit)!;
            if (e.ClickCount == 2)
            {
                band.GainDb = 0.0;
                InvalidateVisual();
                e.Handled = true;
                return;
            }

            _draggingBandIndex = hit;
            CaptureMouse();
            InvalidateVisual();
            e.Handled = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (Track == null) return;

        Point pt = e.GetPosition(this);
        if (_draggingBandIndex > 0)
        {
            var band = GetBand(_draggingBandIndex)!;
            double rawFreq = XToFreq(pt.X, ActualWidth);
            int nearestIdx = TascamDspTables.FindNearestFrequencyIndex(rawFreq, band.FrequencyTable);
            band.FrequencyHz = band.FrequencyTable[nearestIdx];
            band.GainDb = Math.Clamp(YToDb(pt.Y, ActualHeight), -12.0, 12.0);
            InvalidateVisual();
            return;
        }

        int hover = HitTestNode(pt);
        if (hover != _hoverBandIndex)
        {
            _hoverBandIndex = hover;
            Cursor = hover > 0 ? Cursors.Hand : Cursors.Cross;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_draggingBandIndex > 0)
        {
            _draggingBandIndex = -1;
            ReleaseMouseCapture();
            InvalidateVisual();
            e.Handled = true;
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (Track == null) return;

        int targetBand = _draggingBandIndex > 0 ? _draggingBandIndex : _hoverBandIndex;
        if (targetBand is 2 or 3)
        {
            var band = GetBand(targetBand)!;
            int qIdx = band.QCode;
            int nextIdx = Math.Clamp(qIdx + (e.Delta > 0 ? 1 : -1), 0, TascamDspTables.QValues.Length - 1);
            band.QFactor = TascamDspTables.QValues[nextIdx];
            InvalidateVisual();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Computes the magnitude response in dB at frequency f for the US-16x08 4-Band EQ + 100 Hz Low Cut filter
    /// </summary>
    private static double EvaluateBandDb(double f, EqBandModel band)
    {
        if (Math.Abs(band.GainDb) < 0.05) return 0.0;
        double f0 = Math.Max(20.0, band.FrequencyHz);
        double logDist = Math.Log(f / f0, 2.0); // Octaves from center frequency

        if (band.BandNumber == 1)
        {
            // Low Shelf filter response
            double s = 1.0 / (1.0 + Math.Exp(logDist * 3.2));
            return band.GainDb * s;
        }
        if (band.BandNumber == 4)
        {
            // High Shelf filter response
            double s = 1.0 / (1.0 + Math.Exp(-logDist * 3.2));
            return band.GainDb * s;
        }

        // Parametric Bell response for Low-Mid & High-Mid
        double q = TascamDspTables.QValues[Math.Clamp((int)band.QCode, 0, TascamDspTables.QValues.Length - 1)];
        double bwOct = 1.0 / Math.Max(0.25, q * 0.85);
        double shape = 1.0 / (1.0 + Math.Pow(logDist / (bwOct * 0.5), 2.0));
        return band.GainDb * shape;
    }

    private static double EvaluateLowCutDb(double f, bool enabled)
    {
        if (!enabled) return 0.0;
        // 100 Hz 12dB/octave Butterworth High-Pass Filter
        double ratio = 100.0 / Math.Max(10.0, f);
        double magSq = 1.0 / (1.0 + Math.Pow(ratio, 4.0));
        return 10.0 * Math.Log10(Math.Max(1e-6, magSq));
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        double h = ActualHeight;
        if (w < 60 || h < 40) return;

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // 1. Background frame
        dc.DrawRoundedRectangle(BgBrush, BorderPen, new Rect(0, 0, w, h), 3.0, 3.0);

        // 2. Logarithmic Frequency Grid Lines
        ReadOnlySpan<double> gridFreqs = stackalloc double[] { 50, 100, 200, 500, 1000, 2000, 5000, 10000 };
        foreach (double gf in gridFreqs)
        {
            double x = FreqToX(gf, w);
            dc.DrawLine(GridPen, new Point(x, 4), new Point(x, h - 18));

            string label = gf >= 1000 ? $"{gf / 1000:0}k" : $"{gf:0}";
            var ft = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, UiTypeface, 9.0, LabelBrush, dpi);
            dc.DrawText(ft, new Point(x - ft.Width * 0.5, h - 15));
        }

        // 3. Horizontal dB Grid Lines (+12, +6, 0, -6, -12)
        ReadOnlySpan<double> gridDbs = stackalloc double[] { 12, 6, 0, -6, -12 };
        foreach (double gdb in gridDbs)
        {
            double y = DbToY(gdb, h);
            Pen p = Math.Abs(gdb) < 0.01 ? ZeroAxisPen : GridPen;
            dc.DrawLine(p, new Point(26, y), new Point(w - 8, y));

            string dbText = gdb > 0 ? $"+{gdb:0}" : $"{gdb:0}";
            var ft = new FormattedText(dbText, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, UiTypeface, 8.5, LabelBrush, dpi);
            dc.DrawText(ft, new Point(4, y - ft.Height * 0.5));
        }

        if (Track == null) return;

        double yZero = DbToY(0, h);
        int numSamples = Math.Max(80, (int)(w - 36));
        StreamGeometry compositeGeom = new();
        StreamGeometry fillGeom = new();

        using (var cCtx = compositeGeom.Open())
        using (var fCtx = fillGeom.Open())
        {
            for (int i = 0; i <= numSamples; i++)
            {
                double x = 28.0 + (i / (double)numSamples) * (w - 38.0);
                double freq = XToFreq(x, w);

                double totalDb = EvaluateLowCutDb(freq, Track.IsLowCutEnabled);
                if (Track.IsEqEnabled)
                {
                    totalDb += EvaluateBandDb(freq, Track.LowBand)
                             + EvaluateBandDb(freq, Track.LowMidBand)
                             + EvaluateBandDb(freq, Track.HighMidBand)
                             + EvaluateBandDb(freq, Track.HighBand);
                }

                double y = DbToY(totalDb, h);
                Point pt = new(x, y);

                if (i == 0)
                {
                    cCtx.BeginFigure(pt, false, false);
                    fCtx.BeginFigure(new Point(x, yZero), true, true);
                    fCtx.LineTo(pt, false, false);
                }
                else
                {
                    cCtx.LineTo(pt, true, true);
                    fCtx.LineTo(pt, false, false);
                }

                if (i == numSamples)
                {
                    fCtx.LineTo(new Point(x, yZero), false, false);
                }
            }
        }

        compositeGeom.Freeze();
        fillGeom.Freeze();

        if (Track.IsEqEnabled)
        {
            dc.DrawGeometry(CompositeFillBrush, null, fillGeom);
            dc.DrawGeometry(null, CompositePen, compositeGeom);
        }
        else
        {
            dc.DrawGeometry(null, DisabledCompositePen, compositeGeom);
        }

        // 4. Draw the 4 interactive Ableton EQ Eight numbered nodes
        for (int b = 1; b <= 4; b++)
        {
            var band = GetBand(b)!;
            Color col = BandColors[b - 1];
            Point nPt = new(FreqToX(band.FrequencyHz, w), DbToY(band.GainDb, h));
            bool active = b == _draggingBandIndex || b == _hoverBandIndex;
            double radius = active ? 10.5 : 8.5;

            Brush nodeFill = new SolidColorBrush(Track.IsEqEnabled ? col : Color.FromRgb(0x50, 0x50, 0x50));
            Pen nodeRing = new(Brushes.White, active ? 2.0 : 1.0);

            if (active)
            {
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(65, col.R, col.G, col.B)), null, nPt, 15.0, 15.0);
            }

            dc.DrawEllipse(nodeFill, nodeRing, nPt, radius, radius);

            var numFt = new FormattedText(
                b.ToString(CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                UiTypeface,
                9.5,
                Brushes.Black,
                dpi);
            dc.DrawText(numFt, new Point(nPt.X - numFt.Width * 0.5, nPt.Y - numFt.Height * 0.5));
        }

        // 5. Tooltip badge when dragging/hovering a band node
        int infoBand = _draggingBandIndex > 0 ? _draggingBandIndex : _hoverBandIndex;
        if (infoBand > 0)
        {
            var band = GetBand(infoBand)!;
            string infoText = band.HasQ
                ? $"BAND {infoBand}: {band.FrequencyDisplay}  |  {band.GainDisplay}  |  Q: {band.QDisplay} (Wheel)"
                : $"BAND {infoBand}: {band.FrequencyDisplay}  |  {band.GainDisplay} (Shelf)";
            var infoFt = new FormattedText(infoText, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, UiTypeface, 10.0, Brushes.White, dpi);
            Rect pill = new(34, 8, infoFt.Width + 12, infoFt.Height + 4);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(210, 0x22, 0x22, 0x22)), BorderPen, pill, 3, 3);
            dc.DrawText(infoFt, new Point(40, 10));
        }
    }
}
