using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GoogleSlidesNdiPresenter;

public sealed record AudioDevice(string Id, string Name, bool IsDefault);

/// <summary>The open ASIO driver. Channel is the offset of the first output of the pair in use; pairs are two offsets apart.</summary>
public sealed record AsioStatus(string Driver, IReadOnlyList<string> ChannelPairs, int Channel, int BufferFrames, double LatencyMs, int BufferMs);

/// <summary>
/// Plays the slide audio captured from the live player on two devices: the main output
/// (e.g. the sound card feeding the mixer) and an optional monitor (e.g. headphones), each with
/// its own volume. Either can be a WASAPI device or, for the lowest latency, an ASIO driver.
/// Also measures the level for the meter in the UI.
/// </summary>
public sealed class AudioRouter : IDisposable
{
    public const string DefaultDevice = "default";
    public const string NoDevice = "none";
    private const string AsioPrefix = "asio:";

    /// <summary>Audio kept queued on WASAPI devices, whose own buffer is larger and less regular than an ASIO driver's.</summary>
    private const double WasapiBufferMs = 150;

    private static readonly WaveFormat Format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    private readonly object _lock = new();
    private Output? _output;
    private Output? _monitor;
    private AsioHost? _asioHost;
    private AppSettings? _settings;

    private float _peakL, _peakR;
    private long _peakTicks;

    /// <summary>Last device problem (e.g. a device that was unplugged), or null.</summary>
    public string? Error { get; private set; }

    /// <summary>The ASIO driver in use, or null if neither device is ASIO.</summary>
    public AsioStatus? Asio
    {
        get { lock (_lock) return AsioOutput?.Asio; }
    }

    private Output? AsioOutput => _output?.Asio != null ? _output : _monitor?.Asio != null ? _monitor : null;

    private static bool IsAsio(string? deviceId) => deviceId?.StartsWith(AsioPrefix, StringComparison.Ordinal) == true;

    public static List<AudioDevice> ListDevices()
    {
        var enumerator = new MMDeviceEnumerator();
        string? defaultId = null;
        try { defaultId = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID; }
        catch { /* no audio devices */ }
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(d => new AudioDevice(d.ID, d.FriendlyName, d.ID == defaultId))
            .OrderBy(d => d.Name)
            .ToList();
        // From the registry, so this doesn't load the drivers.
        try { devices.AddRange(AsioOut.GetDriverNames().Order().Select(n => new AudioDevice(AsioPrefix + n, $"ASIO: {n}", false))); }
        catch { /* can't read the list of ASIO drivers */ }
        return devices;
    }

    /// <summary>
    /// Applies the device choices and volumes (0-100). Devices are reopened only when they change,
    /// so moving a volume slider is instant.
    /// </summary>
    public void Configure(AppSettings settings)
    {
        lock (_lock)
        {
            _settings = settings;
            Error = null;
            var outputId = settings.AudioOutputDevice;
            var monitorId = settings.AudioMonitorDevice;
            if (IsAsio(outputId) && IsAsio(monitorId))
            {
                monitorId = NoDevice;
                Error = "ASIO can only be used by one device at a time, so the monitor is off.";
            }
            // Close before opening: a driver can only be open once, so moving ASIO from one device
            // to the other wouldn't work if the new one opened while the old one still held it.
            _output = CloseIfChanged(_output, outputId, settings.AudioAsioChannel);
            _monitor = CloseIfChanged(_monitor, monitorId, settings.AudioAsioChannel);
            _output = Apply(_output, outputId, settings.AudioOutputVolume, "output", settings);
            _monitor = Apply(_monitor, monitorId, settings.AudioMonitorVolume, "monitor", settings);
        }
    }

    private static Output? CloseIfChanged(Output? current, string deviceId, int asioChannel)
    {
        if (current == null || current.Matches(deviceId, asioChannel)) return current;
        current.Dispose();
        return null;
    }

