using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace GoogleSlidesNdiPresenter;

/// <summary>A frame captured from the live player: JPEG data plus how much of the viewport is slide content.</summary>
public sealed record LiveFrame(string Base64Jpeg, int ContentHeight, int ViewportHeight);

/// <summary>
/// State of the video on the current slide. State is playing, paused, ended, buffering or ready;
/// Quality is the YouTube quality level in use (e.g. "hd1080") and Best the highest available.
/// </summary>
public sealed record VideoInfo(string State, string Quality, string Best, string Resolution, string Available, string PlayerSize);

public enum VideoCommand { Play, Pause, Toggle, Restart }

/// <summary>
/// Runs Google's embedded Slides player in an offscreen WebView2 so animations, builds and
/// videos play exactly as they do when presenting. Frames are captured with the DevTools
/// screencast API. All public methods are thread-safe (they marshal to the UI thread).
/// </summary>
public sealed class LivePresenter : IDisposable
{
    // The embed player reserves a navbar strip at the bottom; make the viewport taller and crop it off.
    private const int NavbarAllowance = 36;

    private readonly HostForm _host;
    private readonly WebView2 _web;
    private readonly int _width;
    private readonly int _height;
    private int _contentHeight;
    private bool _disposed;

    /// <summary>Raised on the UI thread: (1-based slide, slide count, slide id param).</summary>
    public event Action<int, int, string?>? SlideChanged;

    /// <summary>Raised on the UI thread for each captured frame.</summary>
    public event Action<LiveFrame>? FrameCaptured;

    /// <summary>Raised on the UI thread when the state of the current slide's video changes (null = no video).</summary>
    public event Action<VideoInfo?>? VideoChanged;

    /// <summary>Raised on the UI thread with captured slide audio: 48 kHz stereo, interleaved 32-bit float.</summary>
    public event Action<byte[]>? AudioCaptured;

    private LivePresenter(int width, int height)
    {
        _width = width;
        _height = height;
        _contentHeight = height;
        _host = new HostForm(new Size(width, height + NavbarAllowance));
        _web = new WebView2 { Dock = DockStyle.Fill };
        _host.Controls.Add(_web);
        _host.Show();
    }

    /// <summary>Creates the player. Must be called on the UI thread.</summary>
    public static async Task<LivePresenter> CreateAsync(int width, int height)
    {
        var lp = new LivePresenter(width, height);
        try
        {
            await lp.InitializeAsync();
            return lp;
        }
        catch
        {
            lp.Dispose();
            throw;
        }
    }

    private async Task InitializeAsync()
    {
        var env = await WebViewEnvironment.GetAsync();
        // The Google profile, so decks shared with the signed-in account play too.
        await _web.EnsureCoreWebView2Async(env, GoogleAccount.ControllerOptions(env));

        var core = _web.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.WebMessageReceived += OnWebMessage;
        core.NavigationCompleted += async (_, _) => await StartScreencastAsync();
        core.GetDevToolsProtocolEventReceiver("Page.screencastFrame").DevToolsProtocolEventReceived += OnScreencastFrame;

        await core.AddScriptToExecuteOnDocumentCreatedAsync(TrackerScript);
        await core.CallDevToolsProtocolMethodAsync("Page.enable", "{}");
        await core.CallDevToolsProtocolMethodAsync("Emulation.setDeviceMetricsOverride", JsonSerializer.Serialize(new
        {
            width = _width,
            height = _height + NavbarAllowance,
            deviceScaleFactor = 1,
            mobile = false,
        }));
    }

    public void Load(string presentationId, string? slideParam)
    {
        var url = $"https://docs.google.com/presentation/d/{Uri.EscapeDataString(presentationId)}/embed?start=false&loop=false&delayms=60000";
        if (!string.IsNullOrEmpty(slideParam)) url += "&slide=" + Uri.EscapeDataString(slideParam);
        OnUi(() => _web.CoreWebView2?.Navigate(url));
    }

    /// <summary>Stops the player (used when the presentation is closed).</summary>
    public void Unload() => OnUi(() => _web.CoreWebView2?.Navigate("about:blank"));

