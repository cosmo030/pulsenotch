using System;
using System.Windows;
using System.Windows.Media;

namespace SpotifyNotch;

/// <summary>Geometry builders for the notch silhouette.</summary>
internal static class Geo
{
    /// <summary>
    /// Full notch: flush with the top edge, concave "ears" where it meets the edge,
    /// rounded bottom corners. Coordinates are window-local, <paramref name="cx"/> is the centre line.
    /// </summary>
    public static Geometry Notch(double cx, double w, double h, double ear, double radius)
    {
        double L = cx - w / 2, R = cx + w / 2;
        double e = Math.Min(ear, h * 0.5);
        double r = Math.Min(Math.Min(radius, h * 0.5), w * 0.5);
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(L - e, -1), true, true);
            c.LineTo(new Point(L - e, 0), true, false);
            c.ArcTo(new Point(L, e), new Size(e, e), 0, false, SweepDirection.Clockwise, true, false);
            c.LineTo(new Point(L, h - r), true, false);
            c.ArcTo(new Point(L + r, h), new Size(r, r), 0, false, SweepDirection.Counterclockwise, true, false);
            c.LineTo(new Point(R - r, h), true, false);
            c.ArcTo(new Point(R, h - r), new Size(r, r), 0, false, SweepDirection.Counterclockwise, true, false);
            c.LineTo(new Point(R, e), true, false);
            c.ArcTo(new Point(R + e, 0), new Size(e, e), 0, false, SweepDirection.Clockwise, true, false);
            c.LineTo(new Point(R + e, -1), true, false);
        }
        g.Freeze();
        return g;
    }

    /// <summary>Body only (square top, rounded bottom) in the local space of a child starting at x0.</summary>
    public static Geometry Body(double x0, double w, double h, double radius)
    {
        double r = Math.Min(Math.Min(radius, h * 0.5), w * 0.5);
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(x0, -1), true, true);
            c.LineTo(new Point(x0 + w, -1), true, false);
            c.LineTo(new Point(x0 + w, h - r), true, false);
            c.ArcTo(new Point(x0 + w - r, h), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
            c.LineTo(new Point(x0 + r, h), true, false);
            c.ArcTo(new Point(x0, h - r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        return g;
    }
}
