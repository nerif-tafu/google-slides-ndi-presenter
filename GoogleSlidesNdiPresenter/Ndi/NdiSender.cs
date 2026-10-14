using System.Runtime.InteropServices;

namespace GoogleSlidesNdiPresenter.Ndi;

/// <summary>
/// An NDI video source that continuously re-sends the most recent BGRA frame at a fixed frame rate
/// (NDI receivers expect a steady stream even for static content).
/// </summary>
public sealed class NdiSender : IDisposable
{
    private readonly int _width;
    private readonly int _height;
    private readonly int _fps;
    private readonly Thread _thread;
    private IntPtr _instance;
    private volatile byte[]? _frame;
    private volatile bool _running = true;

    public string Name { get; }

    public NdiSender(string name, int width, int height, int fps)
    {
        Name = name;
        _width = width;
        _height = height;
        _fps = fps;

        var namePtr = Marshal.StringToCoTaskMemUTF8(name);
        try
        {
            var create = new NdiInterop.SendCreate
            {
                p_ndi_name = namePtr,
                p_groups = IntPtr.Zero,
                clock_video = 1, // let NDI pace send calls to the frame rate
                clock_audio = 0,
            };
            _instance = NdiInterop.SendCreateInstance(ref create);
        }
        finally
        {
            Marshal.FreeCoTaskMem(namePtr);
        }
        if (_instance == IntPtr.Zero)
            throw new InvalidOperationException($"Failed to create NDI sender '{name}'.");

        _thread = new Thread(SendLoop) { IsBackground = true, Name = $"NDI {name}" };
        _thread.Start();
    }

    /// <summary>Sets the frame to output. Must be width*height*4 bytes of BGRA; the array must not be mutated afterwards.</summary>
    public void SetFrame(byte[] bgra)
    {
        if (bgra.Length != _width * _height * 4)
            throw new ArgumentException("Frame size does not match sender resolution.", nameof(bgra));
        _frame = bgra;
    }

    /// <summary>
    /// Approximate number of receivers watching this source. NDI only reports connections, and
    /// each video receiver opens two (measured: 1 receiver = 2, 2 receivers = 4, a metadata-only
    /// connection = 1), so halve and round up.
    /// </summary>
    public int Connections
    {
        get
        {
            if (_instance == IntPtr.Zero) return 0;
            var connections = NdiInterop.SendGetNoConnections(_instance, 0);
            return (connections + 1) / 2;
        }
    }

    private unsafe void SendLoop()
    {
        var frame = new NdiInterop.VideoFrameV2
        {
            xres = _width,
            yres = _height,
            FourCC = NdiInterop.FourCC_BGRA,
            frame_rate_N = _fps * 1000,
            frame_rate_D = 1000,
            picture_aspect_ratio = (float)_width / _height,
            frame_format_type = NdiInterop.FrameFormatProgressive,
            timecode = NdiInterop.SendTimecodeSynthesize,
            line_stride_in_bytes = _width * 4,
            p_metadata = IntPtr.Zero,
            timestamp = 0,
        };

        while (_running)
        {
            var data = _frame;
            if (data == null)
            {
                Thread.Sleep(1000 / _fps);
                continue;
            }
            fixed (byte* p = data)
            {
                frame.p_data = (IntPtr)p;
                NdiInterop.SendVideoV2(_instance, ref frame);
            }
        }
    }

    public void Dispose()
    {
        _running = false;
        _thread.Join(2000);
        if (_instance != IntPtr.Zero)
        {
            NdiInterop.SendDestroy(_instance);
            _instance = IntPtr.Zero;
        }
    }
}
