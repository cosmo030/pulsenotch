using System;
using System.Diagnostics;
using SD = System.Drawing;
using SWF = System.Windows.Forms;

namespace SpotifyNotch;

/// <summary>Notification-area icon with the settings toggles.</summary>
internal sealed class TrayIcon : IDisposable
{
    readonly SWF.NotifyIcon _icon;
    readonly SD.Icon _glyph;

    public TrayIcon(Settings settings, NotchController controller, SpotifyFeatures spotify, Action exit)
    {
        _glyph = MakeIcon();

        var hover = new SWF.ToolStripMenuItem("Open on hover (top centre)");
        var peek = new SWF.ToolStripMenuItem("Peek when the track changes");
        var viz = new SWF.ToolStripMenuItem("Audio visualizer");
        var blobs = new SWF.ToolStripMenuItem("Background blobs");
        var dots = new SWF.ToolStripMenuItem("Dotted background");
        var reduce = new SWF.ToolStripMenuItem("Reduce motion (still background)");
        var autostart = new SWF.ToolStripMenuItem("Start with Windows");
        var connect = new SWF.ToolStripMenuItem("Connect Spotify (likes, devices, queue)");
        var disconnect = new SWF.ToolStripMenuItem("Disconnect Spotify");

        hover.Click += (_, _) => { settings.HoverEnabled = !settings.HoverEnabled; settings.Save(); controller.ApplySettings(); };
        peek.Click += (_, _) => { settings.PeekOnTrackChange = !settings.PeekOnTrackChange; settings.Save(); };
        viz.Click += (_, _) => { settings.Visualizer = !settings.Visualizer; settings.Save(); };
        blobs.Click += (_, _) => { settings.Blobs = !settings.Blobs; settings.Save(); };
        dots.Click += (_, _) => { settings.DotGrid = !settings.DotGrid; settings.Save(); };
        reduce.Click += (_, _) => { settings.ReduceMotion = !settings.ReduceMotion; settings.Save(); };
        autostart.Click += (_, _) => Autostart.Set(!Autostart.IsEnabled());
        connect.Click += (_, _) => _ = spotify.ConnectAsync();
        disconnect.Click += (_, _) => spotify.Disconnect();

        var menu = new SWF.ContextMenuStrip();
        menu.Items.Add($"Show / hide notch   ({settings.Hotkey})", null, (_, _) => controller.Toggle());
        menu.Items.Add(new SWF.ToolStripSeparator());
        menu.Items.AddRange(new SWF.ToolStripItem[] { connect, disconnect, new SWF.ToolStripSeparator() });
        menu.Items.AddRange(new SWF.ToolStripItem[] { hover, peek, viz, blobs, dots, reduce, autostart });
        menu.Items.Add(new SWF.ToolStripSeparator());
        menu.Items.Add("Open settings file", null, (_, _) =>
        {
            settings.Save();
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{Settings.FilePath}\"") { UseShellExecute = true });
        });
        menu.Items.Add("Exit", null, (_, _) => exit());
        menu.Opening += (_, _) =>
        {
            hover.Checked = settings.HoverEnabled;
            peek.Checked = settings.PeekOnTrackChange;
            viz.Checked = settings.Visualizer;
            blobs.Checked = settings.Blobs;
            dots.Checked = settings.DotGrid;
            reduce.Checked = settings.ReduceMotion;
            autostart.Checked = Autostart.IsEnabled();
            connect.Visible = spotify.Enabled && !spotify.Connected;
            disconnect.Visible = spotify.Enabled && spotify.Connected;
        };

        _icon = new SWF.NotifyIcon
        {
            Icon = _glyph,
            Text = "Spotify Notch",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == SWF.MouseButtons.Left) controller.Toggle(); };
    }

    public void Balloon(string text) => _icon.ShowBalloonTip(4000, "Spotify Notch", text, SWF.ToolTipIcon.Warning);

    static SD.Icon MakeIcon()
    {
        try // the app icon, in the size the tray wants
        {
            using var res = typeof(TrayIcon).Assembly.GetManifestResourceStream("pulsenotch.ico");
            if (res != null) return new SD.Icon(res, SWF.SystemInformation.SmallIconSize);
        }
        catch { /* fall back to the drawn glyph below */ }

        using var bmp = new SD.Bitmap(32, 32);
        using (var g = SD.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(SD.Color.Transparent);
            using var green = new SD.SolidBrush(SD.Color.FromArgb(30, 215, 96));
            using var black = new SD.SolidBrush(SD.Color.Black);
            g.FillEllipse(green, 1, 1, 30, 30);
            g.FillRectangle(black, 8, 0, 16, 8);          // the notch: square top,
            g.FillPie(black, 8, 0, 16, 16, 0, 180);       // rounded bottom
        }
        return SD.Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _glyph.Dispose();
    }
}
