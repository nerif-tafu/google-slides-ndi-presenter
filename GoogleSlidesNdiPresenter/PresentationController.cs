using System.Threading.Channels;
using GoogleSlidesNdiPresenter.Ndi;

namespace GoogleSlidesNdiPresenter;

public sealed record PresentationState(
    string Url,
    string? PresentationId,
    string? Title,
    long Version,       // increments on every successful load (cache-busts slide thumbnails)
    int ThumbnailCount, // slides with a thumbnail available (/api/slides/{n}.jpg)
    int Slide,          // 1-based, 0 when nothing is loaded
    int SlideCount,
    float NotesFontSize,
    bool NotesAutoFit,
    float NotesEffectiveFontSize,
    float NotesScroll,
    float NotesMaxScroll,
    string Notes,
    bool Loading,
    string? LoadingId,      // deck being opened or refreshed, whoever started it (UI, phone, Companion…)
    string? LoadingTitle,   // its title if known (from recents), for the "Opening …" overlay
    bool LoadingRefresh,    // true when reloading the deck that's already open
    bool Live,
    VideoInfo? Video,   // video on the current slide, null when there is none
    string Status,
    string? SlidesNdiName,
    string? NotesNdiName,
    int SlidesReceivers,
    int NotesReceivers,
    string? NdiError,
    string? NdiDiscovery,   // discovery server(s) the NDI outputs register with, or null for automatic discovery
    string? LiveError,
    IReadOnlyList<RecentPresentation> Recent);

/// <summary>
/// Owns the presentation state and pushes rendered frames to the two NDI senders.
/// When a <see cref="LivePresenter"/> is attached, the slide output is the live Google Slides
/// player (animations/videos included) and navigation is forwarded to it; otherwise slides are
/// static images from the PDF export. All public members are thread-safe.
/// </summary>
public sealed class PresentationController : IDisposable
{
    public const float MinFontSize = 12;
    public const float MaxFontSize = 300;
    public const float FontStep = 4;
    private const float ScrollLines = 3;

    private readonly object _lock = new();
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly AppSettings _settings;
    private readonly NdiSender? _slidesSender;
    private readonly NdiSender? _notesSender;
    private readonly Channel<LiveFrame> _liveFrames = Channel.CreateBounded<LiveFrame>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    private LivePresenter? _live;
    private string? _liveId;          // presentation currently in the live player
    private int _liveCount;           // slide count reported by the live player (0 = not yet)
    private string? _liveSlideParam;  // e.g. "id.p62", used to keep position on refresh
    private VideoInfo? _video;

    private LoadedPresentation? _presentation;
    private int _index;
    private float _fontSize;
    private bool _autoFit;
    private float _effectiveFontSize;
    private float _scroll;
    private float _maxScroll;
    private string _url;
    private bool _loading;
    private readonly string? _ndiDiscovery;
    private string? _loadingId;
    private string? _loadingTitle;
    private bool _loadingRefresh;
    private string _status = "Ready";
    private Bitmap? _slideFrame;
    private Bitmap? _notesFrame;
    private PresenterBackground? _background;
    private long _frameVersion;
    // Starts from the clock so thumbnail URLs (cached by the browser) never repeat across runs.
    private long _version = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
    private string? _liveError;
    private readonly Dictionary<(int Index, int Width), byte[]> _thumbCache = new();

    public event Action? StateChanged;

    public string? NdiError { get; }

    /// <summary>Increments every time an output frame changes (for preview refresh).</summary>
    public long FrameVersion => Interlocked.Read(ref _frameVersion);

    public PresentationController(AppSettings settings)
    {
        _settings = settings;
        _url = settings.LastUrl;
        _fontSize = Math.Clamp(settings.NotesFontSize, MinFontSize, MaxFontSize);
        _effectiveFontSize = _fontSize;
        _autoFit = settings.AutoFitNotes;

        try
        {
            NdiConfig.Apply(settings.NdiDiscoveryServer); // must happen before NDI starts
            _ndiDiscovery = NdiConfig.Normalise(settings.NdiDiscoveryServer) is { Length: > 0 } d ? d : null;
            NdiInterop.EnsureResolver();
            if (!NdiInterop.Initialize())
                throw new InvalidOperationException("NDIlib_initialize failed (unsupported CPU?).");
            _slidesSender = new NdiSender(settings.SlidesNdiName, settings.Width, settings.Height, settings.FrameRate);
            _notesSender = new NdiSender(settings.NotesNdiName, settings.Width, settings.Height, settings.FrameRate);
        }
        catch (DllNotFoundException)
        {
            NdiError = "NDI runtime not found. Install NDI Tools or the NDI Runtime from ndi.video.";
        }
        catch (Exception ex)
        {
            NdiError = "NDI unavailable: " + ex.Message;
        }

        lock (_lock) RenderStaticSlide();
        _ = Task.Run(ProcessLiveFramesAsync);
    }