    /// <summary>Advances like a presentation clicker: next animation build, then next slide.</summary>
    public void Next() => Run("window.__gs && __gs.key('ArrowRight', 39)");
    public void Previous() => Run("window.__gs && __gs.key('ArrowLeft', 37)");
    public void GoTo(int slide) => Run($"window.__gs && __gs.goto({slide})");

    /// <summary>Plays, pauses or toggles the video on the current slide.</summary>
    public void Video(VideoCommand command) => Run($"window.__gs && __gs.video('{command.ToString().ToLowerInvariant()}')");

    private void Run(string script) => OnUi(() => _ = _web.CoreWebView2?.ExecuteScriptAsync(script));

    private void OnUi(Action action)
    {
        if (_disposed || _host.IsDisposed) return;
        if (_host.InvokeRequired) _host.BeginInvoke(action);
        else action();
    }

    private async Task StartScreencastAsync()
    {
        var core = _web.CoreWebView2;
        if (core == null) return;
        try
        {
            await core.CallDevToolsProtocolMethodAsync("Page.stopScreencast", "{}");
            await core.CallDevToolsProtocolMethodAsync("Page.startScreencast", JsonSerializer.Serialize(new
            {
                format = "jpeg",
                quality = 92,
                maxWidth = _width,
                maxHeight = _height + NavbarAllowance,
                everyNthFrame = 1,
            }));
        }
        catch
        {
            // Navigation raced with shutdown; ignore.
        }
    }

    private void OnScreencastFrame(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        string data;
        int sessionId;
        using (var doc = JsonDocument.Parse(e.ParameterObjectAsJson))
        {
            data = doc.RootElement.GetProperty("data").GetString() ?? "";
            sessionId = doc.RootElement.GetProperty("sessionId").GetInt32();
        }
        // Ack right away so Chromium keeps streaming; consumers keep only the latest frame.
        _ = _web.CoreWebView2?.CallDevToolsProtocolMethodAsync("Page.screencastFrameAck", $"{{\"sessionId\":{sessionId}}}");
        FrameCaptured?.Invoke(new LiveFrame(data, _contentHeight, _height + NavbarAllowance));
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var message = e.TryGetWebMessageAsString();
            // Audio chunks are frequent, so they skip JSON parsing: {"audio":"<base64>"}
            if (message.StartsWith("{\"audio\":\"", StringComparison.Ordinal))
            {
                AudioCaptured?.Invoke(Convert.FromBase64String(message[10..^2]));
                return;
            }
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            if (root.TryGetProperty("video", out var v))
            {
                VideoChanged?.Invoke(v.ValueKind == JsonValueKind.Object
                    ? new VideoInfo(
                        v.GetProperty("state").GetString() ?? "unknown",
                        v.GetProperty("quality").GetString() ?? "",
                        v.GetProperty("best").GetString() ?? "",
                        v.GetProperty("resolution").GetString() ?? "",
                        v.GetProperty("available").GetString() ?? "",
                        v.GetProperty("size").GetString() ?? "")
                    : null);
                return;
            }
            var slide = root.GetProperty("slide").GetInt32();
            var count = root.GetProperty("count").GetInt32();
            var contentH = root.GetProperty("contentH").GetInt32();
            var param = root.GetProperty("slideParam").GetString();
            _contentHeight = Math.Clamp(contentH, 1, _height + NavbarAllowance);
            SlideChanged?.Invoke(slide, count, string.IsNullOrEmpty(param) ? null : param);
        }
        catch
        {
            // Ignore malformed messages.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _web.Dispose();
        _host.Dispose();
    }

