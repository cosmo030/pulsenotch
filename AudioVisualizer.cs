using System;
using NAudio.Wave;

namespace SpotifyNotch;

/// <summary>
/// Captures whatever is playing on the default output device (WASAPI loopback) and turns it into
/// log-spaced spectrum bands. Only runs while the notch is visible.
/// </summary>
public sealed class AudioVisualizer : IDisposable
{
    public const int Bands = 28;
    const int N = 2048;
    const int Hop = 512;

    public readonly float[] Levels = new float[Bands];

    /// <summary>Sum of all band levels; ~0 when silent, so the UI can skip redraws.</summary>
    public float Energy
    {
        get
        {
            float e = 0;
            for (int i = 0; i < Bands; i++) e += Levels[i];
            return e;
        }
    }

    readonly float[] _ring = new float[N];
    readonly double[] _re = new double[N];
    readonly double[] _im = new double[N];
    readonly double[] _window = new double[N];
    int _write, _sinceHop;
    double _peak = 0.4;
    long _lastDataTicks;
    WasapiLoopbackCapture? _cap;

    public AudioVisualizer()
    {
        for (int i = 0; i < N; i++) _window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (N - 1));
    }

    // Starting/stopping WASAPI takes tens of milliseconds, so it happens on a pool thread; the UI thread only records
    // what it wants. The latest request always wins.
    readonly object _gate = new();
    volatile bool _wanted;

    public void Start()
    {
        _wanted = true;
        _ = System.Threading.Tasks.Task.Run(Apply);
    }

    public void Stop()
    {
        _wanted = false;
        Array.Clear(Levels);
        _ = System.Threading.Tasks.Task.Run(Apply);
    }

    void Apply()
    {
        lock (_gate)
        {
            if (_wanted) { if (_cap == null) StartCore(); }
            else if (_cap != null) StopCore();
        }
    }

    void StartCore()
    {
        try
        {
            _cap = new WasapiLoopbackCapture();
            _cap.DataAvailable += OnData;
            _cap.StartRecording();
        }
        catch (Exception ex)
        {
            Log.Write("visualizer start failed: " + ex.Message);
            _cap = null;
        }
    }

    void StopCore()
    {
        var cap = _cap;
        _cap = null;
        if (cap == null) return;
        try
        {
            cap.DataAvailable -= OnData;
            cap.StopRecording();
            cap.Dispose();
        }
        catch { }
        Array.Clear(Levels);
    }

    /// <summary>Call every UI frame: fades the bars out when no audio data is arriving.</summary>
    public void Update(double dt)
    {
        if (Environment.TickCount64 - _lastDataTicks < 120) return;
        double f = Math.Exp(-7 * dt);
        for (int i = 0; i < Bands; i++) Levels[i] = (float)(Levels[i] * f);
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        if (sender is not WasapiLoopbackCapture cap) return;
        _lastDataTicks = Environment.TickCount64;

        var fmt = cap.WaveFormat;
        int ch = Math.Max(1, fmt.Channels);
        int bytesPerSample = fmt.BitsPerSample / 8;
        bool isFloat = fmt.BitsPerSample == 32;
        int frames = e.BytesRecorded / (bytesPerSample * ch);

        for (int f = 0; f < frames; f++)
        {
            float sum = 0;
            for (int c = 0; c < ch; c++)
            {
                int off = (f * ch + c) * bytesPerSample;
                sum += isFloat ? BitConverter.ToSingle(e.Buffer, off) : BitConverter.ToInt16(e.Buffer, off) / 32768f;
            }
            _ring[_write] = sum / ch;
            _write = (_write + 1) % N;
            if (++_sinceHop >= Hop)
            {
                _sinceHop = 0;
                Analyze(fmt.SampleRate);
            }
        }
    }

    void Analyze(int sampleRate)
    {
        for (int i = 0; i < N; i++)
        {
            _re[i] = _ring[(_write + i) % N] * _window[i];
            _im[i] = 0;
        }
        Fft(_re, _im);

        double binHz = (double)sampleRate / N;
        const double fLo = 45, fHi = 14000;
        double frameMax = 0;
        Span<double> v = stackalloc double[Bands];

        for (int k = 0; k < Bands; k++)
        {
            double f0 = fLo * Math.Pow(fHi / fLo, (double)k / Bands);
            double f1 = fLo * Math.Pow(fHi / fLo, (double)(k + 1) / Bands);
            int b0 = Math.Max(1, (int)Math.Floor(f0 / binHz));
            int b1 = Math.Min(N / 2 - 1, Math.Max(b0, (int)Math.Ceiling(f1 / binHz)));
            double amp = 0;
            for (int b = b0; b <= b1; b++)
            {
                double mag = Math.Sqrt(_re[b] * _re[b] + _im[b] * _im[b]) * 4.0 / N; // amplitude of a sine at this bin
                if (mag > amp) amp = mag;
            }
            double db = 20 * Math.Log10(amp + 1e-7);
            double level = (db + 72 + 16.0 * k / (Bands - 1)) / 52.0; // tilt: lift highs so the spectrum looks balanced
            level = Math.Clamp(level, 0, 1.2);
            v[k] = level;
            if (level > frameMax) frameMax = level;
        }

        // slow auto-gain so quiet playback still moves
        _peak = Math.Max(_peak * 0.9985, frameMax);
        double gain = 0.95 / Math.Max(_peak, 0.4);

        for (int k = 0; k < Bands; k++)
        {
            float target = (float)Math.Clamp(v[k] * gain, 0, 1);
            float cur = Levels[k];
            Levels[k] = target > cur ? cur + (target - cur) * 0.55f : cur - (cur - target) * 0.12f;
        }
    }

    static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len;
            double wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int j = 0; j < len / 2; j++)
                {
                    int u = i + j, w = i + j + len / 2;
                    double xr = re[w] * cr - im[w] * ci;
                    double xi = re[w] * ci + im[w] * cr;
                    re[w] = re[u] - xr;
                    im[w] = im[u] - xi;
                    re[u] += xr;
                    im[u] += xi;
                    double ncr = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr;
                    cr = ncr;
                }
            }
        }
    }

    public void Dispose()
    {
        _wanted = false;
        lock (_gate) { if (_cap != null) StopCore(); }
    }
}