    /// <summary>Connects the live player; from now on it drives the slide output and navigation.</summary>
    public void AttachLive(LivePresenter live)
    {
        _live = live;
        live.SlideChanged += OnLiveSlideChanged;
        live.FrameCaptured += f => _liveFrames.Writer.TryWrite(f);
        live.VideoChanged += v =>
        {
            lock (_lock) _video = v;
            OnChanged();
        };
    }

    private int CountLocked => _liveCount > 0 ? _liveCount : _presentation?.SlidePngs.Count ?? 0;

    public PresentationState GetState()
    {
        lock (_lock)
        {
            var count = CountLocked;
            return new PresentationState(
                _url,
                _presentation?.Id,
                _presentation?.Title,
                _version,
                _presentation?.SlidePngs.Count ?? 0,
                count > 0 ? _index + 1 : 0,
                count,
                _fontSize,
                _autoFit,
                _effectiveFontSize,
                _scroll,
                _maxScroll,
                NotesFor(_index) ?? "",
                _loading,
                _loading ? _loadingId : null,
                _loading ? _loadingTitle : null,
                _loading && _loadingRefresh,
                _live != null,
                _video,
                _status,
                _slidesSender?.Name,
                _notesSender?.Name,
                _slidesSender?.Connections ?? 0,
                _notesSender?.Connections ?? 0,
                NdiError,
                _ndiDiscovery,
                _liveError,
                _settings.Recent.ToList());
        }
    }

    /// <summary>Records why the live player couldn't start (slides fall back to static images).</summary>
    public void SetLiveError(string message)
    {
        lock (_lock) _liveError = message;
        OnChanged();
    }

    /// <summary>JPEG thumbnail of a slide from the PDF export (1-based), or null if out of range.</summary>
    public byte[]? GetSlideThumbnail(int slideNumber, int width)
    {
        byte[]? png;
        var key = (slideNumber - 1, Math.Clamp(width, 64, 1920));
        lock (_lock)
        {
            if (_thumbCache.TryGetValue(key, out var cached)) return cached;
            png = PngFor(slideNumber - 1);
        }
        if (png == null) return null;

        using var full = FrameRenderer.RenderSlide(png, _settings.Width, _settings.Height);
        using var thumb = FrameRenderer.Thumbnail(full, key.Item2);
        var jpeg = FrameRenderer.EncodeJpeg(thumb, 85);
        lock (_lock) _thumbCache[key] = jpeg;
        return jpeg;
    }

    /// <summary>Closes the open presentation: both outputs go to the holding scene.</summary>
    public async Task CloseAsync()
    {
        await _loadGate.WaitAsync();
        try
        {
            lock (_lock)
            {
                _presentation = null;
                _url = "";
                _index = 0;
                _scroll = 0;
                _video = null;
                _liveId = null;
                _liveCount = 0;
                _liveSlideParam = null;
                _status = "Ready";
                _version++;
                _thumbCache.Clear();
                InvalidateNotes();
                RenderStaticSlide();
            }
            _live?.Unload();
            _settings.LastUrl = "";
            _settings.Save();
        }
        finally
        {
            _loadGate.Release();
        }
        OnChanged();
    }

    public void RemoveRecent(string id)
    {
        lock (_lock) _settings.Recent.RemoveAll(r => r.Id == id);
        _settings.Save();
        OnChanged();
    }

    /// <summary>Returns a copy of the current slide output frame (caller disposes).</summary>
    public Bitmap? GetSlidePreview(int maxWidth = int.MaxValue)
    {
        lock (_lock) return _slideFrame == null ? null : FrameRenderer.Thumbnail(_slideFrame, maxWidth);
    }

    /// <summary>Returns a copy of the current notes output frame (caller disposes).</summary>
    public Bitmap? GetNotesPreview(int maxWidth = int.MaxValue)
    {
        lock (_lock) return _notesFrame == null ? null : FrameRenderer.Thumbnail(_notesFrame, maxWidth);
    }

