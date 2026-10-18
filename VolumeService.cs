using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NAudio.CoreAudioApi;

namespace SpotifyNotch;

/// <summary>Adjusts Spotify's own volume-mixer slider (falls back to master volume).</summary>
internal static class VolumeService
{
    public static float? Adjust(float delta, string processFilter)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

            var mgr = device.AudioSessionManager;
            mgr.RefreshSessions();
            var volumes = new List<SimpleAudioVolume>();
            var sessions = mgr.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                var s = sessions[i];
                uint pid = s.GetProcessID;
                if (pid == 0 || string.IsNullOrWhiteSpace(processFilter)) continue;
                try
                {
                    using var p = Process.GetProcessById((int)pid);
                    if (p.ProcessName.Contains(processFilter, StringComparison.OrdinalIgnoreCase))
                        volumes.Add(s.SimpleAudioVolume);
                }
                catch { }
            }

            if (volumes.Count > 0)
            {
                float next = Math.Clamp(volumes.Max(v => v.Volume) + delta, 0f, 1f);
                foreach (var v in volumes) v.Volume = next;
                return next;
            }

            var master = device.AudioEndpointVolume;
            float m = Math.Clamp(master.MasterVolumeLevelScalar + delta, 0f, 1f);
            master.MasterVolumeLevelScalar = m;
            return m;
        }
        catch (Exception ex)
        {
            Log.Write("volume failed: " + ex.Message);
            return null;
        }
    }
}