    private Output? Apply(Output? current, string deviceId, int volume, string label, AppSettings settings)
    {
        if (current != null)
        {
            current.Volume = volume;
            if (current.Asio != null) current.SetAsioBuffer(settings.AudioAsioBufferMs);
            return current;
        }
        if (string.IsNullOrEmpty(deviceId) || deviceId == NoDevice) return null;
        try
        {
            if (IsAsio(deviceId))
            {
                _asioHost ??= new AsioHost();
                return Output.OpenAsio(deviceId, deviceId[AsioPrefix.Length..], settings.AudioAsioChannel, volume, settings.AudioAsioBufferMs, _asioHost, ReopenAsio);
            }
            var enumerator = new MMDeviceEnumerator();
            var device = deviceId == DefaultDevice
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                : enumerator.GetDevice(deviceId);
            return Output.OpenWasapi(deviceId, device, volume);
        }
        catch (Exception ex)
        {
            Error = $"Couldn't open the {label} device ({ex.Message}).";
            return null;
        }
    }

    /// <summary>Opens the ASIO driver's own settings window, where its buffer size is usually set.</summary>
    public void ShowAsioControlPanel()
    {
        lock (_lock) AsioOutput?.ShowAsioControlPanel();
    }

    /// <summary>
    /// The driver asks to be reopened when its settings change (e.g. a new buffer size). It can ask
    /// from any thread and can't be closed from inside the request, so this happens separately.
    /// </summary>
    private void ReopenAsio(Output source) => Task.Run(() =>
    {
        lock (_lock)
        {
            if (_settings == null) return;
            if (ReferenceEquals(source, _output)) _output = null;
            else if (ReferenceEquals(source, _monitor)) _monitor = null;
            else return; // already closed
            source.Dispose();
            Configure(_settings);
        }
    });

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
            _asioHost?.Dispose();
            _asioHost = null;
        }
    }

    /// <summary>One playback device with its own buffer and volume.</summary>
    private sealed class Output : IDisposable
    {
        private readonly BufferedWaveProvider _buffer;
        private readonly JitterBuffer _source;
        private readonly string _deviceId;
        private IWavePlayer? _player;

        // ASIO only.
        private AsioHost? _host;
        private AsioStatus? _asio;
        private Action<Output>? _reopen;
        private int _requestedChannel;
        private double _minBufferMs;
        private volatile bool _panelOpen;
        private volatile bool _reopenPending;

        public AsioStatus? Asio => _asio == null ? null : _asio with { BufferMs = (int)Math.Round(_source.TargetMs) };

        public int Volume
        {
            set => _source.Gain = MathF.Pow(Math.Clamp(value, 0, 100) / 100f, 2); // perceptual curve
        }

        private Output(string deviceId, int volume, double bufferMs)
        {
            _deviceId = deviceId;
            _buffer = new BufferedWaveProvider(Format)
            {
                BufferDuration = TimeSpan.FromSeconds(2),
                DiscardOnBufferOverflow = true,
                ReadFully = false,
            };
            _source = new JitterBuffer(_buffer) { TargetMs = bufferMs };
            Volume = volume;
        }

        public static Output OpenWasapi(string deviceId, MMDevice device, int volume)
        {
            var output = new Output(deviceId, volume, WasapiBufferMs);
            var player = new WasapiOut(device, AudioClientShareMode.Shared, true, 60);
            player.Init(output._source);
            player.Play();
            output._player = player;
            return output;
        }

        public static Output OpenAsio(string deviceId, string driver, int channel, int volume, int bufferMs, AsioHost host, Action<Output> reopen)
        {
            var output = new Output(deviceId, volume, bufferMs) { _host = host, _reopen = reopen, _requestedChannel = channel };
            host.Run(() =>
            {
                var player = new AsioOut(driver);
                try
                {
                    if (!player.IsSampleRateSupported(Format.SampleRate))
                        throw new NotSupportedException("it can't run at 48 kHz");

                    // Stay on a whole pair the driver has, in case the channel came from another driver.
                    var count = player.DriverOutputChannelCount;
                    player.ChannelOffset = Math.Max(0, Math.Min(channel, count - 2)) & ~1;
                    player.DriverResetRequest += (_, _) => output.OnResetRequest();
                    player.Init(output._source);

                    var pairs = new List<string>();
                    for (var i = 0; i + 1 < count; i += 2)
                        pairs.Add($"{i + 1}/{i + 2}: {player.AsioOutputChannelName(i)} + {player.AsioOutputChannelName(i + 1)}");
                    var frames = player.FramesPerBuffer;
                    output._asio = new AsioStatus(driver, pairs, player.ChannelOffset, frames, player.PlaybackLatency * 1000.0 / Format.SampleRate, 0);

                    // The queue has to cover a whole driver buffer, or it would run dry on every callback.
                    output._minBufferMs = frames * 1000.0 / Format.SampleRate + 10;
                    output.SetAsioBuffer(bufferMs);

                    player.Play();
                    output._player = player;
                }
                catch
                {
                    player.Dispose();
                    throw;
                }
            });
            return output;
        }

        public bool Matches(string deviceId, int asioChannel) =>
            _deviceId == deviceId && (_asio == null || asioChannel == _requestedChannel || asioChannel == _asio.Channel);

        public void SetAsioBuffer(int bufferMs) => _source.TargetMs = Math.Max(bufferMs, _minBufferMs);

        public void ShowAsioControlPanel()
        {
            if (_host == null || _player is not AsioOut player) return;
            _host.Post(() =>
            {
                // A window that blocks here while it's open may ask to be reopened before it has
                // returned, and the driver can't be closed under it, so that waits until it has.
                _panelOpen = true;
                try { player.ShowControlPanel(); }
                catch { /* the driver has been closed, or has no settings window */ }
                _panelOpen = false;
                if (_reopenPending) _reopen?.Invoke(this);
            });
        }

        private void OnResetRequest()
        {
            if (_panelOpen) _reopenPending = true;
            else _reopen?.Invoke(this);
        }

        public void Write(byte[] chunk, int rate)
        {
            _source.SourceRate = rate;
            _buffer.AddSamples(chunk, 0, chunk.Length);
        }

        public void Dispose()
        {
            if (_player is AsioOut asio)
            {
                try { _host!.Run(asio.Dispose); } catch { /* device already gone */ }
                return;
            }
            try { _player?.Stop(); } catch { /* device already gone */ }
            _player?.Dispose();
        }
    }

    /// <summary>
    /// The thread that owns the ASIO driver. Drivers are COM objects that expect every call to come
    /// from one STA thread, and some rely on it pumping messages.
    /// </summary>
    private sealed class AsioHost : IDisposable
    {
        private readonly Control _control;

        public AsioHost()
        {
            Control? control = null;
            using var ready = new ManualResetEventSlim();
            var thread = new Thread(() =>
            {
                control = new Control();
                _ = control.Handle; // creates the window on this thread, so calls can be sent to it
                ready.Set();
                Application.Run();
            }) { IsBackground = true, Name = "ASIO" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            ready.Wait();
            _control = control!;
        }

        /// <summary>Runs on the ASIO thread and waits. Exceptions come back to the caller.</summary>
        public void Run(Action action) => _control.Invoke((Delegate)action);

        /// <summary>Runs on the ASIO thread without waiting. Must not throw.</summary>
        public void Post(Action action) => _control.BeginInvoke((Delegate)action);

        public void Dispose() => Post(Application.ExitThread);
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

        /// <summary>How much audio to keep queued, and to queue before (re)starting.</summary>
        public double TargetMs { get; set; } = WasapiBufferMs;

        /// <summary>Sample rate of the captured audio being written to the buffer.</summary>
        public int SourceRate { get; set; } = Rate;

        private double QueuedMs => (buffer.BufferedBytes / 8 + (_inFrames - _pos)) * 1000.0 / SourceRate;

        public int Read(float[] dest, int offset, int count)
        {
            var frames = count / 2;
            var made = 0;
            if (!_playing && QueuedMs >= TargetMs)
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