    /// <summary>
    /// Loads (or reloads) the presentation. Pass null to refresh the current URL.
    /// Reloading the same presentation keeps the current slide position.
    /// </summary>
    public async Task LoadAsync(string? url = null, CancellationToken ct = default)
    {
        url = (url ?? _url).Trim();
        if (url.Length == 0) throw new ArgumentException("No Google Slides URL set.");

        await _loadGate.WaitAsync(ct);
        try
        {
            var id = PresentationLoader.ExtractId(url);
            lock (_lock)
            {
                _loading = true;
                _loadingId = id;
                _loadingRefresh = id != null && id == _presentation?.Id;
                _loadingTitle = _loadingRefresh ? _presentation?.Title : _settings.Recent.FirstOrDefault(r => r.Id == id)?.Title;
                _status = "Loading presentation…";
                if (id != null && _live != null)
                {
                    var same = id == _liveId;
                    if (!same) { _liveCount = 0; _liveSlideParam = null; }
                    _liveId = id;
                    _live.Load(id, same ? _liveSlideParam : null);
                }
            }
            OnChanged();

            LoadedPresentation loaded;
            try
            {
                loaded = await Task.Run(() => PresentationLoader.LoadAsync(url, _settings.Width, _settings.Height, ct), ct);
            }
            catch (Exception ex)
            {
                lock (_lock)
                {
                    _loading = false;
                    _status = "Load failed: " + ex.Message;
                }
                OnChanged();
                throw;
            }

            lock (_lock)
            {
                var samePresentation = _presentation?.Id == loaded.Id;
                _presentation = loaded;
                _url = url;
                if (!samePresentation && _live == null) _index = 0;
                _index = Math.Clamp(_index, 0, Math.Max(0, CountLocked - 1));
                _loading = false;
                _status = $"Loaded {loaded.SlidePngs.Count} slides at {DateTime.Now:HH:mm:ss}";
                _version++;
                _thumbCache.Clear();
                _settings.Recent.RemoveAll(r => r.Id == loaded.Id);
                _settings.Recent.Insert(0, new RecentPresentation
                {
                    Id = loaded.Id, Url = url, Title = loaded.Title,
                    SlideCount = loaded.SlidePngs.Count, LastOpened = DateTime.Now,
                });
                if (_settings.Recent.Count > 8) _settings.Recent.RemoveRange(8, _settings.Recent.Count - 8);
                if (_liveCount > 0 && _liveCount != loaded.SlidePngs.Count)
                    _status += $" (player reports {_liveCount}; notes may be misaligned)";
                InvalidateNotes();
                if (_live == null || _liveCount == 0) RenderStaticSlide();
                else RenderNotesFrame();
            }

            _settings.LastUrl = url;
            _settings.Save();
            OnChanged();
        }
        finally
        {
            _loadGate.Release();
        }
    }

    /// <summary>Next animation build or slide (like a clicker).</summary>
    public bool Next()
    {
        if (_live != null) { _live.Next(); return true; }
        return GoToIndexStatic(i => i + 1);
    }

    /// <summary>Previous animation build or slide.</summary>
    public bool Previous()
    {
        if (_live != null) { _live.Previous(); return true; }
        return GoToIndexStatic(i => i - 1);
    }

    public bool GoTo(int slideNumber)
    {
        if (_live != null) { _live.GoTo(slideNumber); return true; }
        return GoToIndexStatic(_ => slideNumber - 1);
    }

    /// <summary>Plays, pauses or toggles the video on the current slide (live player only).</summary>
    public bool ControlVideo(VideoCommand command)
    {
        if (_live == null) return false;
        _live.Video(command);
        return true;
    }

    private bool GoToIndexStatic(Func<int, int> next)
    {
        lock (_lock)
        {
            var count = CountLocked;
            if (count == 0) return false;
            var target = next(_index);
            if (target < 0 || target >= count || target == _index) return false;
            _index = target;
            _scroll = 0;
            InvalidateNotes();
            RenderStaticSlide();
        }
        OnChanged();
        return true;
    }

    private void OnLiveSlideChanged(int slide, int count, string? slideParam)
    {
        lock (_lock)
        {
            _liveCount = count;
            _liveSlideParam = slideParam;
            var index = Math.Clamp(slide - 1, 0, Math.Max(0, count - 1));
            if (index != _index)
            {
                _index = index;
                _scroll = 0;
            }
            InvalidateNotes();
            RenderNotesFrame();
        }
        OnChanged();
    }

    private async Task ProcessLiveFramesAsync()
    {
        await foreach (var frame in _liveFrames.Reader.ReadAllAsync())
        {
            try
            {
                var bmp = FrameRenderer.RenderLiveFrame(frame, _settings.Width, _settings.Height);
                lock (_lock)
                {
                    if (_liveCount == 0) { bmp.Dispose(); continue; } // player not ready / switching decks
                    SetSlideFrame(bmp);
                    RenderNotesFrame();
                }
            }
            catch
            {
                // Skip undecodable frames.
            }
        }
    }

    // While auto-fit is on, A+/A- start from the fitted size and switch back to manual sizing.
    public void IncreaseNotesSize() => AdjustNotesSize(+FontStep);
    public void DecreaseNotesSize() => AdjustNotesSize(-FontStep);

    private void AdjustNotesSize(float delta)
    {
        float baseSize;
        lock (_lock) baseSize = _autoFit ? _effectiveFontSize : _fontSize;
        SetNotesSize(baseSize + delta);
    }