    /// <summary>
    /// Injected into every frame. In the top frame it reports the slide position and exposes
    /// navigation/video helpers; in video iframes (YouTube/Drive players) it handles play/pause
    /// commands, pins playback to the highest available quality, and reports the video state.
    /// </summary>
    private const string TrackerScript = """
        (() => {
          // Audio tap (every frame): route each <video>/<audio> into Web Audio instead of the
          // speakers and forward the samples to the app, which plays them on the chosen devices.
          (() => {
            let ctx = null, mix = null;
            const tapped = new WeakSet();
            const toBase64 = f32 => {
              const u8 = new Uint8Array(f32.buffer);
              let s = '';
              for (let i = 0; i < u8.length; i += 0x8000) s += String.fromCharCode.apply(null, u8.subarray(i, i + 0x8000));
              return btoa(s);
            };
            const setup = () => {
              ctx = new AudioContext({ sampleRate: 48000 });
              mix = ctx.createGain();
              const proc = ctx.createScriptProcessor(2048, 2, 2);
              const silent = ctx.createGain();
              silent.gain.value = 0; // keeps the processor running without playing anything locally
              mix.connect(proc); proc.connect(silent); silent.connect(ctx.destination);
              proc.onaudioprocess = e => {
                const L = e.inputBuffer.getChannelData(0), R = e.inputBuffer.getChannelData(1);
                let peak = 0;
                for (let i = 0; i < L.length; i++) { const a = Math.abs(L[i]), b = Math.abs(R[i]); if (a > peak) peak = a; if (b > peak) peak = b; }
                if (peak < 1e-5) return; // silence: send nothing
                const out = new Float32Array(L.length * 2);
                for (let i = 0; i < L.length; i++) { out[2 * i] = L[i]; out[2 * i + 1] = R[i]; }
                window.top.postMessage({ gsAudio: toBase64(out) }, '*');
              };
            };
            setInterval(() => {
              for (const m of document.querySelectorAll('video, audio')) {
                if (tapped.has(m)) continue;
                tapped.add(m);
                try {
                  if (!ctx) setup();
                  ctx.createMediaElementSource(m).connect(mix);
                } catch (err) { /* already captured elsewhere */ }
              }
              if (ctx && ctx.state === 'suspended') ctx.resume();
            }, 500);
          })();

          if (window.top !== window) {
            const player = () => document.getElementById('movie_player');
            const media = () => document.querySelector('video');
            const names = { '-1': 'ready', 0: 'ended', 1: 'playing', 2: 'paused', 3: 'buffering', 5: 'ready' };
            const best = p => ((p.getAvailableQualityLevels && p.getAvailableQualityLevels()) || []).find(q => q !== 'auto') || '';
            let pinned = '';
            // The output is a program feed: hide YouTube's play button, title bar, controls and overlays.
            const hideChrome = () => {
              if (document.getElementById('gs-hide-chrome') || !document.head) return;
              const st = document.createElement('style');
              st.id = 'gs-hide-chrome';
              st.textContent = '.ytp-large-play-button,.ytp-chrome-top,.ytp-chrome-bottom,.ytp-gradient-top,.ytp-gradient-bottom,' +
                '.ytp-pause-overlay,.ytp-watermark,.ytp-show-cards-title,.ytp-ce-element,.ytp-spinner,.ytp-contextmenu,' +
                '.ytp-tooltip,.ytp-bezel,.ytp-paid-content-overlay,.ytp-impression-link{display:none!important}' +
                '*{cursor:none!important}';
              document.head.appendChild(st);
            };
            document.addEventListener('DOMContentLoaded', hideChrome);
            const enforceQuality = () => {
              const p = player();
              if (!p || !p.getPlaybackQuality) return;
              const b = best(p);
              if (!b || (pinned === b && p.getPlaybackQuality() === b)) return;
              try { p.setPlaybackQualityRange(b, b); } catch (e) {}
              try { p.setPlaybackQuality(b); } catch (e) {}
              pinned = b;
            };
            const report = () => {
              const p = player(), v = media();
              if (!p && !v) return;
              const state = p && p.getPlayerState
                ? (names[p.getPlayerState()] || 'ready')
                : (v.ended ? 'ended' : v.paused ? 'paused' : 'playing');
              window.top.postMessage({ gsVideoState: {
                state,
                quality: p && p.getPlaybackQuality ? p.getPlaybackQuality() : '',
                best: p && p.getAvailableQualityLevels ? best(p) : '',
                resolution: v && v.videoWidth ? v.videoWidth + 'x' + v.videoHeight : '',
                available: p && p.getAvailableQualityLevels ? p.getAvailableQualityLevels().join(',') : '',
                size: Math.round(innerWidth) + 'x' + Math.round(innerHeight),
              } }, '*');
            };
            window.addEventListener('message', e => {
              const cmd = e.data && e.data.gsVideo;
              if (!cmd) return;
              for (const f of document.querySelectorAll('iframe')) { try { f.contentWindow.postMessage(e.data, '*'); } catch (err) {} }
              const p = player(), v = media();
              if (cmd === 'restart') {
                // Back to the start; keeps playing if it was playing.
                if (p && p.seekTo) p.seekTo(0, true); else if (v) v.currentTime = 0;
                setTimeout(report, 300);
                return;
              }
              const playing = p && p.getPlayerState ? [1, 3].includes(p.getPlayerState()) : !!(v && !v.paused);
              const play = cmd === 'play' || (cmd === 'toggle' && !playing);
              if (p && p.playVideo) { enforceQuality(); play ? p.playVideo() : p.pauseVideo(); }
              else if (v) { play ? v.play() : v.pause(); }
              setTimeout(report, 300);
            });
            setInterval(() => { hideChrome(); enforceQuality(); report(); }, 500);
            return;
          }

          const key = (k, kc) => {
            for (const type of ['keydown', 'keypress', 'keyup']) {
              document.body.dispatchEvent(new KeyboardEvent(type, {
                key: k, code: k.length === 1 ? 'Digit' + k : k, keyCode: kc, which: kc,
                charCode: type === 'keypress' ? kc : 0, bubbles: true, cancelable: true }));
            }
          };
          let video = null, videoAt = 0;
          window.addEventListener('message', e => {
            if (!e.data) return;
            if (e.data.gsVideoState) { video = e.data.gsVideoState; videoAt = Date.now(); }
            else if (e.data.gsAudio) window.chrome.webview.postMessage('{"audio":"' + e.data.gsAudio + '"}');
          });
          window.__gs = {
            key,
            goto: n => { for (const ch of String(n)) key(ch, ch.charCodeAt(0)); key('Enter', 13); },
            video: cmd => {
              for (const f of document.querySelectorAll('iframe')) { try { f.contentWindow.postMessage({ gsVideo: cmd }, '*'); } catch (e) {} }
            },
          };
          document.addEventListener('DOMContentLoaded', () => {
            const s = document.createElement('style');
            s.textContent = '*{cursor:none!important}';
            document.head.appendChild(s);
          });
          let last = '', lastVideo = '';
          setInterval(() => {
            // Video iframes only exist while their slide is showing; drop stale state once they go.
            const v = document.querySelector('iframe') && Date.now() - videoAt < 1500 ? video : null;
            const vmsg = JSON.stringify({ video: v });
            if (vmsg !== lastVideo) { lastVideo = vmsg; window.chrome.webview.postMessage(vmsg); }

            const cap = document.querySelector('.punch-viewer-navbar-page [aria-setsize]');
            if (!cap) return;
            const slide = parseInt((cap.innerText || cap.textContent || '').replace(/\D+/g, ''), 10);
            const count = parseInt(cap.getAttribute('aria-setsize'), 10);
            if (!slide || !count) return;
            const nb = document.querySelector('.punch-viewer-navbar');
            const contentH = Math.round(nb ? nb.getBoundingClientRect().top : innerHeight);
            const slideParam = new URLSearchParams(location.search).get('slide') || '';
            const msg = JSON.stringify({ slide, count, contentH, slideParam });
            if (msg !== last) { last = msg; window.chrome.webview.postMessage(msg); }
          }, 100);
        })();
        """;

    /// <summary>An offscreen, non-activating window that hosts the WebView.</summary>
    private sealed class HostForm : Form
    {
        public HostForm(Size size)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            Location = new Point(-20000, -20000);
            ClientSize = size;
            BackColor = Color.Black;
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                const int WS_EX_TOOLWINDOW = 0x80;
                const int WS_EX_NOACTIVATE = 0x08000000;
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                return cp;
            }
        }
    }
}
