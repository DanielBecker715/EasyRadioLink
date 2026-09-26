using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace EasyRadioLink.Client.UI.Controls;

/// <summary>
///     A round, skeuomorphic knob drawn with geometry: soft shadow, knurled skirt, domed cap with a fixed highlight and
///     an indicator dot. The skirt notches and the dot turn with <see cref="Angle" /> (degrees, clockwise, 0 = dot at
///     the top); the light stays where it is.
/// </summary>
public abstract class RotaryKnob : FrameworkElement
{
    public static readonly DependencyProperty AngleProperty = DependencyProperty.Register(nameof(Angle),
        typeof(double), typeof(RotaryKnob),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IndicatorBrushProperty = DependencyProperty.Register(
        nameof(IndicatorBrush), typeof(Brush), typeof(RotaryKnob),
        new FrameworkPropertyMetadata(Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xB2, 0x3E))),
            FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty NotchCountProperty = DependencyProperty.Register(nameof(NotchCount),
        typeof(int), typeof(RotaryKnob),
        new FrameworkPropertyMetadata(30, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush ShadowBrush = Freeze(new RadialGradientBrush(
        new GradientStopCollection
        {
            new(Color.FromArgb(0x90, 0, 0, 0), 0.0),
            new(Color.FromArgb(0x60, 0, 0, 0), 0.72),
            new(Color.FromArgb(0x00, 0, 0, 0), 1.0)
        }));

    private static readonly Brush SkirtBrush = Freeze(new LinearGradientBrush(
        new GradientStopCollection
        {
            new(Color.FromRgb(0x5A, 0x5F, 0x67), 0.0),
            new(Color.FromRgb(0x33, 0x36, 0x3B), 0.5),
            new(Color.FromRgb(0x1A, 0x1C, 0x1F), 1.0)
        }, new Point(0.5, 0), new Point(0.5, 1)));

    private static readonly Brush CapBrush = Freeze(new RadialGradientBrush(
        new GradientStopCollection
        {
            new(Color.FromRgb(0x6A, 0x70, 0x79), 0.0),
            new(Color.FromRgb(0x3A, 0x3E, 0x44), 0.55),
            new(Color.FromRgb(0x22, 0x24, 0x28), 1.0)
        })
    {
        GradientOrigin = new Point(0.38, 0.28),
        Center = new Point(0.45, 0.38),
        RadiusX = 0.75,
        RadiusY = 0.75
    });

    // bevel of the cap: light on the upper edge, dark on the lower edge
    private static readonly Brush CapRimBrush = Freeze(new LinearGradientBrush(
        new GradientStopCollection
        {
            new(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF), 0.0),
            new(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF), 0.45),
            new(Color.FromArgb(0x80, 0x00, 0x00, 0x00), 1.0)
        }, new Point(0.5, 0), new Point(0.5, 1)));

