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

    /// <summary>Feeds a chunk of 48 kHz stereo float samples (interleaved).</summary>
    public void Feed(byte[] chunk)
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
            _output?.Write(chunk);
            _monitor?.Write(chunk);
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

        public void Write(byte[] chunk) => _buffer.AddSamples(chunk, 0, chunk.Length);

        public void Dispose()
        {
            try { _player.Stop(); } catch { /* device already gone */ }
            _player.Dispose();
        }
    }

    /// <summary>
    /// Waits for ~120 ms of audio before playing (and again after running dry), so the uneven
    /// arrival of captured chunks doesn't cause crackles; outputs silence meanwhile. Applies the gain.
    /// </summary>
    private sealed class JitterBuffer(BufferedWaveProvider buffer) : ISampleProvider
    {
        private static readonly int PrimeBytes = Format.AverageBytesPerSecond * 120 / 1000;
        private readonly ISampleProvider _samples = buffer.ToSampleProvider();
        private bool _primed;

        public float Gain { get; set; } = 1f;
        public WaveFormat WaveFormat => Format;

        public int Read(float[] dest, int offset, int count)
        {
            if (!_primed && buffer.BufferedBytes >= PrimeBytes) _primed = true;
            var read = 0;
            if (_primed)
            {
                read = _samples.Read(dest, offset, count);
                if (read < count) _primed = false; // ran dry: re-buffer before playing again
                var g = Gain;
                for (var i = 0; i < read; i++) dest[offset + i] *= g;
            }
            Array.Clear(dest, offset + read, count - read);
            return count;
        }
    }
}
