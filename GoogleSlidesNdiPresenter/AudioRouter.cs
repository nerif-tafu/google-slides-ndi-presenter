using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GoogleSlidesNdiPresenter;

public sealed record AudioDevice(string Id, string Name, bool IsDefault);

/// <summary>
/// Plays the slide audio captured from the live player on two devices: the main output
/// (e.g. the sound card feeding the mixer) and an optional monitor (e.g. headphones), each with
/// its own volume. Also measures the level for the meter in the UI.
/// </summary>
public sealed class AudioRouter : IDisposable
{
    public const string DefaultDevice = "default";
    public const string NoDevice = "none";

    private static readonly WaveFormat Format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    private readonly object _lock = new();
    private Output? _output;
    private Output? _monitor;

    private float _peakL, _peakR;
    private long _peakTicks;

    /// <summary>Last device problem (e.g. a device that was unplugged), or null.</summary>
    public string? Error { get; private set; }

    public static List<AudioDevice> ListDevices()
    {
        var enumerator = new MMDeviceEnumerator();
        string? defaultId = null;
        try { defaultId = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID; }
        catch { /* no audio devices */ }
        return enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(d => new AudioDevice(d.ID, d.FriendlyName, d.ID == defaultId))
            .OrderBy(d => d.Name)
            .ToList();
    }

    /// <summary>
    /// Applies the device choices and volumes (0–100). Devices are reopened only when they change,
    /// so moving a volume slider is instant.
    /// </summary>
    public void Configure(string outputDevice, int outputVolume, string monitorDevice, int monitorVolume)
    {
        lock (_lock)
        {
            Error = null;
            _output = Apply(_output, outputDevice, outputVolume, "output");
            _monitor = Apply(_monitor, monitorDevice, monitorVolume, "monitor");
        }
    }

    private Output? Apply(Output? current, string deviceId, int volume, string label)
    {
        if (current != null && current.DeviceId == deviceId)
        {
            current.Volume = volume;
            return current;
        }
        current?.Dispose();
        if (string.IsNullOrEmpty(deviceId) || deviceId == NoDevice) return null;
        try
        {
            var enumerator = new MMDeviceEnumerator();
            var device = deviceId == DefaultDevice
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                : enumerator.GetDevice(deviceId);
            return new Output(deviceId, device, volume);
        }
        catch (Exception ex)
        {
            Error = $"Couldn't open the {label} device ({ex.Message}).";
            return null;
        }
    }

    /// <summary>Feeds a chunk of stereo float samples (interleaved) at the given sample rate.</summary>
    public void Feed(byte[] chunk, int rate)
    {
        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(chunk);
        float l = 0, r = 0;
        for (var i = 0; i + 1 < samples.Length; i += 2)
        {
            var a = Math.Abs(samples[i]);
            var b = Math.Abs(samples[i + 1]);
            if (a > l) l = a;
            if (b > r) r = b;
        }

        lock (_lock)
        {
            var (dl, dr) = DecayedLevels();
            _peakL = Math.Max(dl, l);
            _peakR = Math.Max(dr, r);
            _peakTicks = Environment.TickCount64;
            _output?.Write(chunk, rate);
            _monitor?.Write(chunk, rate);
        }
    }

    /// <summary>Current meter levels (linear peak, 0–1) with a short release so the bars fall smoothly.</summary>
    public (float Left, float Right) GetLevels()
    {
        lock (_lock) return DecayedLevels();
    }