    // soft reflection of the light on the upper half of the cap
    private static readonly Brush SheenBrush = Freeze(new RadialGradientBrush(
        new GradientStopCollection
        {
            new(Color.FromArgb(0x2C, 0xFF, 0xFF, 0xFF), 0.0),
            new(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF), 0.55),
            new(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1.0)
        })
    {
        GradientOrigin = new Point(0.42, 0.18),
        Center = new Point(0.45, 0.25),
        RadiusX = 0.62,
        RadiusY = 0.5
    });

    private static readonly Color GrooveColor = Color.FromArgb(0xC0, 0x08, 0x09, 0x0A);
    private static readonly Color RidgeColor = Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF);

    private Point _lastPointer;
    private double _lastPointerAngle;

    protected RotaryKnob()
    {
        Cursor = Cursors.Hand;
        Focusable = false;
        SnapsToDevicePixels = false;
    }

    /// <summary>Rotation in degrees, clockwise; 0 = indicator at the top.</summary>
    public double Angle
    {
        get => (double)GetValue(AngleProperty);
        set => SetValue(AngleProperty, value);
    }

    public Brush IndicatorBrush
    {
        get => (Brush)GetValue(IndicatorBrushProperty);
        set => SetValue(IndicatorBrushProperty, value);
    }

    /// <summary>Number of grip notches on the skirt.</summary>
    public int NotchCount
    {
        get => (int)GetValue(NotchCountProperty);
        set => SetValue(NotchCountProperty, value);
    }

    /// <summary>Radius of the knob body relative to the available radius (room for a scale around it).</summary>
    protected virtual double BodyScale => 1.0;

    protected bool IsDragging { get; private set; }

    protected static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    protected Point Center => new(ActualWidth / 2, ActualHeight / 2);

    protected double OuterRadius => Math.Max(0, Math.Min(ActualWidth, ActualHeight) / 2);

    protected override void OnRender(DrawingContext dc)
    {
        var radius = OuterRadius;
        if (radius < 2) return;

        var center = Center;

        // transparent background so the whole square is hit-testable (drag starts anywhere on the knob)
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));

        RenderScale(dc, center, radius);

        if (!IsEnabled) dc.PushOpacity(0.55);

        var r = radius * BodyScale;

        // soft shadow below the knob
        dc.DrawEllipse(ShadowBrush, null, new Point(center.X, center.Y + r * 0.07), r * 1.0, r * 1.0);

        // skirt with knurled grip
        var skirt = r * 0.93;
        dc.DrawEllipse(SkirtBrush, Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x0C, 0x0D, 0x0F)), 1)),
            center, skirt, skirt);

        dc.PushTransform(new RotateTransform(Angle, center.X, center.Y));

        var notches = Math.Max(0, NotchCount);
        if (notches > 0)
        {
            var groove = Freeze(new Pen(new SolidColorBrush(GrooveColor), Math.Max(0.8, r * 0.035))
                { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
            var ridge = Freeze(new Pen(new SolidColorBrush(RidgeColor), Math.Max(0.5, r * 0.018))
                { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });

            var inner = r * 0.80;
            var outer = r * 0.90;
            for (var i = 0; i < notches; i++)
            {
                var angle = i * 2 * Math.PI / notches;
                var sin = Math.Sin(angle);
                var cos = Math.Cos(angle);
                dc.DrawLine(groove, new Point(center.X + sin * inner, center.Y - cos * inner),
                    new Point(center.X + sin * outer, center.Y - cos * outer));

                // a thin highlight next to each groove makes the ridges catch the light
                var offset = Math.PI / notches * 0.55;
                var sin2 = Math.Sin(angle + offset);
                var cos2 = Math.Cos(angle + offset);
                dc.DrawLine(ridge, new Point(center.X + sin2 * inner, center.Y - cos2 * inner),
                    new Point(center.X + sin2 * outer, center.Y - cos2 * outer));
            }
        }

        dc.Pop();

        // domed cap: the light comes from the top left and does not turn
        var cap = r * 0.74;
        dc.DrawEllipse(CapBrush, Freeze(new Pen(CapRimBrush, Math.Max(0.8, r * 0.025))), center, cap, cap);
        dc.DrawEllipse(SheenBrush, null, center, cap * 0.97, cap * 0.97);

        // indicator dot with a soft glow
        dc.PushTransform(new RotateTransform(Angle, center.X, center.Y));

        var dot = new Point(center.X, center.Y - cap * 0.70);
        var dotRadius = Math.Max(1.2, r * 0.065);
        var indicator = IsEnabled ? IndicatorBrush : Brushes.DimGray;

        if (IsEnabled && indicator is SolidColorBrush solid)
        {
            var glow = new RadialGradientBrush(
                new GradientStopCollection
                {
                    new(Color.FromArgb(0x90, solid.Color.R, solid.Color.G, solid.Color.B), 0.0),
                    new(Color.FromArgb(0x00, solid.Color.R, solid.Color.G, solid.Color.B), 1.0)
                });
            glow.Freeze();
            dc.DrawEllipse(glow, null, dot, dotRadius * 2.6, dotRadius * 2.6);
        }

        dc.DrawEllipse(indicator, Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x90, 0, 0, 0)), 0.6)), dot,
            dotRadius, dotRadius);

        dc.Pop();

        if (!IsEnabled) dc.Pop();
    }

    /// <summary>Optional scale drawn behind the knob body (see <see cref="BodyScale" />).</summary>
    protected virtual void RenderScale(DrawingContext dc, Point center, double radius)
    {
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (e.Property == IsEnabledProperty) InvalidateVisual();
    }

    #region Dragging

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        // the knob must not start a window drag
        e.Handled = true;

        if (!IsEnabled || !CaptureMouse()) return;

        IsDragging = true;
        _lastPointer = e.GetPosition(this);
        _lastPointerAngle = PointerAngle(_lastPointer);
        OnDragStarted();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (!IsDragging) return;

        var position = e.GetPosition(this);
        var movement = position - _lastPointer;

        // close to the centre the angle jumps around - only follow the pointer once it is away from it
        var distance = (position - Center).Length;
        var angle = PointerAngle(position);
        var deltaAngle = 0d;

        if (distance > OuterRadius * 0.18)
        {
            deltaAngle = angle - _lastPointerAngle;
            if (deltaAngle > 180) deltaAngle -= 360;
            if (deltaAngle < -180) deltaAngle += 360;
        }

        _lastPointerAngle = angle;
        _lastPointer = position;

        OnDrag(deltaAngle, movement);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        if (!IsDragging) return;

        e.Handled = true;
        ReleaseMouseCapture();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);

        if (!IsDragging) return;

        IsDragging = false;
        OnDragCompleted();
    }

    /// <summary>Angle of <paramref name="position" /> around the centre in degrees, clockwise, 0 = top.</summary>
    private double PointerAngle(Point position)
    {
        var vector = position - Center;
        return Math.Atan2(vector.X, -vector.Y) * 180 / Math.PI;
    }

    protected virtual void OnDragStarted()
    {
    }

    /// <param name="deltaAngle">clockwise rotation of the pointer around the centre in degrees since the last call</param>
    /// <param name="movement">pointer movement since the last call</param>
    protected abstract void OnDrag(double deltaAngle, Vector movement);

    protected virtual void OnDragCompleted()
    {
    }

    #endregion
}

