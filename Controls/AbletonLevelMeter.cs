using System;
using System.Windows;
using System.Windows.Media;

namespace US16x08_AbletonPanel.Controls;

public sealed class AbletonLevelMeter : FrameworkElement
{
    public static readonly DependencyProperty LevelProperty =
        DependencyProperty.Register(
            nameof(Level),
            typeof(double),
            typeof(AbletonLevelMeter),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PeakHoldProperty =
        DependencyProperty.Register(
            nameof(PeakHold),
            typeof(double),
            typeof(AbletonLevelMeter),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsGainReductionProperty =
        DependencyProperty.Register(
            nameof(IsGainReduction),
            typeof(bool),
            typeof(AbletonLevelMeter),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// For normal meter: 0.0 to 1.0 linear peak.
    /// For Gain Reduction meter: 0.0 to 24.0 dB reduction.
    /// </summary>
    public double Level
    {
        get => (double)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public double PeakHold
    {
        get => (double)GetValue(PeakHoldProperty);
        set => SetValue(PeakHoldProperty, value);
    }

    public bool IsGainReduction
    {
        get => (bool)GetValue(IsGainReductionProperty);
        set => SetValue(IsGainReductionProperty, value);
    }

    private static readonly Brush BgBrush = new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x11));
    private static readonly Pen BorderPen = new(new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D)), 1.0);
    private static readonly Brush GrBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x91, 0x00));
    private static readonly Brush ClipBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30));
    private static readonly Pen PeakHoldPen = new(new SolidColorBrush(Color.FromRgb(0xFF, 0xE0, 0x66)), 1.5);
    private static readonly LinearGradientBrush MeterGradient;

    static AbletonLevelMeter()
    {
        BgBrush.Freeze();
        BorderPen.Freeze();
        GrBrush.Freeze();
        ClipBrush.Freeze();
        PeakHoldPen.Freeze();

        MeterGradient = new LinearGradientBrush
        {
            StartPoint = new Point(0, 1),
            EndPoint = new Point(0, 0)
        };
        MeterGradient.GradientStops.Add(new GradientStop(Color.FromRgb(0x00, 0xC8, 0x53), 0.0));
        MeterGradient.GradientStops.Add(new GradientStop(Color.FromRgb(0x00, 0xE6, 0x76), 0.65));
        MeterGradient.GradientStops.Add(new GradientStop(Color.FromRgb(0xFF, 0xD6, 0x00), 0.85));
        MeterGradient.GradientStops.Add(new GradientStop(Color.FromRgb(0xFF, 0x3B, 0x30), 1.0));
        MeterGradient.Freeze();

        // Default size as metadata, not as local values in the constructor: a local value outranks sizes set
        // inside a DataTemplate, which pinned the channel-strip meters at 148 px while the master meters stretched.
        WidthProperty.OverrideMetadata(typeof(AbletonLevelMeter), new FrameworkPropertyMetadata(8.0));
        HeightProperty.OverrideMetadata(typeof(AbletonLevelMeter), new FrameworkPropertyMetadata(148.0));
    }

    private static double LinearToMeterHeightNormalized(double linear)
    {
        if (linear <= 0.0003) return 0.0;
        double db = 20.0 * Math.Log10(linear); // -60 dB to 0 dB
        return Math.Clamp((db + 60.0) / 60.0, 0.0, 1.0);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        double h = ActualHeight;
        if (w < 2 || h < 8) return;

        Rect fullRect = new(0, 0, w, h);
        dc.DrawRoundedRectangle(BgBrush, BorderPen, fullRect, 1.5, 1.5);

        double innerW = Math.Max(1.0, w - 2.0);
        double innerH = Math.Max(1.0, h - 2.0);

        if (IsGainReduction)
        {
            // Top-down orange bar (0 to 24 dB GR)
            double normGr = Math.Clamp(Level / 24.0, 0.0, 1.0);
            double barH = normGr * innerH;
            if (barH > 0.5)
            {
                dc.DrawRectangle(GrBrush, null, new Rect(1.0, 1.0, innerW, barH));
            }
            return;
        }

        // Standard bottom-up Ableton audio meter
        double normLevel = LinearToMeterHeightNormalized(Level);
        double fillH = normLevel * innerH;
        if (fillH > 0.5)
        {
            Rect barRect = new(1.0, 1.0 + (innerH - fillH), innerW, fillH);
            dc.PushClip(new RectangleGeometry(barRect));
            dc.DrawRectangle(MeterGradient, null, new Rect(1.0, 1.0, innerW, innerH));
            dc.Pop();
        }

        // Peak hold horizontal line
        double normPeak = LinearToMeterHeightNormalized(PeakHold);
        if (normPeak > 0.02)
        {
            double py = 1.0 + (1.0 - normPeak) * innerH;
            dc.DrawLine(PeakHoldPen, new Point(1.0, py), new Point(w - 1.0, py));
        }

        // Top 3px red clip LED when peak >= 0.98
        if (PeakHold >= 0.98)
        {
            dc.DrawRectangle(ClipBrush, null, new Rect(1.0, 1.0, innerW, 4.0));
        }
    }
}
