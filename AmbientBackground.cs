using System;
using System.Windows;
using System.Windows.Media;

namespace SpotifyNotch;

/// <summary>
/// Soft, slowly drifting neon "horizon wave": three big radial-gradient blobs along the bottom edge.
/// Radial gradients are already soft, so no blur effect is needed. Drawn in a single OnRender pass.
/// </summary>
public sealed class AmbientBackground : FrameworkElement
{
    // centre x/y, radii, base rotation, drift period (s), phase, drift amplitude (px) - tuned for a 480x180 surface
    static readonly (double cx, double cy, double rx, double ry, double rot, double period, double phase, double amp)[] Blobs =
    {
        (60, 160, 225, 120, -16, 17, 0.0, 38),
        (250, 182, 265, 112, 3, 23, 2.1, 30),
        (430, 156, 215, 122, 16, 19, 4.2, 36),
    };

    readonly RadialGradientBrush[] _brushes = new RadialGradientBrush[Blobs.Length];
    readonly GradientStop[][] _stops = new GradientStop[Blobs.Length][];
    readonly Color[] _applied = new Color[Blobs.Length];
    double _time;

    public AmbientBackground()
    {
        for (int i = 0; i < Blobs.Length; i++)
        {
            var s0 = new GradientStop(Colors.Transparent, 0.0);
            var s1 = new GradientStop(Colors.Transparent, 0.55);
            var s2 = new GradientStop(Colors.Transparent, 1.0);
            _stops[i] = new[] { s0, s1, s2 };
            _brushes[i] = new RadialGradientBrush(new GradientStopCollection { s0, s1, s2 })
            {
                Center = new Point(0.5, 0.5),
                GradientOrigin = new Point(0.5, 0.5),
                RadiusX = 0.5,
                RadiusY = 0.5,
            };
        }
        IsHitTestVisible = false;
    }

    /// <summary>Advance the drift to <paramref name="seconds"/> and re-tint the blobs.</summary>
    public void Update(double seconds, Color[] colors)
    {
        _time = seconds;
        for (int i = 0; i < Blobs.Length; i++)
        {
            if (colors[i] == _applied[i]) continue;
            _applied[i] = colors[i];
            var c = colors[i];
            _stops[i][0].Color = Color.FromArgb(0xFF, c.R, c.G, c.B);
            _stops[i][1].Color = Color.FromArgb(0xA6, c.R, c.G, c.B);
            _stops[i][2].Color = Color.FromArgb(0x00, c.R, c.G, c.B);
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        for (int i = 0; i < Blobs.Length; i++)
        {
            var b = Blobs[i];
            double ph = _time * 2 * Math.PI / b.period + b.phase;
            double x = b.cx + b.amp * Math.Sin(ph);
            double y = b.cy + b.amp * 0.45 * Math.Cos(ph * 0.8);
            double scale = 1 + 0.12 * Math.Sin(ph * 0.7 + 1);
            double rot = b.rot + 6 * Math.Sin(ph * 0.5);

            dc.PushTransform(new RotateTransform(rot, x, y));
            dc.DrawEllipse(_brushes[i], null, new Point(x, y), b.rx * scale, b.ry * scale);
            dc.Pop();
        }
    }
}