/// <summary>
///     The endless tuning knob: 30 detents per revolution (<see cref="RotaryKnob.NotchCount" />), one detent = one
///     tuning step. Turn it by dragging in a circle or with the mouse wheel; <see cref="Tuned" /> reports the steps
///     (positive = clockwise = up).
/// </summary>
public class TuningKnob : RotaryKnob
{
    // rotation not yet reported as a detent
    private double _pending;

    /// <summary>Raised with the number of detents turned (positive = clockwise).</summary>
    public event EventHandler<int> Tuned;

    private double DetentAngle => 360.0 / Math.Max(1, NotchCount);

    /// <summary>Turns the knob by <paramref name="detents" /> without raising <see cref="Tuned" /> (visual feedback).</summary>
    public void Nudge(int detents)
    {
        Angle = Normalise(Angle + detents * DetentAngle);
    }

    protected override void OnDrag(double deltaAngle, Vector movement)
    {
        if (deltaAngle == 0) return;

        Angle = Normalise(Angle + deltaAngle);
        _pending += deltaAngle;

        var detents = (int)Math.Truncate(_pending / DetentAngle);
        if (detents == 0) return;

        _pending -= detents * DetentAngle;
        Tuned?.Invoke(this, detents);
    }

    protected override void OnDragCompleted()
    {
        // the rest of the turn: half a detent or more counts as one more detent, less snaps back - the knob always
        // settles on the detent that matches the reported steps
        var detents = Math.Abs(_pending) >= DetentAngle / 2 ? Math.Sign(_pending) : 0;
        var settled = Angle - _pending + detents * DetentAngle;

        _pending = 0;
        Angle = Normalise(Math.Round(settled / DetentAngle) * DetentAngle);

        if (detents != 0) Tuned?.Invoke(this, detents);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);

        if (!IsEnabled) return;

        e.Handled = true;
        var detents = WheelDetents(e.Delta);
        if (detents == 0) return;