    /// <summary>Sets a manual notes size (turns auto-fit off).</summary>
    public void SetNotesSize(float size)
    {
        lock (_lock)
        {
            size = Math.Clamp(size, MinFontSize, MaxFontSize);
            if (size == _fontSize && !_autoFit) return;
            _fontSize = size;
            _autoFit = false;
            InvalidateNotes();
            RenderNotesFrame();
        }
        _settings.NotesFontSize = size;
        _settings.AutoFitNotes = false;
        OnChanged();
    }

    /// <summary>When on, notes are drawn at the largest size that fits without scrolling.</summary>
    public void SetNotesAutoFit(bool enabled)
    {
        lock (_lock)
        {
            if (_autoFit == enabled) return;
            _autoFit = enabled;
            _scroll = 0;
            InvalidateNotes();
            RenderNotesFrame();
        }
        _settings.AutoFitNotes = enabled;
        OnChanged();
    }

    public void ToggleNotesAutoFit()
    {
        bool current;
        lock (_lock) current = _autoFit;
        SetNotesAutoFit(!current);
    }

    public void ScrollNotesDown() => ScrollNotesBy(+1);
    public void ScrollNotesUp() => ScrollNotesBy(-1);

    private void ScrollNotesBy(int direction)
    {
        lock (_lock)
        {
            var step = _effectiveFontSize * 1.33f * ScrollLines;
            var target = Math.Clamp(_scroll + direction * step, 0, _maxScroll);
            if (target == _scroll) return;
            _scroll = target;
            InvalidateNotes();
            RenderNotesFrame();
        }
        OnChanged();
    }

    public void ScrollNotesToTop()
    {
        lock (_lock)
        {
            if (_scroll == 0) return;
            _scroll = 0;
            InvalidateNotes();
            RenderNotesFrame();
        }
        OnChanged();
    }

    private string? NotesFor(int index) =>
        _presentation is { } p && index >= 0 && index < p.Notes.Count ? p.Notes[index] : null;

    private byte[]? PngFor(int index) =>
        _presentation is { } p && index >= 0 && index < p.SlidePngs.Count ? p.SlidePngs[index] : null;

    // Caller must hold _lock.
    private void InvalidateNotes()
    {
        _background?.Dispose();
        _background = null;
    }

    // Caller must hold _lock.
    private void RenderStaticSlide()
    {
        SetSlideFrame(HasDeckLocked
            ? FrameRenderer.RenderSlide(PngFor(_index), _settings.Width, _settings.Height)
            : FrameRenderer.RenderHolding(HoldingScene.Slides, _settings.Width, _settings.Height));
        RenderNotesFrame();
    }

    // A deck is open (or the live player is already showing one while its notes download).
    private bool HasDeckLocked => _presentation != null || _liveCount > 0;

    // Caller must hold _lock.
    private void SetSlideFrame(Bitmap frame)
    {
        _slidesSender?.SetFrame(FrameRenderer.ToBgra(frame));
        _slideFrame?.Dispose();
        _slideFrame = frame;
        Interlocked.Increment(ref _frameVersion);
    }

    // Caller must hold _lock.
    private void RenderNotesFrame()
    {
        if (!HasDeckLocked)
        {
            var holding = FrameRenderer.RenderHolding(HoldingScene.Presenter, _settings.Width, _settings.Height);
            _notesSender?.SetFrame(FrameRenderer.ToBgra(holding));
            _notesFrame?.Dispose();
            _notesFrame = holding;
            Interlocked.Increment(ref _frameVersion);
            return;
        }

        if (_background == null)
        {
            var count = CountLocked;
            _background = FrameRenderer.RenderPresenterBackground(
                NotesFor(_index), _index, count, PngFor(_index + 1), _index + 1 < count,
                _fontSize, _autoFit, MinFontSize, MaxFontSize, _scroll, _settings.Width, _settings.Height);
            _scroll = _background.Scroll;
            _effectiveFontSize = _background.FontSize;
            _maxScroll = _background.MaxScroll;
        }

        var notes = _slideFrame != null
            ? FrameRenderer.ComposePresenter(_background, _slideFrame)
            : new Bitmap(_background.Bitmap);
        _notesSender?.SetFrame(FrameRenderer.ToBgra(notes));
        _notesFrame?.Dispose();
        _notesFrame = notes;
        Interlocked.Increment(ref _frameVersion);
    }

    private void OnChanged() => StateChanged?.Invoke();

    public void Dispose()
    {
        _liveFrames.Writer.TryComplete();
        _slidesSender?.Dispose();
        _notesSender?.Dispose();
        lock (_lock)
        {
            _slideFrame?.Dispose();
            _notesFrame?.Dispose();
            _background?.Dispose();
        }
    }
}
