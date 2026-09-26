using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace EasyRadioLink.Client.UI.Controls;

/// <summary>
///     A seven-segment LCD / VFD read-out drawn with WPF geometry (no font files).
///     <list type="bullet">
///         <item>
///             <see cref="Text" /> is shown right aligned in <see cref="DigitCount" /> cells. Supported: digits, space,
///             '-', '_' and the letters that seven segments can show (A b C c d E F G H h I J L n o P r S t U u y); a '.'
///             lights the decimal point of the preceding cell. Unknown characters are blank.
///         </item>
///         <item>Unlit segments are drawn faintly (<see cref="UnlitBrush" />) like on a real LCD.</item>
///         <item>Lit segments glow (<see cref="GlowColor" />, <see cref="GlowRadius" />; 0 = no glow).</item>
///         <item><see cref="MarkedDigit" /> (0 = left cell) is underlined, e.g. the digit a tuning step changes.</item>
///     </list>
///     The control has a fixed natural size computed from the digit geometry - scale it with a Viewbox.
/// </summary>
public class SevenSegmentDisplay : FrameworkElement
{
    // segment bits: a = top, b = upper right, c = lower right, d = bottom, e = lower left, f = upper left, g = middle
    private const int A = 1, B = 2, C = 4, D = 8, E = 16, F = 32, G = 64;

    private static readonly Dictionary<char, int> Segments = new()
    {
        { '0', A | B | C | D | E | F },
        { '1', B | C },
        { '2', A | B | D | E | G },
        { '3', A | B | C | D | G },
        { '4', B | C | F | G },
        { '5', A | C | D | F | G },
        { '6', A | C | D | E | F | G },
        { '7', A | B | C },
        { '8', A | B | C | D | E | F | G },
        { '9', A | B | C | D | F | G },
        { '-', G },
        { '_', D },
        { ' ', 0 },
        { 'A', A | B | C | E | F | G },
        { 'b', C | D | E | F | G },
        { 'C', A | D | E | F },
        { 'c', D | E | G },
        { 'd', B | C | D | E | G },
        { 'E', A | D | E | F | G },
        { 'F', A | E | F | G },
        { 'G', A | C | D | E | F },
        { 'H', B | C | E | F | G },
        { 'h', C | E | F | G },
        { 'I', B | C },
        { 'J', B | C | D | E },
        { 'L', D | E | F },
        { 'n', C | E | G },
        { 'o', C | D | E | G },
        { 'P', A | B | E | F | G },
        { 'r', E | G },
        { 'S', A | C | D | F | G },
        { 't', D | E | F | G },
        { 'U', B | C | D | E | F },
        { 'u', C | D | E },
        { 'y', B | C | D | F | G }
    };

    public static readonly DependencyProperty TextProperty = Register(nameof(Text), typeof(string), "");

    public static readonly DependencyProperty DigitCountProperty = Register(nameof(DigitCount), typeof(int), 6);

    public static readonly DependencyProperty DigitWidthProperty = Register(nameof(DigitWidth), typeof(double), 24d);

    public static readonly DependencyProperty DigitHeightProperty = Register(nameof(DigitHeight), typeof(double), 44d);

    public static readonly DependencyProperty SegmentThicknessProperty =
        Register(nameof(SegmentThickness), typeof(double), 5d);

    public static readonly DependencyProperty DigitSpacingProperty = Register(nameof(DigitSpacing), typeof(double), 9d);

    public static readonly DependencyProperty SkewAngleProperty = Register(nameof(SkewAngle), typeof(double), 6d);

    public static readonly DependencyProperty MarkedDigitProperty = Register(nameof(MarkedDigit), typeof(int), -1);

    public static readonly DependencyProperty LitBrushProperty =
        Register(nameof(LitBrush), typeof(Brush), Brushes.Orange);

    public static readonly DependencyProperty UnlitBrushProperty =
        Register(nameof(UnlitBrush), typeof(Brush), null);

    public static readonly DependencyProperty MarkerBrushProperty =
        Register(nameof(MarkerBrush), typeof(Brush), null);

    public static readonly DependencyProperty GlowColorProperty =
        Register(nameof(GlowColor), typeof(Color), Colors.Orange);

    public static readonly DependencyProperty GlowRadiusProperty = Register(nameof(GlowRadius), typeof(double), 10d);

    public static readonly DependencyProperty ShowUnlitDecimalPointsProperty =
        Register(nameof(ShowUnlitDecimalPoints), typeof(bool), false);

    // unlit segments and decimal points
    private readonly DrawingVisual _unlitVisual = new();

    // lit segments with the glow effect
    private readonly DrawingVisual _litVisual = new();

    private readonly VisualCollection _visuals;

    public SevenSegmentDisplay()
    {
        _visuals = new VisualCollection(this) { _unlitVisual, _litVisual };
        SnapsToDevicePixels = false;
        Redraw();
    }

    /// <summary>Text to show, e.g. "027.185" (right aligned, '.' = decimal point of the preceding cell).</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>Number of digit cells.</summary>
    public int DigitCount
    {
        get => (int)GetValue(DigitCountProperty);
        set => SetValue(DigitCountProperty, value);
    }