        Nudge(detents);
        Tuned?.Invoke(this, detents);
    }

    /// <summary>Detents for a mouse wheel delta: one per notch (120), at least one.</summary>
    public static int WheelDetents(int delta)
    {
        if (delta == 0) return 0;

        var detents = delta / Mouse.MouseWheelDeltaForOneLine;
        return detents != 0 ? detents : Math.Sign(delta);
    }

    private static double Normalise(double angle)
    {
        angle %= 360;
        return angle < 0 ? angle + 360 : angle;
    }
}

/// <summary>
///     A small volume knob: <see cref="Value" /> 0..1 over a 270° sweep with a lit arc. Drag up / right to turn it up
///     (or around the centre), mouse wheel = 5 % per notch.
/// </summary>
public class VolumeKnob : RotaryKnob
{
    public const double Sweep = 270;

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value),
        typeof(double), typeof(VolumeKnob),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, _) => ((VolumeKnob)d).UpdateAngle(), (_, value) => Coerce((double)value)));

    public static readonly DependencyProperty ArcBrushProperty = DependencyProperty.Register(nameof(ArcBrush),
        typeof(Brush), typeof(VolumeKnob),
        new FrameworkPropertyMetadata(Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xB2, 0x3E))),
            FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush TrackBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xFF, 0x12, 0x13, 0x15)));

    public VolumeKnob()
    {
        NotchCount = 18;
        UpdateAngle();
    }

    /// <summary>Volume 0..1.</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public Brush ArcBrush
    {
        get => (Brush)GetValue(ArcBrushProperty);
        set => SetValue(ArcBrushProperty, value);
    }

    /// <summary>Raised when the user changed <see cref="Value" /> (drag or wheel).</summary>
    public event EventHandler ValueChangedByUser;

    protected override double BodyScale => 0.74;

    private static object Coerce(double value)
    {
        return double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0d;
    }

    private void UpdateAngle()
    {
        Angle = -Sweep / 2 + Sweep * Value;
    }

    private void ChangeValue(double delta)
    {
        var old = Value;
        Value = old + delta;
        if (Math.Abs(Value - old) > 1e-9) ValueChangedByUser?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnDrag(double deltaAngle, Vector movement)
    {
        // up / right turns it up; circling around the centre works as well
        var linear = (movement.X - movement.Y) / 140.0;
        var circular = deltaAngle / Sweep;

        var delta = Math.Abs(circular) > Math.Abs(linear) ? circular : linear;
        if (delta != 0) ChangeValue(delta);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);

        if (!IsEnabled) return;

        e.Handled = true;
        ChangeValue(TuningKnob.WheelDetents(e.Delta) * 0.05);
    }

    protected override void RenderScale(DrawingContext dc, Point center, double radius)
    {
        var arcRadius = radius * 0.90;
        var thickness = Math.Max(1.2, radius * 0.12);

        var track = Freeze(new Pen(TrackBrush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
        dc.DrawGeometry(null, track, Arc(center, arcRadius, -Sweep / 2, Sweep / 2));

        if (Value <= 0.001) return;

        var brush = IsEnabled ? ArcBrush : Brushes.DimGray;
        var lit = Freeze(new Pen(brush, thickness * 0.62) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
        dc.DrawGeometry(null, lit, Arc(center, arcRadius, -Sweep / 2, -Sweep / 2 + Sweep * Value));
    }

    /// <summary>Arc from <paramref name="fromAngle" /> to <paramref name="toAngle" /> (degrees, clockwise, 0 = top).</summary>
    private static Geometry Arc(Point center, double radius, double fromAngle, double toAngle)
    {
        static Point On(Point c, double r, double degrees)
        {
            var radians = degrees * Math.PI / 180;
            return new Point(c.X + Math.Sin(radians) * r, c.Y - Math.Cos(radians) * r);
        }

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(On(center, radius, fromAngle), false, false);
            context.ArcTo(On(center, radius, toAngle), new Size(radius, radius), 0, toAngle - fromAngle > 180,
                SweepDirection.Clockwise, true, false);
        }

        geometry.Freeze();
        return geometry;
    }
}
