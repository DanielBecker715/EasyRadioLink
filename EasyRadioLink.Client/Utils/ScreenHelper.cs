using System;
using System.Windows;
using System.Windows.Forms;
using Rectangle = System.Drawing.Rectangle;

namespace EasyRadioLink.Client.Utils;

/// <summary>
///     Checks saved window positions (WPF units) against the monitors that are connected now.
///     The virtual screen is only the bounding box of all monitors, so with monitors of different sizes it contains
///     areas no monitor shows.
/// </summary>
public static class ScreenHelper
{
    // part of the window's top edge (title bar / header) that must be on a monitor to count as visible
    private const double MinVisibleWidth = 100;
    private const double MinVisibleHeight = 20;
    private const double TitleStripHeight = 30;

    /// <summary>
    ///     True if the top strip (title bar) of a window at <paramref name="left" />/<paramref name="top" /> with
    ///     <paramref name="width" /> is at least 100 x 20 units on one monitor, so it can be dragged.
    /// </summary>
    public static bool IsTitleVisible(double left, double top, double width)
    {
        if (!double.IsFinite(left) || !double.IsFinite(top) || !double.IsFinite(width) || width <= 0) return false;

        var scale = PixelsPerUnit();
        var strip = new Rectangle(ToPixels(left, scale), ToPixels(top, scale), ToPixels(width, scale),
            ToPixels(TitleStripHeight, scale));

        foreach (var screen in Screen.AllScreens)
        {
            var visible = Rectangle.Intersect(screen.Bounds, strip);
            if (visible.Width >= MinVisibleWidth * scale && visible.Height >= MinVisibleHeight * scale) return true;
        }

        return false;
    }

    /// <summary>Working area (WPF units) of the monitor that contains the point, or the nearest monitor.</summary>
    public static Rect WorkingAreaAt(double left, double top)
    {
        var scale = PixelsPerUnit();

        var point = double.IsFinite(left) && double.IsFinite(top)
            ? new System.Drawing.Point(ToPixels(left, scale), ToPixels(top, scale))
            : System.Drawing.Point.Empty;

        var area = Screen.FromPoint(point).WorkingArea;

        return new Rect(area.Left / scale, area.Top / scale, area.Width / scale, area.Height / scale);
    }

    /// <summary>Device pixels per WPF unit (the client is system DPI aware: one factor for all monitors).</summary>
    private static double PixelsPerUnit()
    {
        var primary = Screen.PrimaryScreen;
        var units = SystemParameters.PrimaryScreenWidth;

        if (primary == null || !(units > 0) || primary.Bounds.Width <= 0) return 1.0;

        return primary.Bounds.Width / units;
    }

    private static int ToPixels(double value, double scale)
    {
        return (int)Math.Round(Math.Clamp(value * scale, int.MinValue / 2.0, int.MaxValue / 2.0));
    }
}
