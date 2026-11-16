using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using US16x08_AbletonPanel.Models;

namespace US16x08_AbletonPanel.Controls;

public sealed class RoutingPatchbayControl : FrameworkElement
{
    public static readonly DependencyProperty RoutesProperty =
        DependencyProperty.Register(
            nameof(Routes),
            typeof(ObservableCollection<LineOutRouteModel>),
            typeof(RoutingPatchbayControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnRoutesChanged));

    public ObservableCollection<LineOutRouteModel>? Routes
    {
        get => (ObservableCollection<LineOutRouteModel>?)GetValue(RoutesProperty);
        set => SetValue(RoutesProperty, value);
    }

    private readonly Point[] _sourceJackCenters = new Point[10];
    private readonly Rect[] _sourceRowRects = new Rect[10];
    private readonly Point[] _destJackCenters = new Point[8];
    private readonly Rect[] _destRowRects = new Rect[8];
    private readonly Rect[,] _matrixCellRects = new Rect[10, 8];

    // Interactive Drag-to-Patch state
    private int _dragFromSourceIdx = -1; // 0..9 when dragging from a Source jack
    private int _dragFromDestIdx = -1;   // 0..7 when dragging from a Line Out jack
    private Point _dragMousePos;

    // Hover state
    private int _hoverSourceIdx = -1;
    private int _hoverDestIdx = -1;
    private int _hoverMatrixSource = -1;
    private int _hoverMatrixDest = -1;

    private static readonly Color[] SourceCableColors =
    {
        Color.FromRgb(0x00, 0xD4, 0xFF), // 0: MASTER L (Ableton Cyan)
        Color.FromRgb(0x38, 0xB6, 0xFF), // 1: MASTER R (Deep Sky Blue)
        Color.FromRgb(0xFF, 0xB0, 0x00), // 2: COMPUTER 1 (Ableton Amber)
        Color.FromRgb(0xFF, 0x52, 0x52), // 3: COMPUTER 2 (Coral Red)
        Color.FromRgb(0x00, 0xE6, 0x76), // 4: COMPUTER 3 (Emerald)
        Color.FromRgb(0xA7, 0x70, 0xFF), // 5: COMPUTER 4 (Purple)
        Color.FromRgb(0xFF, 0x6E, 0xC7), // 6: COMPUTER 5 (Pink)
        Color.FromRgb(0x26, 0xC6, 0xDA), // 7: COMPUTER 6 (Teal)
        Color.FromRgb(0xFF, 0xD5, 0x4F), // 8: COMPUTER 7 (Gold)
        Color.FromRgb(0x69, 0xF0, 0xAE)  // 9: COMPUTER 8 (Mint Green)
    };