    private (float, float) DecayedLevels()
    {
        var elapsed = (Environment.TickCount64 - _peakTicks) / 1000f;
        var k = MathF.Exp(-elapsed / 0.35f);
        return (_peakL * k, _peakR * k);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _output?.Dispose();
            _monitor?.Dispose();
            _output = _monitor = null;
        }
    }

    /// <summary>One playback device with its own buffer and volume.</summary>
    private sealed class Output : IDisposable
    {
        private readonly BufferedWaveProvider _buffer;
        private readonly JitterBuffer _source;
        private readonly WasapiOut _player;

        public string DeviceId { get; }

        public int Volume
        {
            set => _source.Gain = MathF.Pow(Math.Clamp(value, 0, 100) / 100f, 2); // perceptual curve
        }

        public Output(string deviceId, MMDevice device, int volume)
        {
            DeviceId = deviceId;
            _buffer = new BufferedWaveProvider(Format)
            {
                BufferDuration = TimeSpan.FromSeconds(2),
                DiscardOnBufferOverflow = true,
                ReadFully = false,
            };
            _source = new JitterBuffer(_buffer);
            Volume = volume;
            _player = new WasapiOut(device, AudioClientShareMode.Shared, true, 60);
            _player.Init(_source);
            _player.Play();
        }

        public void Write(byte[] chunk, int rate)
        {
            _source.SourceRate = rate;
            _buffer.AddSamples(chunk, 0, chunk.Length);
        }

        public void Dispose()
        {
            try { _player.Stop(); } catch { /* device already gone */ }
            _player.Dispose();
        }
    }

    /// <summary>
    /// Sits between the captured audio and the device. Waits for some audio before playing (and again
    /// after running dry), so the uneven arrival of captured chunks doesn't cause crackles, and fades
    /// in and out so starting and stopping doesn't click. Applies the gain.
    /// <para>
    /// The captured audio arrives at the browser's output rate (e.g. 44.1 kHz) and is resampled to
    /// 48 kHz here. The browser and the device also run on separate clocks that never quite agree, so
    /// a plain buffer would slowly drain or fill and eventually glitch; playback is resampled very
    /// slightly faster or slower to hold the buffer at its target level.
    /// </para>
    /// </summary>
    private sealed class JitterBuffer(BufferedWaveProvider buffer) : ISampleProvider
    {
        private const int Rate = 48000;
        private const double TargetMs = 150;   // aim to keep this much audio queued
        private const double StartMs = 150;    // queue this much before (re)starting
        private const double MaxSkew = 0.02;   // speed up or slow down by at most 2%
        private const int FadeFrames = Rate * 5 / 1000;

        private readonly ISampleProvider _samples = buffer.ToSampleProvider();
        private float[] _in = new float[Rate];  // captured frames being played (interleaved stereo)
        private int _inFrames;                   // frames held in _in
        private double _pos;                     // read position in _in, in frames (fractional)
        private bool _playing;
        private int _fadeIn;
        private double _level;                   // smoothed queue level, ms
        private double _drift;                   // learned clock difference (integral term)

        public float Gain { get; set; } = 1f;
        public WaveFormat WaveFormat => Format;

        /// <summary>Sample rate of the captured audio being written to the buffer.</summary>
        public int SourceRate { get; set; } = Rate;

        private double QueuedMs => (buffer.BufferedBytes / 8 + (_inFrames - _pos)) * 1000.0 / SourceRate;

        public int Read(float[] dest, int offset, int count)
        {
            var frames = count / 2;
            var made = 0;
            if (!_playing && QueuedMs >= StartMs)
            {
                _playing = true;
                _fadeIn = 0;
                _level = QueuedMs;
            }
            if (_playing) made = Play(dest, offset, frames);
            // Not Array.Clear: NAudio hands us its byte buffer disguised as a float[], and Array.Clear
            // would treat it as bytes and blank only a quarter of it, so the rest would keep replaying
            // the last real audio (a constant buzz when a clip is cut off mid-play).
            for (var i = made * 2; i < count; i++) dest[offset + i] = 0f;
            return count;
        }

        private int Play(float[] dest, int offset, int frames)
        {
            // Speed: proportional to how far the queue is from its target, plus a slowly learned
            // offset for the clock difference so it settles on the target rather than beside it.
            var dt = frames / (double)Rate;
            _level += (QueuedMs - _level) * Math.Min(1, dt / 0.5);
            var error = (_level - TargetMs) / 1000.0;
            _drift = Math.Clamp(_drift + error * dt * 0.0025, -MaxSkew, MaxSkew);
            var step = SourceRate / (double)Rate * (1 + Math.Clamp(error * 0.1 + _drift, -MaxSkew, MaxSkew));

            // Pull enough captured frames for this block, plus a margin for the fade-out.
            var needed = (int)Math.Ceiling(_pos + frames * step) + 2;
            Fill(needed + FadeFrames);
            var made = frames;
            var stopping = _inFrames < needed + FadeFrames;
            if (stopping) made = Math.Clamp((int)((_inFrames - 2 - _pos) / step), 0, frames);

            var g = Gain;
            for (var f = 0; f < made; f++)
            {
                var i = (int)_pos;
                var t = (float)(_pos - i);
                var a = i * 2;
                var amp = g;
                if (_fadeIn < FadeFrames) amp *= _fadeIn++ / (float)FadeFrames;
                if (stopping && made - f <= FadeFrames) amp *= (made - f) / (float)Math.Min(made, FadeFrames);
                dest[offset + f * 2] = (_in[a] + (_in[a + 2] - _in[a]) * t) * amp;
                dest[offset + f * 2 + 1] = (_in[a + 1] + (_in[a + 3] - _in[a + 1]) * t) * amp;
                _pos += step;
            }

            if (stopping)
            {
                // Ran dry (the clip ended or the capture stalled): wait for a full queue again.
                _playing = false;
                _inFrames = 0;
                _pos = 0;
            }
            return made;
        }

        /// <summary>Drops played frames from <see cref="_in"/> and tops it up to <paramref name="frames"/> if possible.</summary>
        private void Fill(int frames)
        {
            var keep = (int)_pos;
            if (keep > 0)
            {
                Array.Copy(_in, keep * 2, _in, 0, (_inFrames - keep) * 2);
                _inFrames -= keep;
                _pos -= keep;
            }
            if (_in.Length < frames * 2) Array.Resize(ref _in, frames * 2);
            while (_inFrames < frames)
            {
                var got = _samples.Read(_in, _inFrames * 2, (frames - _inFrames) * 2);
                if (got <= 0) break;
                _inFrames += got / 2;
            }
        }
    }
}