    public double DigitWidth
    {
        get => (double)GetValue(DigitWidthProperty);
        set => SetValue(DigitWidthProperty, value);
    }

    public double DigitHeight
    {
        get => (double)GetValue(DigitHeightProperty);
        set => SetValue(DigitHeightProperty, value);
    }

    public double SegmentThickness
    {
        get => (double)GetValue(SegmentThicknessProperty);
        set => SetValue(SegmentThicknessProperty, value);
    }

    /// <summary>Space between two cells (the decimal point sits in it).</summary>
    public double DigitSpacing
    {
        get => (double)GetValue(DigitSpacingProperty);
        set => SetValue(DigitSpacingProperty, value);
    }

    /// <summary>Italic slant of the digits in degrees.</summary>
    public double SkewAngle
    {
        get => (double)GetValue(SkewAngleProperty);
        set => SetValue(SkewAngleProperty, value);
    }

    /// <summary>Cell (0 = left) that gets an underline, -1 = none.</summary>
    public int MarkedDigit
    {
        get => (int)GetValue(MarkedDigitProperty);
        set => SetValue(MarkedDigitProperty, value);
    }

    public Brush LitBrush
    {
        get => (Brush)GetValue(LitBrushProperty);
        set => SetValue(LitBrushProperty, value);
    }

    /// <summary>Brush of the unlit segments ("ghost" segments), null = not drawn.</summary>
    public Brush UnlitBrush
    {
        get => (Brush)GetValue(UnlitBrushProperty);
        set => SetValue(UnlitBrushProperty, value);
    }

    /// <summary>Brush of the <see cref="MarkedDigit" /> underline, null = <see cref="LitBrush" />.</summary>
    public Brush MarkerBrush
    {
        get => (Brush)GetValue(MarkerBrushProperty);
        set => SetValue(MarkerBrushProperty, value);
    }

    public Color GlowColor
    {
        get => (Color)GetValue(GlowColorProperty);
        set => SetValue(GlowColorProperty, value);
    }

    /// <summary>Blur radius of the glow around lit segments, 0 = no glow.</summary>
    public double GlowRadius
    {
        get => (double)GetValue(GlowRadiusProperty);
        set => SetValue(GlowRadiusProperty, value);
    }

    /// <summary>Draw the decimal points of every cell faintly (false: a decimal point is only drawn when lit).</summary>
    public bool ShowUnlitDecimalPoints
    {
        get => (bool)GetValue(ShowUnlitDecimalPointsProperty);
        set => SetValue(ShowUnlitDecimalPointsProperty, value);
    }

    protected override int VisualChildrenCount => _visuals.Count;

    // space below the digits for the marker (always reserved so the layout does not jump)
    private double MarkerGap => Math.Max(1.5, SegmentThickness * 0.7);
    private double MarkerThickness => Math.Max(1, SegmentThickness * 0.55);

    private static DependencyProperty Register(string name, Type type, object defaultValue)
    {
        return DependencyProperty.Register(name, type, typeof(SevenSegmentDisplay),
            new FrameworkPropertyMetadata(defaultValue, FrameworkPropertyMetadataOptions.AffectsMeasure,
                (d, _) => ((SevenSegmentDisplay)d).Redraw()));
    }

    protected override Visual GetVisualChild(int index)
    {
        return _visuals[index];
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var count = Math.Max(1, DigitCount);
        var slant = DigitHeight * Math.Tan(SkewAngle * Math.PI / 180);

        // the last cell keeps room for its decimal point
        var width = count * DigitWidth + count * DigitSpacing + Math.Abs(slant);
        var height = DigitHeight + MarkerGap + MarkerThickness;

        return new Size(width, height);
    }

    /// <summary>Cells to draw: segment mask and decimal point per cell, right aligned.</summary>
    private static List<(int segments, bool point)> ParseCells(string text, int count)
    {
        var cells = new List<(int segments, bool point)>();

        foreach (var character in text ?? "")
        {
            if (character == '.' || character == ',')
            {
                if (cells.Count == 0) cells.Add((0, true));
                else cells[^1] = (cells[^1].segments, true);
                continue;
            }

            cells.Add((Segments.TryGetValue(character, out var mask) ? mask : 0, false));
        }

        // right aligned: pad on the left, drop what does not fit on the left
        while (cells.Count < count) cells.Insert(0, (0, false));
        if (cells.Count > count) cells.RemoveRange(0, cells.Count - count);

        return cells;
    }

