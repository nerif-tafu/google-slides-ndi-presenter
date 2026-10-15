using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace GoogleSlidesNdiPresenter;

/// <summary>A frame captured from the live player: JPEG data plus how much of the viewport is slide content.</summary>
public sealed record LiveFrame(string Base64Jpeg, int ContentHeight, int ViewportHeight);

/// <summary>
/// State of the video or audio on the current slide. Kind is "video" or "audio"; State is playing,
/// paused, ended, buffering or ready; Quality is the video's YouTube quality level (e.g. "hd1080")
/// and Best the highest available; Position and Duration are in whole seconds.
/// </summary>
public sealed record VideoInfo(string State, string Quality, string Best, string Resolution, string Available, string PlayerSize,
    string Kind = "video", int Position = 0, int Duration = 0);

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

    /// <summary>
    /// Raised on the UI thread with captured slide audio: stereo, interleaved 32-bit float, at the
    /// given sample rate (the browser's output device rate, e.g. 44100 or 48000).
    /// </summary>
    public event Action<byte[], int>? AudioCaptured;

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
        core.NavigationCompleted += async (_, _) =>
        {
            await StartScreencastAsync();
            _ = core.ExecuteScriptAsync($"window.__gs && __gs.setMedia({_mediaJson})");
        };
        core.GetDevToolsProtocolEventReceiver("Page.screencastFrame").DevToolsProtocolEventReceived += OnScreencastFrame;

        await core.AddScriptToExecuteOnDocumentCreatedAsync(TrackerScript);
        await core.CallDevToolsProtocolMethodAsync("Page.enable", "{}");
        // Lets the audio capture load its AudioWorklet in the YouTube player frames, whose CSP blocks it.
        await core.CallDevToolsProtocolMethodAsync("Page.setBypassCSP", "{\"enabled\":true}");
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

    private string _mediaJson = "{}";

    /// <summary>
    /// Tells the player which audio and video each slide has (pageId -> clips), so Play/Pause/Rewind
    /// can find the current slide's audio. Kept and re-applied whenever the player reloads.
    /// </summary>
    public void SetMedia(IReadOnlyList<SlideMedia> media)
    {
        _mediaJson = JsonSerializer.Serialize(media
            .GroupBy(m => m.PageId)
            .ToDictionary(g => g.Key, g => g.Select(m => new { kind = m.Kind, fileId = m.FileId }).ToArray()));
        Run($"window.__gs && __gs.setMedia({_mediaJson})");
    }

    /// <summary>Plays, pauses, toggles or rewinds the video or audio on the current slide.</summary>
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
            // Audio chunks are frequent, so they skip JSON parsing: {"audio":"<rate>:<base64>"}
            if (message.StartsWith("{\"audio\":\"", StringComparison.Ordinal))
            {
                var colon = message.IndexOf(':', 10);
                if (colon > 10 && int.TryParse(message.AsSpan(10, colon - 10), out var rate) && rate > 0)
                    AudioCaptured?.Invoke(Convert.FromBase64String(message[(colon + 1)..^2]), rate);
                return;
            }
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            if (root.TryGetProperty("diag", out var diag))
            {
                // Audio capture diagnostics, written only when GSNDI_AUDIO_LOG names a file.
                if (Environment.GetEnvironmentVariable("GSNDI_AUDIO_LOG") is { Length: > 0 } logPath)
                    File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} {diag.GetString()}{Environment.NewLine}");
                return;
            }
            if (root.TryGetProperty("video", out var v))
            {
                string Text(string name) => v.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
                int Number(string name) => v.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? (int)p.GetDouble() : 0;
                VideoChanged?.Invoke(v.ValueKind == JsonValueKind.Object
                    ? new VideoInfo(Text("state") is { Length: > 0 } st ? st : "unknown", Text("quality"), Text("best"), Text("resolution"),
                        Text("available"), Text("size"), Text("kind") is { Length: > 0 } k ? k : "video", Number("position"), Number("duration"))
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
          const diag = text => { try { window.top.postMessage({ gsAudioDiag: location.host + ': ' + text }, '*'); } catch (e) {} };

          // Slides serves audio from another Google domain. Ask for it with CORS so the app can capture
          // it: without CORS the browser silences cross-site audio once it's routed through Web Audio.
          const googleHost = url => {
            try { const h = new URL(url, location.href).host; return h !== location.host && /(^|\.)google(usercontent)?\.com$/.test(h); }
            catch (e) { return false; }
          };
          const corsFor = (el, url) => {
            if (el instanceof HTMLAudioElement && el.crossOrigin === null && el.dataset.gsDirect !== '1' && googleHost(url)) el.crossOrigin = 'anonymous';
          };
          const srcDesc = Object.getOwnPropertyDescriptor(HTMLMediaElement.prototype, 'src');
          Object.defineProperty(HTMLMediaElement.prototype, 'src', {
            configurable: true, enumerable: srcDesc.enumerable, get: srcDesc.get,
            set(v) { corsFor(this, v); srcDesc.set.call(this, v); },
          });
          const setAttr = Element.prototype.setAttribute;
          Element.prototype.setAttribute = function (name, value) {
            if (this instanceof HTMLMediaElement && String(name).toLowerCase() === 'src') corsFor(this, value);
            return setAttr.call(this, name, value);
          };

          // Audio tap (every frame): route each <video>/<audio> into Web Audio instead of the speakers
          // and forward the samples to the app, which plays them on the chosen devices.
          (() => {
            let ctx = null, mix = null, sent = 0;
            const tapped = new WeakSet();
            const toBase64 = f32 => {
              const u8 = new Uint8Array(f32.buffer);
              let s = '';
              for (let i = 0; i < u8.length; i += 0x8000) s += String.fromCharCode.apply(null, u8.subarray(i, i + 0x8000));
              return btoa(s);
            };
            // Sends a block of interleaved stereo samples to the app, unless it's silence.
            const send = out => {
              let peak = 0;
              for (let i = 0; i < out.length; i++) { const a = Math.abs(out[i]); if (a > peak) peak = a; }
              if (peak < 1e-5) return;
              window.top.postMessage({ gsAudio: toBase64(out), rate: ctx.sampleRate }, '*');
              sent++;
            };
            // Collects 2048-frame blocks on the audio thread. A ScriptProcessor (the fallback) runs on
            // the page's main thread, and whenever that's a little late Chromium drops 128-frame
            // slices of audio, heard as clicks.
            const worklet = `registerProcessor('gs-tap', class extends AudioWorkletProcessor {
              constructor() { super(); this.buf = new Float32Array(4096); this.n = 0; }
              process(inputs) {
                const inp = inputs[0];
                if (inp && inp.length) {
                  const L = inp[0], R = inp[1] || inp[0];
                  for (let i = 0; i < L.length; i++) {
                    this.buf[2 * this.n] = L[i]; this.buf[2 * this.n + 1] = R[i];
                    if (++this.n === 2048) { this.port.postMessage(this.buf, [this.buf.buffer]); this.buf = new Float32Array(4096); this.n = 0; }
                  }
                }
                return true;
              }
            });`;
            const setup = () => {
              // At the output device's own rate (the app resamples), so Chromium doesn't have to.
              ctx = new AudioContext();
              mix = ctx.createGain();
              const silent = ctx.createGain();
              silent.gain.value = 0; // keeps the capture node running without playing anything locally
              silent.connect(ctx.destination);
              const scriptProcessor = () => {
                const proc = ctx.createScriptProcessor(2048, 2, 2);
                proc.onaudioprocess = e => {
                  const L = e.inputBuffer.getChannelData(0), R = e.inputBuffer.getChannelData(1);
                  const out = new Float32Array(L.length * 2);
                  for (let i = 0; i < L.length; i++) { out[2 * i] = L[i]; out[2 * i + 1] = R[i]; }
                  send(out);
                };
                mix.connect(proc); proc.connect(silent);
              };
              const blobUrl = URL.createObjectURL(new Blob([worklet], { type: 'application/javascript' }));
              const dataUrl = 'data:application/javascript;base64,' + btoa(worklet);
              ctx.audioWorklet.addModule(blobUrl).catch(() => ctx.audioWorklet.addModule(dataUrl)).then(() => {
                const node = new AudioWorkletNode(ctx, 'gs-tap', { channelCount: 2, channelCountMode: 'explicit', outputChannelCount: [2] });
                node.port.onmessage = e => send(e.data);
                mix.connect(node); node.connect(silent);
                diag('capturing on the audio thread @' + ctx.sampleRate);
              }).catch(err => {
                diag('audio worklet unavailable (' + err + '), using ScriptProcessor @' + ctx.sampleRate);
                scriptProcessor();
              });
            };
            // Reload a media element with or without CORS, keeping its position and play state.
            const reload = (m, cors) => {
              const t = m.currentTime, playing = !m.paused;
              if (cors) m.crossOrigin = 'anonymous'; else m.removeAttribute('crossorigin');
              m.load();
              try { m.currentTime = t; } catch (e) {}
              if (playing) m.play().catch(() => {});
            };
            // If CORS is refused (e.g. a private deck's audio), play it normally rather than not at all.
            document.addEventListener('error', e => {
              const m = e.target;
              if (m instanceof HTMLAudioElement && m.crossOrigin && !tapped.has(m) && m.dataset.gsDirect !== '1') {
                m.dataset.gsDirect = '1';
                diag('CORS refused, playing directly: ' + (m.currentSrc || m.src).slice(0, 60));
                reload(m, false);
              }
            }, true);
            setInterval(() => {
              for (const m of document.querySelectorAll('video, audio')) {
                if (tapped.has(m)) continue;
                if (m instanceof HTMLAudioElement) {
                  if (m.dataset.gsDirect === '1') continue;
                  const src = m.currentSrc || m.src;
                  if (m.crossOrigin === null && src && googleHost(src)) { reload(m, true); continue; } // set before we could catch it
                  // Only tap audio that has loaded with CORS; tapping anything else would make it go silent.
                  if (src && googleHost(src) && (m.crossOrigin !== 'anonymous' || m.readyState < 1 || m.error)) continue;
                  if (!src) continue;
                }
                tapped.add(m);
                try {
                  if (!ctx) setup();
                  ctx.createMediaElementSource(m).connect(mix);
                  diag('tapped ' + m.tagName + ' ' + (m.currentSrc || m.src || '').slice(0, 60));
                } catch (err) { diag('tap failed ' + m.tagName + ': ' + err); }
              }
              if (ctx && ctx.state === 'suspended') ctx.resume();
            }, 250);
            setInterval(() => {
              const media = [...document.querySelectorAll('video, audio')];
              if (ctx || media.length) diag('ctx=' + (ctx && ctx.state) + ' sent=' + sent + ' media=' + media.map(m => m.tagName + (m.paused ? ':paused' : ':playing') + (m.muted ? ':muted' : '') + (tapped.has(m) ? ':tapped' : '') + (m.dataset.gsDirect === '1' ? ':direct' : '')).join(','));
              sent = 0;
            }, 2000);
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
              const yt = p && p.getPlayerState;
              const state = yt ? (names[p.getPlayerState()] || 'ready') : (v.ended ? 'ended' : v.paused ? 'paused' : 'playing');
              const position = yt && p.getCurrentTime ? p.getCurrentTime() : (v ? v.currentTime : 0);
              const duration = yt && p.getDuration ? p.getDuration() : (v && isFinite(v.duration) ? v.duration : 0);
              window.top.postMessage({ gsVideoState: {
                kind: 'video',
                state,
                quality: p && p.getPlaybackQuality ? p.getPlaybackQuality() : '',
                best: p && p.getAvailableQualityLevels ? best(p) : '',
                resolution: v && v.videoWidth ? v.videoWidth + 'x' + v.videoHeight : '',
                available: p && p.getAvailableQualityLevels ? p.getAvailableQualityLevels().join(',') : '',
                size: Math.round(innerWidth) + 'x' + Math.round(innerHeight),
                position: Math.floor(position || 0),
                duration: Math.floor(duration || 0),
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

          // Video players on other slides stay in the page, hidden. Only the visible one counts:
          // audio and state from hidden ones are ignored, and they're paused when the slide changes
          // (otherwise a video left playing keeps sending choppy audio, heard as a buzz).
          const visibleFrames = () => [...document.querySelectorAll('iframe')].filter(f =>
            f.checkVisibility ? f.checkVisibility({ visibilityProperty: true, opacityProperty: true }) : f.getClientRects().length > 0);
          const fromVisible = src => src === window || visibleFrames().some(f => f.contentWindow === src);

          let media = {}; // pageId -> [{ kind, fileId }], from the app (parsed from the deck)
          let frameVideo = null, frameVideoAt = 0;
          window.addEventListener('message', e => {
            const d = e.data;
            if (!d) return;
            if (d.gsVideoState) { if (fromVisible(e.source)) { frameVideo = d.gsVideoState; frameVideoAt = Date.now(); } }
            else if (d.gsAudio) { if (fromVisible(e.source)) window.chrome.webview.postMessage('{"audio":"' + (d.rate | 0) + ':' + d.gsAudio + '"}'); }
            else if (d.gsAudioDiag) window.chrome.webview.postMessage(JSON.stringify({ diag: d.gsAudioDiag }));
          });

          const pageId = () => (new URLSearchParams(location.search).get('slide') || '').replace(/^id\./, '');
          // The <audio> element for the current slide's audio (Slides keeps one per clip, shared by slides using the same file).
          const slideAudio = () => {
            const ids = (media[pageId()] || []).filter(m => m.kind === 'audio' && m.fileId).map(m => m.fileId);
            if (!ids.length) return null;
            const els = [...document.querySelectorAll('audio')].filter(a => ids.some(id => (a.currentSrc || a.src || '').includes(id)));
            return els.find(a => !a.paused) || els[els.length - 1] || null;
          };
          const audioState = a => ({
            kind: 'audio',
            state: a.ended ? 'ended' : !a.paused ? 'playing' : a.currentTime > 0 ? 'paused' : 'ready',
            quality: '', best: '', resolution: '', available: '', size: '',
            position: Math.floor(a.currentTime || 0),
            duration: isFinite(a.duration) ? Math.floor(a.duration) : 0,
          });
          const videoVisible = () => visibleFrames().length > 0 && Date.now() - frameVideoAt < 1500;

          window.__gs = {
            key,
            goto: n => { for (const ch of String(n)) key(ch, ch.charCodeAt(0)); key('Enter', 13); },
            setMedia: m => { media = m || {}; },
            video: cmd => {
              if (videoVisible()) {
                for (const f of visibleFrames()) { try { f.contentWindow.postMessage({ gsVideo: cmd }, '*'); } catch (e) {} }
                return;
              }
              const a = slideAudio();
              if (!a) return;
              if (cmd === 'restart') { a.currentTime = 0; return; }
              const play = cmd === 'play' || (cmd === 'toggle' && a.paused);
              if (play) a.play().catch(() => {}); else a.pause();
            },
          };
          document.addEventListener('DOMContentLoaded', () => {
            const s = document.createElement('style');
            s.textContent = '*{cursor:none!important}';
            document.head.appendChild(s);
          });

          let last = '', lastVideo = '', lastPage = '', shown = [];
          setInterval(() => {
            const page = pageId();
            if (page !== lastPage) {
              lastPage = page;
              // Pause the videos of the slide we just left, once they're hidden. Only those: a video
              // on the new slide may still be loading in and about to start by itself.
              const leaving = shown;
              setTimeout(() => {
                const visible = new Set(visibleFrames());
                for (const f of leaving)
                  if (!visible.has(f)) { try { f.contentWindow.postMessage({ gsVideo: 'pause' }, '*'); } catch (e) {} }
              }, 400);
            }
            shown = visibleFrames();

            let v = videoVisible() ? frameVideo : null;
            if (!v) { const a = slideAudio(); if (a) v = audioState(a); }
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
