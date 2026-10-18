using System;
using System.IO;
using System.Text.Json;

namespace SpotifyNotch;

public sealed class Settings
{
    /// <summary>Open the notch when the cursor dwells at the top-centre of the screen.</summary>
    public bool HoverEnabled { get; set; } = true;
    public int HoverDelayMs { get; set; } = 150;

    /// <summary>Global toggle hotkey, e.g. "Ctrl+Alt+N".</summary>
    public string Hotkey { get; set; } = "Ctrl+Alt+N";

    /// <summary>Briefly drop a mini notch when the track changes.</summary>
    public bool PeekOnTrackChange { get; set; } = true;

    public bool Visualizer { get; set; } = true;

    /// <summary>
    /// true = classic WPF transparent (layered) window, which presents at half the display refresh rate;
    /// false = GPU-composited window made transparent by DWM (smoother). Switch to true if the notch ever renders wrongly.
    /// </summary>
    public bool LayeredWindow { get; set; } = false;

    /// <summary>
    /// WPF's software renderer presents every display refresh (144 fps on a 144 Hz screen) where its GPU path manages every second one,
    /// at the cost of some CPU while the notch is on screen. Set false to use the GPU renderer.
    /// </summary>
    public bool SoftwareRendering { get; set; } = true;

    /// <summary>The soft moving colour blobs behind the expanded notch.</summary>
    public bool Blobs { get; set; } = true;

    /// <summary>The faint dot grid behind the expanded notch.</summary>
    public bool DotGrid { get; set; } = true;

    /// <summary>"Default" = neon cyan/violet/magenta leaning toward the cover; "Album" = colours taken from the current cover.</summary>
    public string BlobColors { get; set; } = "Default";

    /// <summary>Keep the background still (no drifting wave).</summary>
    public bool ReduceMotion { get; set; } = false;

    /// <summary>Only follow media sessions / audio sessions from this app. Empty = any app.</summary>
    public string SourceFilter { get; set; } = "Spotify";

    /// <summary>Reserved for the Web API features (like button, devices, queue).</summary>
    public string SpotifyClientId { get; set; } = "";

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpotifyNotch");
    public static string FilePath => Path.Combine(Dir, "settings.json");

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception ex) { Log.Write("settings load failed: " + ex.Message); }
        var s = new Settings();
        s.Save();
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex) { Log.Write("settings save failed: " + ex.Message); }
    }
}

internal static class Log
{
    public static void Write(string msg)
    {
        try
        {
            Directory.CreateDirectory(Settings.Dir);
            File.AppendAllText(Path.Combine(Settings.Dir, "log.txt"), $"{DateTime.Now:s} {msg}{Environment.NewLine}");
        }
        catch { }
    }
}
