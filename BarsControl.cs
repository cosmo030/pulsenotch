using System;
using System.Windows;
using System.Windows.Media;

namespace SpotifyNotch;

/// <summary>Equaliser bars driven by a shared level array (0..1).</summary>
public sealed class BarsControl : FrameworkElement
{
    public float[]? Levels { get; set; }
    public Brush? Fill { get; set; }
    public int Count { get; set; } = 28;
    public bool Centered { get; set; }

    protected override void OnRender(DrawingContext dc)
    {
        var levels = Levels;
        var fill = Fill;
        if (levels == null || fill == null || levels.Length == 0) return;

        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        double slot = w / Count;
        double bw = Math.Max(2, slot * 0.58);
        for (int i = 0; i < Count; i++)
        {
            int idx = Math.Min(levels.Length - 1, (int)((i + 0.5) * levels.Length / Count));
            double level = levels[idx];
            bool idle = level < 0.04;
            double bh = idle ? 2 : Math.Max(bw, level * h); // at rest: a thin faint line, not a dot
            double x = i * slot + (slot - bw) / 2;
            double y = Centered ? (h - bh) / 2 : h - bh;
            double rad = Math.Min(bw, bh) / 2;
            if (idle) dc.PushOpacity(0.35);
            dc.DrawRoundedRectangle(fill, null, new Rect(x, y, bw, bh), rad, rad);
            if (idle) dc.Pop();
        }
    }
}
