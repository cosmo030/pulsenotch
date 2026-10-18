using System;
using Microsoft.Win32;

namespace SpotifyNotch;

internal static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "SpotifyNotch";

    public static bool IsEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(ValueName) is string;
    }

    public static void Set(bool enabled)
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, true);
        if (k == null) return;
        if (enabled && Environment.ProcessPath is { } exe)
            k.SetValue(ValueName, $"\"{exe}\"");
        else
            k.DeleteValue(ValueName, false);
    }
}
