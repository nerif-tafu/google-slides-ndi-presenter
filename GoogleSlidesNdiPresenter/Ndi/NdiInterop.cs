using System.Reflection;
using System.Runtime.InteropServices;

namespace GoogleSlidesNdiPresenter.Ndi;

/// <summary>Minimal P/Invoke bindings for the NDI runtime (Processing.NDI.Lib.x64.dll).</summary>
internal static class NdiInterop
{
    private const string Lib = "Processing.NDI.Lib.x64.dll";

    // NDI_LIB_FOURCC('B','G','R','A')
    public const uint FourCC_BGRA = 'B' | ('G' << 8) | ('R' << 16) | ('A' << 24);
    public const int FrameFormatProgressive = 1;
    public const long SendTimecodeSynthesize = long.MaxValue;

    private static bool _resolverSet;

    /// <summary>Locates the NDI runtime installed by the NDI Tools / Runtime installer.</summary>
    public static void EnsureResolver()
    {
        if (_resolverSet) return;
        _resolverSet = true;
        NativeLibrary.SetDllImportResolver(typeof(NdiInterop).Assembly, Resolve);
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != Lib) return IntPtr.Zero;

        var candidates = new List<string> { AppContext.BaseDirectory };
        foreach (var env in new[] { "NDI_RUNTIME_DIR_V6", "NDI_RUNTIME_DIR_V5" })
        {
            var dir = Environment.GetEnvironmentVariable(env);
            if (!string.IsNullOrEmpty(dir)) candidates.Add(dir);
        }

        foreach (var dir in candidates)
        {
            var path = Path.Combine(dir, Lib);
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out var handle))
                return handle;
        }
        return NativeLibrary.TryLoad(Lib, out var h) ? h : IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SendCreate
    {
        public IntPtr p_ndi_name;
        public IntPtr p_groups;
        public byte clock_video;
        public byte clock_audio;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VideoFrameV2
    {
        public int xres;
        public int yres;
        public uint FourCC;
        public int frame_rate_N;
        public int frame_rate_D;
        public float picture_aspect_ratio;
        public int frame_format_type;
        public long timecode;
        public IntPtr p_data;
        public int line_stride_in_bytes;
        public IntPtr p_metadata;
        public long timestamp;
    }

    [DllImport(Lib, EntryPoint = "NDIlib_initialize")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool Initialize();

    [DllImport(Lib, EntryPoint = "NDIlib_destroy")]
    public static extern void Destroy();

    [DllImport(Lib, EntryPoint = "NDIlib_send_create")]
    public static extern IntPtr SendCreateInstance(ref SendCreate settings);

    [DllImport(Lib, EntryPoint = "NDIlib_send_destroy")]
    public static extern void SendDestroy(IntPtr instance);

    [DllImport(Lib, EntryPoint = "NDIlib_send_send_video_v2")]
    public static extern void SendVideoV2(IntPtr instance, ref VideoFrameV2 frame);

    [DllImport(Lib, EntryPoint = "NDIlib_send_get_no_connections")]
    public static extern int SendGetNoConnections(IntPtr instance, uint timeoutMs);
}