    private void Redraw()
    {
        var count = Math.Max(1, DigitCount);
        var width = Math.Max(1, DigitWidth);
        var height = Math.Max(1, DigitHeight);
        var thickness = Math.Clamp(SegmentThickness, 0.5, Math.Min(width, height) / 3);
        var spacing = Math.Max(0, DigitSpacing);
        var skew = Math.Tan(SkewAngle * Math.PI / 180);
        var slant = height * skew;
        var originX = slant < 0 ? -slant : 0;

        var cells = ParseCells(Text, count);
        var unlitPoints = ShowUnlitDecimalPoints;

        var lit = new StreamGeometry();
        var unlit = new StreamGeometry();

        using (var litContext = lit.Open())
        using (var unlitContext = unlit.Open())
        {
            for (var i = 0; i < count; i++)
            {
                var x = originX + i * (width + spacing);
                var (mask, point) = cells[i];

                AddDigit(litContext, unlitContext, mask, x, width, height, thickness, skew);

                // decimal point: a small square in the gap after the cell, on the base line
                if (!point && !unlitPoints) continue;

                var size = thickness * 1.05;
                var pointX = x + width + (spacing - size) / 2;
                AddPolygon(point ? litContext : unlitContext, skew, height,
                    new Point(pointX, height - size), new Point(pointX + size, height - size),
                    new Point(pointX + size, height), new Point(pointX, height));
            }
        }

        lit.Freeze();
        unlit.Freeze();

        using (var context = _unlitVisual.RenderOpen())
        {
            if (UnlitBrush != null) context.DrawGeometry(UnlitBrush, null, unlit);
        }

        using (var context = _litVisual.RenderOpen())
        {
            context.DrawGeometry(LitBrush, null, lit);

            var marked = MarkedDigit;
            if (marked >= 0 && marked < count)
            {
                var x = originX + marked * (width + spacing);
                var top = height + MarkerGap;
                var inset = thickness * 0.4;

                var marker = new StreamGeometry();
                using (var markerContext = marker.Open())
                {
                    AddPolygon(markerContext, skew, height,
                        new Point(x + inset, top), new Point(x + width - inset, top),
                        new Point(x + width - inset, top + MarkerThickness), new Point(x + inset, top + MarkerThickness));
                }

                marker.Freeze();
                context.DrawGeometry(MarkerBrush ?? LitBrush, null, marker);
            }
        }

        _litVisual.Effect = GlowRadius > 0
            ? new DropShadowEffect
            {
                Color = GlowColor,
                BlurRadius = GlowRadius,
                ShadowDepth = 0,
                Opacity = 0.85,
                RenderingBias = RenderingBias.Quality
            }
            : null;
    }

    private static void AddDigit(StreamGeometryContext lit, StreamGeometryContext unlit, int mask, double x,
        double width, double height, double thickness, double skew)
    {
        var half = thickness / 2;
        var gap = thickness * 0.16;
        var left = x + half;
        var right = x + width - half;
        var top = half;
        var middle = height / 2;
        var bottom = height - half;

        AddHorizontal(Pick(mask, A, lit, unlit), skew, height, left, right, top, half, gap);
        AddVertical(Pick(mask, B, lit, unlit), skew, height, right, top, middle, half, gap);
        AddVertical(Pick(mask, C, lit, unlit), skew, height, right, middle, bottom, half, gap);
        AddHorizontal(Pick(mask, D, lit, unlit), skew, height, left, right, bottom, half, gap);
        AddVertical(Pick(mask, E, lit, unlit), skew, height, left, middle, bottom, half, gap);
        AddVertical(Pick(mask, F, lit, unlit), skew, height, left, top, middle, half, gap);
        AddHorizontal(Pick(mask, G, lit, unlit), skew, height, left, right, middle, half, gap);
    }

    private static StreamGeometryContext Pick(int mask, int segment, StreamGeometryContext lit,
        StreamGeometryContext unlit)
    {
        return (mask & segment) != 0 ? lit : unlit;
    }

    // hexagonal segment between (x1, y) and (x2, y)
    private static void AddHorizontal(StreamGeometryContext context, double skew, double height, double x1,
        double x2, double y, double half, double gap)
    {
        AddPolygon(context, skew, height,
            new Point(x1 + gap, y),
            new Point(x1 + gap + half, y - half),
            new Point(x2 - gap - half, y - half),
            new Point(x2 - gap, y),
            new Point(x2 - gap - half, y + half),
            new Point(x1 + gap + half, y + half));
    }

    // hexagonal segment between (x, y1) and (x, y2)
    private static void AddVertical(StreamGeometryContext context, double skew, double height, double x, double y1,
        double y2, double half, double gap)
    {
        AddPolygon(context, skew, height,
            new Point(x, y1 + gap),
            new Point(x + half, y1 + gap + half),
            new Point(x + half, y2 - gap - half),
            new Point(x, y2 - gap),
            new Point(x - half, y2 - gap - half),
            new Point(x - half, y1 + gap + half));
    }

    /// <summary>Adds a closed polygon, slanted by <paramref name="skew" /> around the base line.</summary>
    private static void AddPolygon(StreamGeometryContext context, double skew, double height, params Point[] points)
    {
        context.BeginFigure(Slant(points[0], skew, height), true, true);
        for (var i = 1; i < points.Length; i++) context.LineTo(Slant(points[i], skew, height), false, true);
    }

    private static Point Slant(Point point, double skew, double height)
    {
        return new Point(point.X + (height - point.Y) * skew, point.Y);
    }
}
