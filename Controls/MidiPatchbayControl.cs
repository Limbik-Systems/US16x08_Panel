using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using US16x08_AbletonPanel.Models;

namespace US16x08_AbletonPanel.Controls;

public sealed class MidiPatchbayControl : FrameworkElement
{
    public static readonly DependencyProperty InputPortsProperty =
        DependencyProperty.Register(
            nameof(InputPorts),
            typeof(ObservableCollection<MidiPortModel>),
            typeof(MidiPatchbayControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnPortsChanged));

    public static readonly DependencyProperty OutputPortsProperty =
        DependencyProperty.Register(
            nameof(OutputPorts),
            typeof(ObservableCollection<MidiPortModel>),
            typeof(MidiPatchbayControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnPortsChanged));

    public static readonly DependencyProperty RoutesProperty =
        DependencyProperty.Register(
            nameof(Routes),
            typeof(ObservableCollection<MidiRouteModel>),
            typeof(MidiPatchbayControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnRoutesChanged));

    public static readonly DependencyProperty SelectedRouteProperty =
        DependencyProperty.Register(
            nameof(SelectedRoute),
            typeof(MidiRouteModel),
            typeof(MidiPatchbayControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public ObservableCollection<MidiPortModel>? InputPorts
    {
        get => (ObservableCollection<MidiPortModel>?)GetValue(InputPortsProperty);
        set => SetValue(InputPortsProperty, value);
    }

    public ObservableCollection<MidiPortModel>? OutputPorts
    {
        get => (ObservableCollection<MidiPortModel>?)GetValue(OutputPortsProperty);
        set => SetValue(OutputPortsProperty, value);
    }

    public ObservableCollection<MidiRouteModel>? Routes
    {
        get => (ObservableCollection<MidiRouteModel>?)GetValue(RoutesProperty);
        set => SetValue(RoutesProperty, value);
    }

    public MidiRouteModel? SelectedRoute
    {
        get => (MidiRouteModel?)GetValue(SelectedRouteProperty);
        set => SetValue(SelectedRouteProperty, value);
    }

    public Action<string, string, bool>? RouteToggleRequested { get; set; }
    public Action<string>? PortPanicRequested { get; set; }
    public Action<string>? PortAuditionRequested { get; set; }
    public Action<string, int>? PortChannelCycleRequested { get; set; }
    public Action<string, bool>? PortDisconnectAllRequested { get; set; }

    private Point[] _inJackCenters = Array.Empty<Point>();
    private Rect[] _inRowRects = Array.Empty<Rect>();
    private Point[] _outJackCenters = Array.Empty<Point>();
    private Rect[] _outRowRects = Array.Empty<Rect>();
    private Rect[] _outPanicRects = Array.Empty<Rect>();
    private Rect[] _outTestRects = Array.Empty<Rect>();
    private Rect[] _outChanRects = Array.Empty<Rect>();
    private Rect[,] _matrixCellRects = new Rect[0, 0];
    private readonly List<(Rect HitBox, MidiRouteModel Route)> _cableBadgeHits = new();

    // Drag-to-patch state
    private int _dragFromInIdx = -1;
    private int _dragFromOutIdx = -1;
    private Point _dragMousePos;

    // Hover state
    private int _hoverInIdx = -1;
    private int _hoverOutIdx = -1;
    private int _hoverMatIn = -1;
    private int _hoverMatOut = -1;
    private int _hoverPanicIdx = -1;
    private int _hoverTestIdx = -1;
    private int _hoverChanIdx = -1;

    private static readonly Color[] CablePalette =
    {
        Color.FromRgb(0x00, 0xD4, 0xFF), // Cyan (US-16x08 DIN)
        Color.FromRgb(0xFF, 0xB0, 0x00), // Amber (Launchkey MK4)
        Color.FromRgb(0x00, 0xE6, 0x76), // Emerald
        Color.FromRgb(0xFF, 0x52, 0x52), // Coral Red
        Color.FromRgb(0xA7, 0x70, 0xFF), // Purple
        Color.FromRgb(0x26, 0xC6, 0xDA), // Teal
        Color.FromRgb(0xFF, 0x6E, 0xC7), // Pink
        Color.FromRgb(0xFF, 0xD5, 0x4F), // Gold
        Color.FromRgb(0x69, 0xF0, 0xAE), // Mint
        Color.FromRgb(0x40, 0xC4, 0xFF), // Sky Blue
        Color.FromRgb(0xFF, 0x8A, 0x65), // Warm Orange
        Color.FromRgb(0xCE, 0x93, 0xD8)  // Lavender
    };

    private static readonly Brush BayCardBgBrush = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
    private static readonly Pen CardBorderPen = new(new SolidColorBrush(Color.FromRgb(0x36, 0x36, 0x36)), 1.0);
    private static readonly Brush RowNormalBrush = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x26));
    private static readonly Pen RowBorderPen = new(new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)), 1.0);
    private static readonly Brush JackSocketBrush = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x10));
    private static readonly Pen JackRingPen = new(new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x8E)), 1.8);
    private static readonly Typeface UiBold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private static readonly Typeface UiBlack = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Black, FontStretches.Normal);

    private static readonly Dictionary<Color, SolidColorBrush> BrushCache = new();
    private static readonly Dictionary<(string Text, int SizeTenths, Color Col, bool Bold, int DpiPct), FormattedText> TextCache = new();

    static MidiPatchbayControl()
    {
        BayCardBgBrush.Freeze();
        CardBorderPen.Freeze();
        RowNormalBrush.Freeze();
        RowBorderPen.Freeze();
        JackSocketBrush.Freeze();
        JackRingPen.Freeze();
    }

    private static SolidColorBrush GetCachedBrush(Color c)
    {
        if (!BrushCache.TryGetValue(c, out var b))
        {
            b = new SolidColorBrush(c);
            b.Freeze();
            BrushCache[c] = b;
        }
        return b;
    }

    public MidiPatchbayControl()
    {
        ClipToBounds = true;
        Cursor = Cursors.Hand;
    }

    private static void OnPortsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ctrl = (MidiPatchbayControl)d;
        if (e.OldValue is INotifyCollectionChanged oldCol)
            oldCol.CollectionChanged -= ctrl.OnCollectionChanged;
        if (e.NewValue is INotifyCollectionChanged newCol)
            newCol.CollectionChanged += ctrl.OnCollectionChanged;
        ctrl.InvalidateVisual();
    }

    private static void OnRoutesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ctrl = (MidiPatchbayControl)d;
        if (e.OldValue is ObservableCollection<MidiRouteModel> oldCol)
        {
            oldCol.CollectionChanged -= ctrl.OnRoutesCollectionChanged;
            foreach (var r in oldCol) r.PropertyChanged -= ctrl.OnRoutePropertyChanged;
        }
        if (e.NewValue is ObservableCollection<MidiRouteModel> newCol)
        {
            newCol.CollectionChanged += ctrl.OnRoutesCollectionChanged;
            foreach (var r in newCol) r.PropertyChanged += ctrl.OnRoutePropertyChanged;
        }
        ctrl.InvalidateVisual();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.InvokeAsync(InvalidateVisual);
    }

    private void OnRoutesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
        {
            foreach (MidiRouteModel r in e.OldItems)
                r.PropertyChanged -= OnRoutePropertyChanged;
        }
        if (e.NewItems != null)
        {
            foreach (MidiRouteModel r in e.NewItems)
                r.PropertyChanged += OnRoutePropertyChanged;
        }
        Dispatcher.InvokeAsync(InvalidateVisual);
    }

    private void OnRoutePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MidiRouteModel.RoutedCount))
            return;
        InvalidateVisual();
    }

    private MidiRouteModel? FindRoute(string inName, string outName)
    {
        if (Routes == null) return null;
        for (int i = 0; i < Routes.Count; i++)
        {
            var r = Routes[i];
            if (string.Equals(r.SourcePortName, inName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(r.DestPortName, outName, StringComparison.OrdinalIgnoreCase))
            {
                return r;
            }
        }
        return null;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (InputPorts == null || OutputPorts == null) return;

        Point pt = e.GetPosition(this);
        _dragMousePos = pt;

        int inCount = InputPorts.Count;
        int outCount = OutputPorts.Count;

        // 1. Check per-output PANIC, TEST, or CHANNEL badge buttons
        for (int d = 0; d < Math.Min(outCount, _outPanicRects.Length); d++)
        {
            if (_outPanicRects[d].Contains(pt))
            {
                PortPanicRequested?.Invoke(OutputPorts[d].Name);
                e.Handled = true;
                return;
            }
            if (_outTestRects[d].Contains(pt))
            {
                PortAuditionRequested?.Invoke(OutputPorts[d].Name);
                e.Handled = true;
                return;
            }
            if (d < _outChanRects.Length && _outChanRects[d].Contains(pt))
            {
                PortChannelCycleRequested?.Invoke(OutputPorts[d].Name, 1);
                InvalidateVisual();
                e.Handled = true;
                return;
            }
        }

        // 2. Check if user clicked a Cable Midpoint Badge to select that route for editing (or double-click to disconnect)
        for (int i = _cableBadgeHits.Count - 1; i >= 0; i--)
        {
            if (_cableBadgeHits[i].HitBox.Contains(pt))
            {
                var r = _cableBadgeHits[i].Route;
                if (e.ClickCount >= 2)
                {
                    RouteToggleRequested?.Invoke(r.SourcePortName, r.DestPortName, true);
                }
                else
                {
                    SelectedRoute = r;
                }
                InvalidateVisual();
                e.Handled = true;
                return;
            }
        }

        // 3. Check Crosspoint Matrix click (Toggles route ON/OFF and selects it)
        if (_matrixCellRects.GetLength(0) == inCount && _matrixCellRects.GetLength(1) == outCount)
        {
            for (int s = 0; s < inCount; s++)
            {
                for (int d = 0; d < outCount; d++)
                {
                    if (_matrixCellRects[s, d].Contains(pt))
                    {
                        RouteToggleRequested?.Invoke(InputPorts[s].Name, OutputPorts[d].Name, true);
                        InvalidateVisual();
                        e.Handled = true;
                        return;
                    }
                }
            }
        }

        // 4. Check Input Port row or jack to start dragging a virtual MIDI patch cable
        for (int s = 0; s < Math.Min(inCount, _inRowRects.Length); s++)
        {
            if ((pt - _inJackCenters[s]).Length <= 16.0 || _inRowRects[s].Contains(pt))
            {
                _dragFromInIdx = s;
                _dragFromOutIdx = -1;
                CaptureMouse();
                InvalidateVisual();
                e.Handled = true;
                return;
            }
        }

        // 5. Check Output Port row or jack to start dragging a virtual MIDI patch cable
        for (int d = 0; d < Math.Min(outCount, _outRowRects.Length); d++)
        {
            if ((pt - _outJackCenters[d]).Length <= 16.0 || _outRowRects[d].Contains(pt))
            {
                _dragFromOutIdx = d;
                _dragFromInIdx = -1;
                CaptureMouse();
                InvalidateVisual();
                e.Handled = true;
                return;
            }
        }
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        if (InputPorts == null || OutputPorts == null) return;

        Point pt = e.GetPosition(this);
        int inCount = InputPorts.Count;
        int outCount = OutputPorts.Count;

        // Right-clicking the per-output CH badge cycles channel backwards
        for (int d = 0; d < Math.Min(outCount, _outChanRects.Length); d++)
        {
            if (_outChanRects[d].Contains(pt))
            {
                PortChannelCycleRequested?.Invoke(OutputPorts[d].Name, -1);
                InvalidateVisual();
                e.Handled = true;
                return;
            }
        }

        // Right-clicking a matrix cell selects the route (if present)
        if (_matrixCellRects.GetLength(0) == inCount && _matrixCellRects.GetLength(1) == outCount)
        {
            for (int s = 0; s < inCount; s++)
            {
                for (int d = 0; d < outCount; d++)
                {
                    if (_matrixCellRects[s, d].Contains(pt))
                    {
                        var existing = FindRoute(InputPorts[s].Name, OutputPorts[d].Name);
                        if (existing != null)
                        {
                            SelectedRoute = existing;
                            InvalidateVisual();
                        }
                        e.Handled = true;
                        return;
                    }
                }
            }
        }

        // Right-clicking a cable badge removes that route
        for (int i = _cableBadgeHits.Count - 1; i >= 0; i--)
        {
            if (_cableBadgeHits[i].HitBox.Contains(pt))
            {
                var r = _cableBadgeHits[i].Route;
                RouteToggleRequested?.Invoke(r.SourcePortName, r.DestPortName, true);
                InvalidateVisual();
                e.Handled = true;
                return;
            }
        }

        // Right-clicking an Output jack unplugs all routes connected to that Output port
        for (int d = 0; d < Math.Min(outCount, _outJackCenters.Length); d++)
        {
            if ((pt - _outJackCenters[d]).Length <= 18.0)
            {
                PortDisconnectAllRequested?.Invoke(OutputPorts[d].Name, false);
                InvalidateVisual();
                e.Handled = true;
                return;
            }
        }

        // Right-clicking an Input jack unplugs all routes from that Input port
        for (int s = 0; s < Math.Min(inCount, _inJackCenters.Length); s++)
        {
            if ((pt - _inJackCenters[s]).Length <= 18.0)
            {
                PortDisconnectAllRequested?.Invoke(InputPorts[s].Name, true);
                InvalidateVisual();
                e.Handled = true;
                return;
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (InputPorts == null || OutputPorts == null) return;

        Point pt = e.GetPosition(this);
        _dragMousePos = pt;

        int inCount = InputPorts.Count;
        int outCount = OutputPorts.Count;

        int newHoverIn = -1;
        int newHoverOut = -1;
        int newMatIn = -1;
        int newMatOut = -1;
        int newPanicIdx = -1;
        int newTestIdx = -1;
        int newChanIdx = -1;

        for (int d = 0; d < Math.Min(outCount, _outPanicRects.Length); d++)
        {
            if (_outPanicRects[d].Contains(pt)) { newPanicIdx = d; break; }
            if (_outTestRects[d].Contains(pt)) { newTestIdx = d; break; }
            if (d < _outChanRects.Length && _outChanRects[d].Contains(pt)) { newChanIdx = d; break; }
        }

        for (int s = 0; s < Math.Min(inCount, _inRowRects.Length); s++)
        {
            if ((pt - _inJackCenters[s]).Length <= 16.0 || _inRowRects[s].Contains(pt))
            {
                newHoverIn = s;
                break;
            }
        }

        for (int d = 0; d < Math.Min(outCount, _outRowRects.Length); d++)
        {
            if ((pt - _outJackCenters[d]).Length <= 16.0 || _outRowRects[d].Contains(pt))
            {
                newHoverOut = d;
                break;
            }
        }

        if (_matrixCellRects.GetLength(0) == inCount && _matrixCellRects.GetLength(1) == outCount)
        {
            for (int s = 0; s < inCount; s++)
            {
                for (int d = 0; d < outCount; d++)
                {
                    if (_matrixCellRects[s, d].Contains(pt))
                    {
                        newMatIn = s;
                        newMatOut = d;
                        break;
                    }
                }
            }
        }

        if (_dragFromInIdx >= 0 || _dragFromOutIdx >= 0 ||
            newHoverIn != _hoverInIdx || newHoverOut != _hoverOutIdx ||
            newMatIn != _hoverMatIn || newMatOut != _hoverMatOut ||
            newPanicIdx != _hoverPanicIdx || newTestIdx != _hoverTestIdx || newChanIdx != _hoverChanIdx)
        {
            _hoverInIdx = newHoverIn;
            _hoverOutIdx = newHoverOut;
            _hoverMatIn = newMatIn;
            _hoverMatOut = newMatOut;
            _hoverPanicIdx = newPanicIdx;
            _hoverTestIdx = newTestIdx;
            _hoverChanIdx = newChanIdx;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (InputPorts == null || OutputPorts == null) return;

        Point pt = e.GetPosition(this);

        if (_dragFromInIdx >= 0)
        {
            if (_hoverOutIdx >= 0 && _hoverOutIdx < OutputPorts.Count && _dragFromInIdx < InputPorts.Count)
            {
                RouteToggleRequested?.Invoke(InputPorts[_dragFromInIdx].Name, OutputPorts[_hoverOutIdx].Name, true);
            }
            else if (_hoverInIdx < 0 && _dragFromInIdx < InputPorts.Count &&
                     (pt - _inJackCenters[_dragFromInIdx]).Length > 28.0 &&
                     !_inRowRects[_dragFromInIdx].Contains(pt))
            {
                // Dragging from an Input jack into open space unplugs all cables from that Input port
                PortDisconnectAllRequested?.Invoke(InputPorts[_dragFromInIdx].Name, true);
            }
            _dragFromInIdx = -1;
            ReleaseMouseCapture();
            InvalidateVisual();
            e.Handled = true;
        }
        else if (_dragFromOutIdx >= 0)
        {
            if (_hoverInIdx >= 0 && _hoverInIdx < InputPorts.Count && _dragFromOutIdx < OutputPorts.Count)
            {
                RouteToggleRequested?.Invoke(InputPorts[_hoverInIdx].Name, OutputPorts[_dragFromOutIdx].Name, true);
            }
            else if (_hoverOutIdx < 0 && _dragFromOutIdx < OutputPorts.Count &&
                     (pt - _outJackCenters[_dragFromOutIdx]).Length > 28.0 &&
                     !_outRowRects[_dragFromOutIdx].Contains(pt))
            {
                // Dragging from an Output jack into open space unplugs all cables connected to that Output port
                PortDisconnectAllRequested?.Invoke(OutputPorts[_dragFromOutIdx].Name, false);
            }
            _dragFromOutIdx = -1;
            ReleaseMouseCapture();
            InvalidateVisual();
            e.Handled = true;
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_dragFromInIdx < 0 && _dragFromOutIdx < 0)
        {
            _hoverInIdx = -1;
            _hoverOutIdx = -1;
            _hoverMatIn = -1;
            _hoverMatOut = -1;
            _hoverPanicIdx = -1;
            _hoverTestIdx = -1;
            _hoverChanIdx = -1;
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        double h = ActualHeight;
        if (w < 300 || h < 180) return;

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        double gap = 12.0;
        double patchbayW = Math.Floor((w - gap) * 0.54);
        double matrixW = w - gap - patchbayW;
        double matrixX = patchbayW + gap;

        RenderVisualMidiPatchbay(dc, new Rect(0, 0, patchbayW, h), dpi);
        RenderMidiCrosspointMatrix(dc, new Rect(matrixX, 0, matrixW, h), dpi);
    }

    private void RenderVisualMidiPatchbay(DrawingContext dc, Rect bounds, double dpi)
    {
        dc.DrawRoundedRectangle(BayCardBgBrush, CardBorderPen, bounds, 3, 3);

        Rect hdrRect = new(bounds.Left, bounds.Top, bounds.Width, 30);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B)), CardBorderPen, hdrRect, 3, 3);
        DrawText(dc, "VISUAL USB & 5-PIN DIN MIDI PATCHBAY // DRAG JACK-TO-JACK", bounds.Left + 12, bounds.Top + 7, 10.5, Color.FromRgb(0xFF, 0xB0, 0x00), dpi, true);
        DrawText(dc, "CLICK CABLE BADGE TO EDIT FILTER/REMAP", bounds.Right - 225, bounds.Top + 8, 9.0, Color.FromRgb(0x9A, 0x9A, 0x9A), dpi);

        var inputs = InputPorts;
        var outputs = OutputPorts;
        if (inputs == null || outputs == null || (inputs.Count == 0 && outputs.Count == 0))
        {
            DrawText(dc, "No Windows MIDI devices detected. Click RESCAN MIDI PORTS.", bounds.Left + 24, bounds.Top + 60, 12, Colors.Gray, dpi);
            return;
        }

        int inCount = inputs.Count;
        int outCount = outputs.Count;

        if (_inJackCenters.Length != inCount)
        {
            _inJackCenters = new Point[inCount];
            _inRowRects = new Rect[inCount];
        }
        if (_outJackCenters.Length != outCount)
        {
            _outJackCenters = new Point[outCount];
            _outRowRects = new Rect[outCount];
            _outPanicRects = new Rect[outCount];
            _outTestRects = new Rect[outCount];
            _outChanRects = new Rect[outCount];
        }

        double contentTop = bounds.Top + 36.0;
        double contentH = Math.Max(120.0, bounds.Height - 44.0);

        double inColW = Math.Clamp(bounds.Width * 0.34, 185.0, 255.0);
        double outColW = Math.Clamp(bounds.Width * 0.38, 210.0, 285.0);
        double inLeft = bounds.Left + 10.0;
        double outLeft = bounds.Right - 10.0 - outColW;

        // 1. Render MIDI IN Ports (Left Column)
        double inStep = contentH / Math.Max(1, inCount);
        double inRowH = Math.Clamp(inStep - 3.0, 22.0, 38.0);

        for (int s = 0; s < inCount; s++)
        {
            var port = inputs[s];
            double ry = contentTop + s * inStep + (inStep - inRowH) * 0.5;
            Rect rowRect = new(inLeft, ry, inColW, inRowH);
            _inRowRects[s] = rowRect;

            Point jackPt = new(rowRect.Right - 12.0, rowRect.Top + rowRect.Height * 0.5);
            _inJackCenters[s] = jackPt;

            Color accent = CablePalette[s % CablePalette.Length];
            bool isHot = (s == _hoverInIdx) || (s == _dragFromInIdx) || (s == _hoverMatIn);

            Brush rowBg = isHot
                ? new SolidColorBrush(Color.FromArgb(65, accent.R, accent.G, accent.B))
                : RowNormalBrush;
            Pen rowPen = isHot
                ? new Pen(new SolidColorBrush(accent), 1.4)
                : RowBorderPen;

            dc.DrawRoundedRectangle(rowBg, rowPen, rowRect, 2.5, 2.5);
            dc.DrawRoundedRectangle(new SolidColorBrush(accent), null, new Rect(rowRect.Left, rowRect.Top, 4.5, rowRect.Height), 2, 2);

            // Activity LED
            Point ledCenter = new(rowRect.Left + 13.0, rowRect.Top + rowRect.Height * 0.5);
            DrawActivityLed(dc, ledCenter, port.ActivityLevel, Color.FromRgb(0x00, 0xE6, 0x76));

            // Port Name + Category / Last MIDI message
            string portLabel = TruncateText(port.ShortDisplayName, 22);
            DrawText(dc, portLabel, rowRect.Left + 23, rowRect.Top + 2.5, 9.8, Colors.White, dpi, true);

            string subInfo = port.ActivityLevel > 0.05
                ? $"{port.CategoryLabel} // {port.LastEventSummary}"
                : $"{port.CategoryLabel} // {(port.IsOpen ? "READY" : "BUSY/CLOSED")}";
            DrawText(dc, TruncateText(subInfo, 28), rowRect.Left + 23, rowRect.Bottom - 12.5, 8.0,
                port.ActivityLevel > 0.05 ? Color.FromRgb(0x00, 0xE6, 0x76) : Color.FromRgb(0x95, 0x95, 0x95), dpi);

            bool hasActiveRoute = Routes != null && Routes.Any(r => r.IsEnabled && string.Equals(r.SourcePortName, port.Name, StringComparison.OrdinalIgnoreCase));
            DrawMidiJackSocket(dc, jackPt, accent, hasActiveRoute || isHot);
        }

        // 2. Render MIDI OUT Ports (Right Column)
        double outStep = contentH / Math.Max(1, outCount);
        double outRowH = Math.Clamp(outStep - 3.0, 22.0, 38.0);

        for (int d = 0; d < outCount; d++)
        {
            var port = outputs[d];
            double ry = contentTop + d * outStep + (outStep - outRowH) * 0.5;
            Rect rowRect = new(outLeft, ry, outColW, outRowH);
            _outRowRects[d] = rowRect;

            Point jackPt = new(rowRect.Left + 12.0, rowRect.Top + rowRect.Height * 0.5);
            _outJackCenters[d] = jackPt;

            Color accent = ParseHexColor(port.AccentColorHex, Color.FromRgb(0x00, 0xE6, 0x76));
            bool isHot = (d == _hoverOutIdx) || (d == _dragFromOutIdx) || (d == _hoverMatOut);

            Brush rowBg = isHot
                ? new SolidColorBrush(Color.FromArgb(60, accent.R, accent.G, accent.B))
                : RowNormalBrush;
            Pen rowPen = isHot
                ? new Pen(new SolidColorBrush(accent), 1.4)
                : RowBorderPen;

            dc.DrawRoundedRectangle(rowBg, rowPen, rowRect, 2.5, 2.5);
            dc.DrawRoundedRectangle(new SolidColorBrush(accent), null, new Rect(rowRect.Right - 4.5, rowRect.Top, 4.5, rowRect.Height), 2, 2);

            // Activity LED
            Point ledCenter = new(jackPt.X + 14.0, rowRect.Top + rowRect.Height * 0.5);
            DrawActivityLed(dc, ledCenter, port.ActivityLevel, Color.FromRgb(0xFF, 0xB0, 0x00));

            string portLabel = TruncateText(port.ShortDisplayName, 15);
            DrawText(dc, portLabel, ledCenter.X + 9, rowRect.Top + 2.5, 9.5, Colors.White, dpi, true);

            string subInfo = port.ActivityLevel > 0.05
                ? port.LastEventSummary
                : $"{port.CategoryLabel} // {(port.IsOpen ? "ONLINE" : "BUSY")}";
            DrawText(dc, TruncateText(subInfo, 19), ledCenter.X + 9, rowRect.Bottom - 12.5, 7.8,
                port.ActivityLevel > 0.05 ? Color.FromRgb(0xFF, 0xB0, 0x00) : Color.FromRgb(0x95, 0x95, 0x95), dpi);

            // Per-port [CH], [C4], and [PANIC] mini action buttons on the right of the row
            double btnH = Math.Min(16.0, rowRect.Height - 4.0);
            double btnY = rowRect.Top + (rowRect.Height - btnH) * 0.5;

            Rect panicRect = new(rowRect.Right - 42.0, btnY, 35.0, btnH);
            Rect testRect = new(panicRect.Left - 29.0, btnY, 26.0, btnH);
            Rect chanRect = new(testRect.Left - 39.0, btnY, 36.0, btnH);
            _outPanicRects[d] = panicRect;
            _outTestRects[d] = testRect;
            _outChanRects[d] = chanRect;

            var activeRouteForOut = Routes?.FirstOrDefault(r => r.IsEnabled && string.Equals(r.DestPortName, port.Name, StringComparison.OrdinalIgnoreCase));
            int dispCh = activeRouteForOut != null ? activeRouteForOut.TargetChannelRemap : port.PreferredChannel;
            string chText = dispCh > 0 ? $"CH{dispCh}" : "PASS";
            bool chanHover = (d == _hoverChanIdx);
            Color chBorderCol = dispCh > 0 ? Color.FromRgb(0xFF, 0xB0, 0x00) : Color.FromRgb(0x70, 0x70, 0x70);
            dc.DrawRoundedRectangle(
                new SolidColorBrush(chanHover ? Color.FromRgb(0x4E, 0x3A, 0x08) : (dispCh > 0 ? Color.FromRgb(0x2E, 0x24, 0x08) : Color.FromRgb(0x1B, 0x1B, 0x1B))),
                new Pen(new SolidColorBrush(chBorderCol), 0.9),
                chanRect, 2, 2);
            DrawText(dc, chText, chanRect.Left + (chText.Length <= 3 ? 6 : 4), chanRect.Top + 1.5, 7.8,
                dispCh > 0 ? Color.FromRgb(0xFF, 0xB0, 0x00) : Color.FromRgb(0xB8, 0xB8, 0xB8), dpi, true);

            bool testHover = (d == _hoverTestIdx);
            dc.DrawRoundedRectangle(
                new SolidColorBrush(testHover ? Color.FromRgb(0x00, 0x55, 0x66) : Color.FromRgb(0x1B, 0x1B, 0x1B)),
                new Pen(new SolidColorBrush(Color.FromRgb(0x00, 0xD4, 0xFF)), 0.9),
                testRect, 2, 2);
            DrawText(dc, "C4", testRect.Left + 6, testRect.Top + 1.5, 8.0, Color.FromRgb(0x00, 0xD4, 0xFF), dpi, true);

            bool panicHover = (d == _hoverPanicIdx);
            dc.DrawRoundedRectangle(
                new SolidColorBrush(panicHover ? Color.FromRgb(0x77, 0x1D, 0x1D) : Color.FromRgb(0x28, 0x16, 0x16)),
                new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0x52, 0x52)), 0.9),
                panicRect, 2, 2);
            DrawText(dc, "PANIC", panicRect.Left + 3.5, panicRect.Top + 1.5, 7.6, Color.FromRgb(0xFF, 0x6E, 0x6E), dpi, true);

            bool hasActiveRoute = Routes != null && Routes.Any(r => r.IsEnabled && string.Equals(r.DestPortName, port.Name, StringComparison.OrdinalIgnoreCase));
            DrawMidiJackSocket(dc, jackPt, accent, hasActiveRoute || isHot);
        }

        // 3. Render Virtual MIDI Patch Cables for all routes
        _cableBadgeHits.Clear();
        if (Routes != null)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                for (int rIdx = 0; rIdx < Routes.Count; rIdx++)
                {
                    var route = Routes[rIdx];
                    int s = IndexOfPort(inputs, route.SourcePortName);
                    int d = IndexOfPort(outputs, route.DestPortName);
                    if (s < 0 || d < 0) continue;

                    bool isSelected = ReferenceEquals(route, SelectedRoute);
                    bool isHovered = isSelected || (s == _hoverInIdx) || (d == _hoverOutIdx) ||
                                     (s == _hoverMatIn && d == _hoverMatOut);

                    if ((pass == 0 && isHovered) || (pass == 1 && !isHovered))
                        continue;

                    Point pStart = _inJackCenters[s];
                    Point pEnd = _outJackCenters[d];
                    Color col = CablePalette[s % CablePalette.Length];
                    if (!route.IsEnabled)
                        col = Color.FromRgb(0x66, 0x66, 0x66);

                    DrawMidiBezierCable(dc, pStart, pEnd, col, isHovered, isSelected, route.RouteActivity > 0.1, false);

                    // Draw clickable filter/channel badge at t=0.68 along the Bezier curve (near destination side so fan-out cables never overlap)
                    Point mid = EvaluateCablePoint(pStart, pEnd, 0.68);
                    string badgeText = route.FilterSummary;
                    double badgeW = Math.Max(44.0, badgeText.Length * 5.2 + 10.0);
                    Rect badgeRect = new(mid.X - badgeW * 0.5, mid.Y - 8.0, badgeW, 16.0);

                    Brush badgeBg = isSelected
                        ? new SolidColorBrush(Color.FromRgb(0x2A, 0x22, 0x08))
                        : new SolidColorBrush(Color.FromArgb(225, 0x16, 0x16, 0x16));
                    Pen badgePen = new(new SolidColorBrush(isSelected ? Color.FromRgb(0xFF, 0xB0, 0x00) : col), isSelected ? 1.5 : 1.0);

                    dc.DrawRoundedRectangle(badgeBg, badgePen, badgeRect, 3, 3);
                    DrawText(dc, badgeText, badgeRect.Left + 5, badgeRect.Top + 2.0, 8.0,
                        isSelected ? Color.FromRgb(0xFF, 0xB0, 0x00) : Colors.White, dpi, true);

                    _cableBadgeHits.Add((badgeRect, route));
                }
            }
        }

        // 4. Render Active Dragging Preview Cable
        if (_dragFromInIdx >= 0 && _dragFromInIdx < inCount)
        {
            Point pStart = _inJackCenters[_dragFromInIdx];
            Point pEnd = (_hoverOutIdx >= 0 && _hoverOutIdx < outCount) ? _outJackCenters[_hoverOutIdx] : _dragMousePos;
            Color col = CablePalette[_dragFromInIdx % CablePalette.Length];
            DrawMidiBezierCable(dc, pStart, pEnd, col, true, true, false, true);
        }
        else if (_dragFromOutIdx >= 0 && _dragFromOutIdx < outCount)
        {
            Point pEnd = _outJackCenters[_dragFromOutIdx];
            Point pStart = (_hoverInIdx >= 0 && _hoverInIdx < inCount) ? _inJackCenters[_hoverInIdx] : _dragMousePos;
            Color col = _hoverInIdx >= 0 ? CablePalette[_hoverInIdx % CablePalette.Length] : Color.FromRgb(0x00, 0xD4, 0xFF);
            DrawMidiBezierCable(dc, pStart, pEnd, col, true, true, false, true);
        }
    }

    private void RenderMidiCrosspointMatrix(DrawingContext dc, Rect bounds, double dpi)
    {
        dc.DrawRoundedRectangle(BayCardBgBrush, CardBorderPen, bounds, 3, 3);

        Rect hdrRect = new(bounds.Left, bounds.Top, bounds.Width, 30);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B)), CardBorderPen, hdrRect, 3, 3);
        DrawText(dc, "MANY-TO-MANY MIDI CROSSPOINT MATRIX", bounds.Left + 12, bounds.Top + 7, 10.5, Color.FromRgb(0x00, 0xD4, 0xFF), dpi, true);
        DrawText(dc, "L-CLICK: TOGGLE  |  R-CLICK: INSPECT", bounds.Right - 196, bounds.Top + 8, 8.8, Color.FromRgb(0x9A, 0x9A, 0x9A), dpi);

        var inputs = InputPorts;
        var outputs = OutputPorts;
        if (inputs == null || outputs == null || inputs.Count == 0 || outputs.Count == 0)
            return;

        int inCount = inputs.Count;
        int outCount = outputs.Count;

        if (_matrixCellRects.GetLength(0) != inCount || _matrixCellRects.GetLength(1) != outCount)
            _matrixCellRects = new Rect[inCount, outCount];

        double labelColW = 132.0;
        double gridLeft = bounds.Left + labelColW + 8.0;
        double gridTop = bounds.Top + 72.0;
        double gridW = Math.Max(80.0, bounds.Width - labelColW - 18.0);
        double gridH = Math.Max(100.0, bounds.Height - 82.0);

        double cellW = gridW / outCount;
        double cellH = gridH / inCount;

        // Column Headers (Angled -35 deg so all 12+ USB synths & DIN ports read clearly)
        for (int d = 0; d < outCount; d++)
        {
            double cx = gridLeft + d * cellW + cellW * 0.5;
            bool colHot = (d == _hoverMatOut) || (d == _hoverOutIdx);
            Color hdrCol = colHot ? Color.FromRgb(0xFF, 0xB0, 0x00) : Color.FromRgb(0xC8, 0xC8, 0xC8);

            string shortCol = ShortMatrixHeader(outputs[d].ShortDisplayName);
            dc.PushTransform(new RotateTransform(-32, cx, gridTop - 8));
            DrawText(dc, shortCol, cx - 6, gridTop - 16, 8.5, hdrCol, dpi, true);
            dc.Pop();
        }

        // Rows (Inputs) x Columns (Outputs)
        for (int s = 0; s < inCount; s++)
        {
            double ry = gridTop + s * cellH;
            Color rowColor = CablePalette[s % CablePalette.Length];
            bool rowHot = (s == _hoverMatIn) || (s == _hoverInIdx);

            string rowName = TruncateText(inputs[s].ShortDisplayName, 19);
            DrawText(dc, rowName, bounds.Left + 10, ry + cellH * 0.5 - 6.5, 8.8,
                rowHot ? rowColor : Color.FromRgb(0xD0, 0xD0, 0xD0), dpi, true);

            for (int d = 0; d < outCount; d++)
            {
                double cx = gridLeft + d * cellW;
                Rect cellRect = new(cx + 1.5, ry + 1.5, Math.Max(6, cellW - 3.0), Math.Max(6, cellH - 3.0));
                _matrixCellRects[s, d] = cellRect;

                var route = FindRoute(inputs[s].Name, outputs[d].Name);
                bool isPatched = route != null && route.IsEnabled;
                bool isMutedPatch = route != null && !route.IsEnabled;
                bool isSelected = route != null && ReferenceEquals(route, SelectedRoute);
                bool isHover = (s == _hoverMatIn && d == _hoverMatOut);
                bool isCrosshair = (s == _hoverMatIn || d == _hoverMatOut);

                Brush cellBg;
                Pen cellPen;

                if (isPatched)
                {
                    cellBg = new SolidColorBrush(Color.FromArgb(225, rowColor.R, rowColor.G, rowColor.B));
                    cellPen = new Pen(isSelected ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)), isSelected ? 2.0 : 1.0);
                }
                else if (isMutedPatch)
                {
                    cellBg = new SolidColorBrush(Color.FromRgb(0x38, 0x28, 0x28));
                    cellPen = new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0x52, 0x52)), 1.2);
                }
                else if (isHover)
                {
                    cellBg = new SolidColorBrush(Color.FromArgb(90, rowColor.R, rowColor.G, rowColor.B));
                    cellPen = new Pen(new SolidColorBrush(rowColor), 1.2);
                }
                else if (isCrosshair)
                {
                    cellBg = new SolidColorBrush(Color.FromRgb(0x28, 0x28, 0x28));
                    cellPen = new Pen(new SolidColorBrush(Color.FromRgb(0x42, 0x42, 0x42)), 1.0);
                }
                else
                {
                    cellBg = new SolidColorBrush(Color.FromRgb(0x15, 0x15, 0x15));
                    cellPen = new Pen(new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2C)), 1.0);
                }

                dc.DrawRoundedRectangle(cellBg, cellPen, cellRect, 2.0, 2.0);

                if (isPatched && route != null)
                {
                    // Show target channel number inside the matrix cell if remapped, or a solid node dot
                    if (route.TargetChannelRemap > 0)
                    {
                        DrawText(dc, $"C{route.TargetChannelRemap}", cellRect.Left + 2, cellRect.Top + cellRect.Height * 0.5 - 5.5, 7.5, Colors.Black, dpi, true);
                    }
                    else
                    {
                        Point center = new(cellRect.Left + cellRect.Width * 0.5, cellRect.Top + cellRect.Height * 0.5);
                        dc.DrawEllipse(Brushes.Black, null, center, 3.0, 3.0);
                    }
                }
            }
        }
    }

    private static void DrawActivityLed(DrawingContext dc, Point center, double level, Color color)
    {
        if (level > 0.1)
        {
            byte glowAlpha = (byte)Math.Clamp(level * 190, 40, 200);
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(glowAlpha, color.R, color.G, color.B)), null, center, 6.0, 6.0);
            dc.DrawEllipse(new SolidColorBrush(color), new Pen(Brushes.White, 0.8), center, 3.5, 3.5);
        }
        else
        {
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14)), new Pen(new SolidColorBrush(Color.FromRgb(0x48, 0x48, 0x48)), 1.0), center, 3.5, 3.5);
        }
    }

    private static void DrawMidiJackSocket(DrawingContext dc, Point center, Color accent, bool connected)
    {
        Pen outerPen = connected
            ? new Pen(new SolidColorBrush(accent), 2.0)
            : JackRingPen;
        dc.DrawEllipse(JackSocketBrush, outerPen, center, 6.5, 6.5);
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x06, 0x06, 0x06)), new Pen(new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)), 0.9), center, 3.2, 3.2);
    }

    private static void DrawMidiBezierCable(
        DrawingContext dc,
        Point pStart,
        Point pEnd,
        Color color,
        bool isHighlighted,
        bool isSelected,
        bool isPulsing,
        bool isDraggingPreview)
    {
        double dx = Math.Max(45.0, Math.Abs(pEnd.X - pStart.X) * 0.50);
        double sag = isDraggingPreview ? 6.0 : 12.0;
        Point c1 = new(pStart.X + dx, pStart.Y + sag);
        Point c2 = new(pEnd.X - dx, pEnd.Y + sag);

        StreamGeometry geom = new();
        using (StreamGeometryContext ctx = geom.Open())
        {
            ctx.BeginFigure(pStart, false, false);
            ctx.BezierTo(c1, c2, pEnd, true, true);
        }
        geom.Freeze();

        Pen shadowPen = new(new SolidColorBrush(Color.FromArgb(115, 0, 0, 0)), isHighlighted ? 6.0 : 4.5)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        dc.PushTransform(new TranslateTransform(0, 2.5));
        dc.DrawGeometry(null, shadowPen, geom);
        dc.Pop();

        byte alpha = (isHighlighted || isPulsing) ? (byte)255 : (byte)200;
        double thickness = isSelected ? 4.4 : (isHighlighted || isPulsing ? 3.8 : 2.8);
        Pen cablePen = new(new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B)), thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        if (isDraggingPreview)
            cablePen.DashStyle = DashStyles.Dash;

        dc.DrawGeometry(null, cablePen, geom);

        if (isSelected || isPulsing)
        {
            Pen corePen = new(new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), 1.3)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            dc.DrawGeometry(null, corePen, geom);
        }

        Brush plugBrush = new SolidColorBrush(color);
        Pen plugPen = new(Brushes.White, 1.0);
        dc.DrawEllipse(plugBrush, plugPen, pStart, 4.0, 4.0);
        dc.DrawEllipse(plugBrush, plugPen, pEnd, 4.0, 4.0);
    }

    private static Point EvaluateCablePoint(Point pStart, Point pEnd, double t)
    {
        double dx = Math.Max(45.0, Math.Abs(pEnd.X - pStart.X) * 0.50);
        double sag = 12.0;
        Point c1 = new(pStart.X + dx, pStart.Y + sag);
        Point c2 = new(pEnd.X - dx, pEnd.Y + sag);

        double u = 1.0 - t;
        double tt = t * t;
        double uu = u * u;
        double uuu = uu * u;
        double ttt = tt * t;

        double x = uuu * pStart.X + 3 * uu * t * c1.X + 3 * u * tt * c2.X + ttt * pEnd.X;
        double y = uuu * pStart.Y + 3 * uu * t * c1.Y + 3 * u * tt * c2.Y + ttt * pEnd.Y;
        return new Point(x, y);
    }

    private static int IndexOfPort(ObservableCollection<MidiPortModel> list, string name)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    private static string TruncateText(string s, int maxChars)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= maxChars) return s;
        return s[..(maxChars - 1)] + "\u2026";
    }

    private static string ShortMatrixHeader(string name)
    {
        if (name.Contains("US-16x08", StringComparison.OrdinalIgnoreCase)) return "US-16x08";
        if (name.StartsWith("Launchkey", StringComparison.OrdinalIgnoreCase)) return "LK MK4";
        if (name.StartsWith("MIDIOUT2", StringComparison.OrdinalIgnoreCase)) return "LK DAW";
        if (name.StartsWith("WASP", StringComparison.OrdinalIgnoreCase)) return "WASP";
        if (name.StartsWith("KOBOL", StringComparison.OrdinalIgnoreCase)) return "KOBOL";
        if (name.StartsWith("SYNCUSSION", StringComparison.OrdinalIgnoreCase)) return "SY-1";
        if (name.StartsWith("General MIDI", StringComparison.OrdinalIgnoreCase)) return "GM SYNTH";
        return TruncateText(name, 9);
    }

    private static Color ParseHexColor(string hex, Color fallback)
    {
        return hex switch
        {
            "#00D4FF" => Color.FromRgb(0x00, 0xD4, 0xFF),
            "#FFB000" => Color.FromRgb(0xFF, 0xB0, 0x00),
            "#00E676" => Color.FromRgb(0x00, 0xE6, 0x76),
            "#9E9E9E" => Color.FromRgb(0x9E, 0x9E, 0x9E),
            _ => fallback
        };
    }

    private static void DrawText(DrawingContext dc, string text, double x, double y, double size, Color color, double dpi, bool bold = false)
    {
        var key = (text, (int)Math.Round(size * 10), color, bold, (int)Math.Round(dpi * 100));
        if (!TextCache.TryGetValue(key, out var ft))
        {
            if (TextCache.Count > 512)
                TextCache.Clear();

            ft = new FormattedText(
                text,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                bold ? UiBold : UiBlack,
                size,
                GetCachedBrush(color),
                dpi);
            TextCache[key] = ft;
        }
        dc.DrawText(ft, new Point(x, y));
    }
}