    private static readonly Brush PanelBgBrush = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x18));
    private static readonly Brush BayCardBgBrush = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
    private static readonly Pen CardBorderPen = new(new SolidColorBrush(Color.FromRgb(0x36, 0x36, 0x36)), 1.0);
    private static readonly Brush RowNormalBrush = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x26));
    private static readonly Brush RowHoverBrush = new SolidColorBrush(Color.FromRgb(0x32, 0x32, 0x32));
    private static readonly Pen RowBorderPen = new(new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)), 1.0);
    private static readonly Brush JackSocketBrush = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x10));
    private static readonly Pen JackRingPen = new(new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)), 2.0);
    private static readonly Typeface UiBold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private static readonly Typeface UiBlack = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Black, FontStretches.Normal);

    static RoutingPatchbayControl()
    {
        PanelBgBrush.Freeze();
        BayCardBgBrush.Freeze();
        CardBorderPen.Freeze();
        RowNormalBrush.Freeze();
        RowHoverBrush.Freeze();
        RowBorderPen.Freeze();
        JackSocketBrush.Freeze();
        JackRingPen.Freeze();
    }

    public RoutingPatchbayControl()
    {
        ClipToBounds = true;
        Cursor = Cursors.Hand;
    }

    private static void OnRoutesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ctrl = (RoutingPatchbayControl)d;
        if (e.OldValue is ObservableCollection<LineOutRouteModel> oldCol)
        {
            oldCol.CollectionChanged -= ctrl.OnCollectionChanged;
            foreach (var r in oldCol) r.PropertyChanged -= ctrl.OnRoutePropertyChanged;
        }
        if (e.NewValue is ObservableCollection<LineOutRouteModel> newCol)
        {
            newCol.CollectionChanged += ctrl.OnCollectionChanged;
            foreach (var r in newCol) r.PropertyChanged += ctrl.OnRoutePropertyChanged;
        }
        ctrl.InvalidateVisual();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
        {
            foreach (LineOutRouteModel r in e.OldItems)
                r.PropertyChanged -= OnRoutePropertyChanged;
        }
        if (e.NewItems != null)
        {
            foreach (LineOutRouteModel r in e.NewItems)
                r.PropertyChanged += OnRoutePropertyChanged;
        }
        Dispatcher.InvokeAsync(InvalidateVisual);
    }

    private void OnRoutePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        Dispatcher.InvokeAsync(InvalidateVisual);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Routes == null || Routes.Count < 8) return;

        Point pt = e.GetPosition(this);
        _dragMousePos = pt;

        // 1. Check if user clicked inside the 10x8 Crosspoint Matrix
        for (int s = 0; s < 10; s++)
        {
            for (int d = 0; d < 8; d++)
            {
                if (_matrixCellRects[s, d].Contains(pt))
                {
                    Routes[d].SourceIndex = s;
                    InvalidateVisual();
                    e.Handled = true;
                    return;
                }
            }
        }

        // 2. Check if user clicked a Source row or Source jack (0..9) to start dragging a patch cable
        for (int s = 0; s < 10; s++)
        {
            if ((pt - _sourceJackCenters[s]).Length <= 18.0 || _sourceRowRects[s].Contains(pt))
            {
                _dragFromSourceIdx = s;
                _dragFromDestIdx = -1;
                CaptureMouse();
                InvalidateVisual();
                e.Handled = true;
                return;
            }
        }

        // 3. Check if user clicked a Destination (Line Out 1..8) jack or row to re-patch
        for (int d = 0; d < 8; d++)
        {
            if ((pt - _destJackCenters[d]).Length <= 18.0 || _destRowRects[d].Contains(pt))
            {
                _dragFromDestIdx = d;
                _dragFromSourceIdx = -1;
                CaptureMouse();
                InvalidateVisual();
                e.Handled = true;
                return;
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Point pt = e.GetPosition(this);
        _dragMousePos = pt;

        int newHoverSrc = -1;
        int newHoverDst = -1;
        int newMatSrc = -1;
        int newMatDst = -1;

        for (int s = 0; s < 10; s++)
        {
            if ((pt - _sourceJackCenters[s]).Length <= 18.0 || _sourceRowRects[s].Contains(pt))
            {
                newHoverSrc = s;
                break;
            }
        }

        for (int d = 0; d < 8; d++)
        {
            if ((pt - _destJackCenters[d]).Length <= 18.0 || _destRowRects[d].Contains(pt))
            {
                newHoverDst = d;
                break;
            }
        }

        for (int s = 0; s < 10; s++)
        {
            for (int d = 0; d < 8; d++)
            {
                if (_matrixCellRects[s, d].Contains(pt))
                {
                    newMatSrc = s;
                    newMatDst = d;
                    break;
                }
            }
        }

        if (_dragFromSourceIdx >= 0 || _dragFromDestIdx >= 0 ||
            newHoverSrc != _hoverSourceIdx || newHoverDst != _hoverDestIdx ||
            newMatSrc != _hoverMatrixSource || newMatDst != _hoverMatrixDest)
        {
            _hoverSourceIdx = newHoverSrc;
            _hoverDestIdx = newHoverDst;
            _hoverMatrixSource = newMatSrc;
            _hoverMatrixDest = newMatDst;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (Routes == null || Routes.Count < 8) return;

        if (_dragFromSourceIdx >= 0)
        {
            if (_hoverDestIdx >= 0 && _hoverDestIdx < 8)
            {
                Routes[_hoverDestIdx].SourceIndex = _dragFromSourceIdx;
            }
            _dragFromSourceIdx = -1;
            ReleaseMouseCapture();
            InvalidateVisual();
            e.Handled = true;
        }
        else if (_dragFromDestIdx >= 0)
        {
            if (_hoverSourceIdx >= 0 && _hoverSourceIdx < 10)
            {
                Routes[_dragFromDestIdx].SourceIndex = _hoverSourceIdx;
            }
            _dragFromDestIdx = -1;
            ReleaseMouseCapture();
            InvalidateVisual();
            e.Handled = true;
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_dragFromSourceIdx < 0 && _dragFromDestIdx < 0)
        {
            _hoverSourceIdx = -1;
            _hoverDestIdx = -1;
            _hoverMatrixSource = -1;
            _hoverMatrixDest = -1;
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        double h = ActualHeight;
        if (w < 300 || h < 200) return;

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // Background
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        // Split into Left Visual Cable Patchbay (56% width) and Right Crosspoint Matrix (44% width)
        double gap = 14.0;
        double patchbayW = Math.Floor((w - gap) * 0.57);
        double matrixW = w - gap - patchbayW;
        double matrixX = patchbayW + gap;

        RenderVisualCablePatchbay(dc, new Rect(0, 0, patchbayW, h), dpi);
        RenderCrosspointMatrix(dc, new Rect(matrixX, 0, matrixW, h), dpi);
    }

    private void RenderVisualCablePatchbay(DrawingContext dc, Rect bounds, double dpi)
    {
        dc.DrawRoundedRectangle(BayCardBgBrush, CardBorderPen, bounds, 3, 3);

        // Sub-header
        Rect hdrRect = new(bounds.Left, bounds.Top, bounds.Width, 32);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B)), CardBorderPen, hdrRect, 3, 3);
        DrawText(dc, "VISUAL PATCHBAY // DRAG JACK-TO-JACK TO ROUTE", bounds.Left + 12, bounds.Top + 8, 11, Color.FromRgb(0xFF, 0xB0, 0x00), dpi, true);
        DrawText(dc, "SOURCES (10)  \u2192  PHYSICAL TRS LINE OUTPUTS (8)", bounds.Right - 270, bounds.Top + 9, 9.5, Color.FromRgb(0x9A, 0x9A, 0x9A), dpi);

        double contentTop = bounds.Top + 42.0;
        double contentH = bounds.Height - 52.0;

        double srcColW = Math.Clamp(bounds.Width * 0.30, 175.0, 230.0);
        double dstColW = Math.Clamp(bounds.Width * 0.32, 190.0, 245.0);
        double srcLeft = bounds.Left + 12.0;
        double dstLeft = bounds.Right - 12.0 - dstColW;

        // Count how many outputs each source feeds
        Span<int> sourceFanout = stackalloc int[10];
        if (Routes != null)
        {
            for (int i = 0; i < Math.Min(8, Routes.Count); i++)
            {
                int s = Math.Clamp(Routes[i].SourceIndex, 0, 9);
                sourceFanout[s]++;
            }
        }

        // 1. Render 10 Signal Source Rows on the Left
        double srcRowStep = contentH / 10.0;
        double srcRowH = Math.Min(srcRowStep - 5.0, 42.0);

        for (int s = 0; s < 10; s++)
        {
            double ry = contentTop + s * srcRowStep + (srcRowStep - srcRowH) * 0.5;
            Rect rowRect = new(srcLeft, ry, srcColW, srcRowH);
            _sourceRowRects[s] = rowRect;

            Point jackPt = new(rowRect.Right - 14.0, rowRect.Top + rowRect.Height * 0.5);
            _sourceJackCenters[s] = jackPt;

            Color accent = SourceCableColors[s];
            bool isHighlighted = (s == _hoverSourceIdx) || (s == _dragFromSourceIdx) || (s == _hoverMatrixSource);
            if (_hoverDestIdx >= 0 && Routes != null && _hoverDestIdx < Routes.Count && Routes[_hoverDestIdx].SourceIndex == s)
                isHighlighted = true;

            Brush rowBg = isHighlighted
                ? new SolidColorBrush(Color.FromArgb(65, accent.R, accent.G, accent.B))
                : RowNormalBrush;
            Pen rowPen = isHighlighted
                ? new Pen(new SolidColorBrush(accent), 1.5)
                : RowBorderPen;

            dc.DrawRoundedRectangle(rowBg, rowPen, rowRect, 2.5, 2.5);

            // Left color tab
            dc.DrawRoundedRectangle(new SolidColorBrush(accent), null, new Rect(rowRect.Left, rowRect.Top, 5, rowRect.Height), 2, 2);

            // Source Name & Category
            string srcName = TascamDspTables.LineOutSourceNames[s];
            string subTag = s <= 1 ? "DSP MASTER" : $"USB CH {s - 1}";
            DrawText(dc, srcName, rowRect.Left + 11, rowRect.Top + 4, 10.5, Colors.White, dpi, true);
            DrawText(dc, subTag, rowRect.Left + 11, rowRect.Bottom - 15, 8.5, Color.FromRgb(0x90, 0x90, 0x90), dpi);

            // Fanout badge if patched
            if (sourceFanout[s] > 0)
            {
                string fanText = $"{sourceFanout[s]}x";
                Rect badge = new(jackPt.X - 38, rowRect.Top + (rowRect.Height - 16) * 0.5, 22, 16);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(45, accent.R, accent.G, accent.B)),
                    new Pen(new SolidColorBrush(accent), 1.0), badge, 2, 2);
                DrawText(dc, fanText, badge.Left + 4, badge.Top + 1.5, 8.5, accent, dpi, true);
            }

            // Draw 1/4" TRS Jack Socket
            DrawJackSocket(dc, jackPt, accent, sourceFanout[s] > 0 || isHighlighted);
        }

        // 2. Render 8 Physical Line Output Rows on the Right
        double dstRowStep = contentH / 8.0;
        double dstRowH = Math.Min(dstRowStep - 6.0, 48.0);

        for (int d = 0; d < 8; d++)
        {
            double ry = contentTop + d * dstRowStep + (dstRowStep - dstRowH) * 0.5;
            Rect rowRect = new(dstLeft, ry, dstColW, dstRowH);
            _destRowRects[d] = rowRect;

            Point jackPt = new(rowRect.Left + 14.0, rowRect.Top + rowRect.Height * 0.5);
            _destJackCenters[d] = jackPt;

            int connectedSrc = (Routes != null && d < Routes.Count) ? Math.Clamp(Routes[d].SourceIndex, 0, 9) : 0;
            Color accent = SourceCableColors[connectedSrc];

            bool isHighlighted = (d == _hoverDestIdx) || (d == _dragFromDestIdx) || (d == _hoverMatrixDest) ||
                                 (_hoverSourceIdx >= 0 && connectedSrc == _hoverSourceIdx) ||
                                 (_hoverMatrixSource >= 0 && connectedSrc == _hoverMatrixSource);

            Brush rowBg = isHighlighted
                ? new SolidColorBrush(Color.FromArgb(65, accent.R, accent.G, accent.B))
                : RowNormalBrush;
            Pen rowPen = isHighlighted
                ? new Pen(new SolidColorBrush(accent), 1.5)
                : RowBorderPen;

            dc.DrawRoundedRectangle(rowBg, rowPen, rowRect, 2.5, 2.5);

            // Right color bar
            dc.DrawRoundedRectangle(new SolidColorBrush(accent), null, new Rect(rowRect.Right - 5, rowRect.Top, 5, rowRect.Height), 2, 2);

            string outTitle = d switch
            {
                0 => "LINE OUT 1 (MON L)",
                1 => "LINE OUT 2 (MON R)",
                _ => $"LINE OUT {d + 1} (TRS)"
            };
            string patchedLabel = $"\u2190 {TascamDspTables.LineOutSourceNames[connectedSrc]}";

            DrawText(dc, outTitle, jackPt.X + 16, rowRect.Top + 5, 10.5, Colors.White, dpi, true);
            DrawText(dc, patchedLabel, jackPt.X + 16, rowRect.Bottom - 17, 9.5, accent, dpi, true);

            // Draw 1/4" TRS Jack Socket
            DrawJackSocket(dc, jackPt, accent, true);
        }

        // 3. Render Patch Cables for all 8 Line Outputs
        if (Routes != null)
        {
            // Draw non-highlighted cables first, then highlighted cables on top
            for (int pass = 0; pass < 2; pass++)
            {
                for (int d = 0; d < Math.Min(8, Routes.Count); d++)
                {
                    int s = Math.Clamp(Routes[d].SourceIndex, 0, 9);
                    bool isHoveredCable = (d == _hoverDestIdx) || (s == _hoverSourceIdx) ||
                                          (d == _hoverMatrixDest) || (s == _hoverMatrixSource);

                    if ((pass == 0 && isHoveredCable) || (pass == 1 && !isHoveredCable))
                        continue;

                    Point pStart = _sourceJackCenters[s];
                    Point pEnd = _destJackCenters[d];
                    Color col = SourceCableColors[s];

                    DrawBezierPatchCable(dc, pStart, pEnd, col, isHoveredCable, false);
                }
            }
        }

        // 4. Render Active Dragging Patch Cable if user is dragging
        if (_dragFromSourceIdx >= 0)
        {
            Point pStart = _sourceJackCenters[_dragFromSourceIdx];
            Point pEnd = _hoverDestIdx >= 0 ? _destJackCenters[_hoverDestIdx] : _dragMousePos;
            Color col = SourceCableColors[_dragFromSourceIdx];
            DrawBezierPatchCable(dc, pStart, pEnd, col, true, true);
        }
        else if (_dragFromDestIdx >= 0)
        {
            Point pEnd = _destJackCenters[_dragFromDestIdx];
            Point pStart = _hoverSourceIdx >= 0 ? _sourceJackCenters[_hoverSourceIdx] : _dragMousePos;
            int sColor = _hoverSourceIdx >= 0 ? _hoverSourceIdx : (Routes != null ? Routes[_dragFromDestIdx].SourceIndex : 0);
            Color col = SourceCableColors[Math.Clamp(sColor, 0, 9)];
            DrawBezierPatchCable(dc, pStart, pEnd, col, true, true);
        }
    }

    private void RenderCrosspointMatrix(DrawingContext dc, Rect bounds, double dpi)
    {
        dc.DrawRoundedRectangle(BayCardBgBrush, CardBorderPen, bounds, 3, 3);

        // Header
        Rect hdrRect = new(bounds.Left, bounds.Top, bounds.Width, 32);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B)), CardBorderPen, hdrRect, 3, 3);
        DrawText(dc, "10\u00D78 CROSSPOINT ROUTING MATRIX", bounds.Left + 12, bounds.Top + 8, 11, Color.FromRgb(0x00, 0xD4, 0xFF), dpi, true);
        DrawText(dc, "CLICK INTERSECTION TO PATCH", bounds.Right - 168, bounds.Top + 9, 9.5, Color.FromRgb(0x9A, 0x9A, 0x9A), dpi);

        double labelColW = 108.0;
        double gridLeft = bounds.Left + labelColW + 12.0;
        double gridTop = bounds.Top + 66.0;
        double gridW = Math.Max(80.0, bounds.Width - labelColW - 24.0);
        double gridH = Math.Max(120.0, bounds.Height - 80.0);

        double cellW = gridW / 8.0;
        double cellH = gridH / 10.0;

        // Column Headers (OUT 1 .. OUT 8)
        for (int d = 0; d < 8; d++)
        {
            double cx = gridLeft + d * cellW;
            bool colHot = (d == _hoverMatrixDest) || (d == _hoverDestIdx);
            Color hdrCol = colHot ? Color.FromRgb(0xFF, 0xB0, 0x00) : Color.FromRgb(0xC0, 0xC0, 0xC0);
            DrawText(dc, $"OUT {d + 1}", cx + cellW * 0.5 - 15, gridTop - 22, 9.5, hdrCol, dpi, true);
        }

        // Rows (10 Sources) x Columns (8 Outputs)
        for (int s = 0; s < 10; s++)
        {
            double ry = gridTop + s * cellH;
            Color srcColor = SourceCableColors[s];
            bool rowHot = (s == _hoverMatrixSource) || (s == _hoverSourceIdx);

            // Row label on left
            DrawText(dc, TascamDspTables.LineOutSourceNames[s], bounds.Left + 12, ry + cellH * 0.5 - 7, 9.5,
                rowHot ? srcColor : Color.FromRgb(0xCC, 0xCC, 0xCC), dpi, true);

            for (int d = 0; d < 8; d++)
            {
                double cx = gridLeft + d * cellW;
                Rect cellRect = new(cx + 2, ry + 2, Math.Max(8, cellW - 4), Math.Max(8, cellH - 4));
                _matrixCellRects[s, d] = cellRect;

                bool isPatched = Routes != null && d < Routes.Count && Routes[d].SourceIndex == s;
                bool isCellHover = (s == _hoverMatrixSource && d == _hoverMatrixDest);
                bool isCrosshair = (s == _hoverMatrixSource || d == _hoverMatrixDest);

                Brush cellBg;
                Pen cellPen;

                if (isPatched)
                {
                    cellBg = new SolidColorBrush(Color.FromArgb(220, srcColor.R, srcColor.G, srcColor.B));
                    cellPen = new Pen(Brushes.White, 1.4);
                }
                else if (isCellHover)
                {
                    cellBg = new SolidColorBrush(Color.FromArgb(90, srcColor.R, srcColor.G, srcColor.B));
                    cellPen = new Pen(new SolidColorBrush(srcColor), 1.2);
                }
                else if (isCrosshair)
                {
                    cellBg = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
                    cellPen = new Pen(new SolidColorBrush(Color.FromRgb(0x48, 0x48, 0x48)), 1.0);
                }
                else
                {
                    cellBg = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x16));
                    cellPen = new Pen(new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D)), 1.0);
                }

                dc.DrawRoundedRectangle(cellBg, cellPen, cellRect, 2.5, 2.5);

                if (isPatched)
                {
                    Point center = new(cellRect.Left + cellRect.Width * 0.5, cellRect.Top + cellRect.Height * 0.5);
                    dc.DrawEllipse(Brushes.Black, null, center, 3.5, 3.5);
                }
            }
        }
    }

    private static void DrawJackSocket(DrawingContext dc, Point center, Color accent, bool connected)
    {
        // Outer metallic hex/ring
        Pen outerPen = connected
            ? new Pen(new SolidColorBrush(accent), 2.2)
            : JackRingPen;
        dc.DrawEllipse(JackSocketBrush, outerPen, center, 7.5, 7.5);
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x06, 0x06, 0x06)), new Pen(new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)), 1.0), center, 4.0, 4.0);
    }

    private static void DrawBezierPatchCable(DrawingContext dc, Point pStart, Point pEnd, Color color, bool isHighlighted, bool isDraggingPreview)
    {
        double dx = Math.Max(50.0, Math.Abs(pEnd.X - pStart.X) * 0.52);
        // Add a slight natural gravity droop for realism
        double sag = isDraggingPreview ? 8.0 : 14.0;
        Point c1 = new(pStart.X + dx, pStart.Y + sag);
        Point c2 = new(pEnd.X - dx, pEnd.Y + sag);

        StreamGeometry geom = new();
        using (StreamGeometryContext ctx = geom.Open())
        {
            ctx.BeginFigure(pStart, false, false);
            ctx.BezierTo(c1, c2, pEnd, true, true);
        }
        geom.Freeze();

        // 1. Cable drop shadow
        Pen shadowPen = new(new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)), isHighlighted ? 6.5 : 5.0)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        dc.PushTransform(new TranslateTransform(0, 3));
        dc.DrawGeometry(null, shadowPen, geom);
        dc.Pop();

        // 2. Main cable jacket
        byte alpha = isHighlighted ? (byte)255 : (byte)205;
        Pen cablePen = new(new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B)), isHighlighted ? 4.4 : 3.2)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        if (isDraggingPreview)
            cablePen.DashStyle = DashStyles.Dash;

        dc.DrawGeometry(null, cablePen, geom);

        // 3. Bright center specular core when highlighted
        if (isHighlighted)
        {
            Pen corePen = new(new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)), 1.3)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            dc.DrawGeometry(null, corePen, geom);
        }

        // 4. Plug heads at both ends
        Brush plugBrush = new SolidColorBrush(color);
        Pen plugPen = new(Brushes.White, 1.2);
        dc.DrawEllipse(plugBrush, plugPen, pStart, 4.5, 4.5);
        dc.DrawEllipse(plugBrush, plugPen, pEnd, 4.5, 4.5);
    }

    private static void DrawText(DrawingContext dc, string text, double x, double y, double size, Color color, double dpi, bool bold = false)
    {
        var ft = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            bold ? UiBold : UiBlack,
            size,
            new SolidColorBrush(color),
            dpi);
        dc.DrawText(ft, new Point(x, y));
    }
}
