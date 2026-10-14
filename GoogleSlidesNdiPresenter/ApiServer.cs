using System.Drawing.Imaging;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GoogleSlidesNdiPresenter;

/// <summary>
/// Serves the app UI (also used as the web remote) and the REST API for remote control
/// (e.g. Bitfocus Companion, Stream Deck, scripts). Every action endpoint accepts GET or POST
/// and returns the current state as JSON.
/// </summary>
public sealed class ApiServer : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly WebApplication _app;

    public int Port { get; }

    /// <summary>True when the API is reachable from other devices on the network.</summary>
    public bool RemoteEnabled { get; }

    /// <summary>Why the configured port couldn't be used (the UI then runs on a random local port).</summary>
    public string? Warning { get; }

    /// <summary>
    /// Secret the desktop window passes to the UI; required for settings changes and restart,
    /// so neither web remotes nor other websites can reconfigure the app.
    /// </summary>
    public string AppToken { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    public string LocalUrl => $"http://127.0.0.1:{Port}/";

    private ApiServer(WebApplication app, int port, bool remote, string? warning)
    {
        _app = app;
        Port = port;
        RemoteEnabled = remote;
        Warning = warning;
    }

    public static async Task<ApiServer> StartAsync(PresentationController controller, AppSettings settings, AudioRouter audio, Action requestRestart)
    {
        var host = settings.RemoteAccess ? "0.0.0.0" : "127.0.0.1";
        try
        {
            return await StartOnAsync(controller, settings, audio, requestRestart, $"http://{host}:{settings.ApiPort}", settings.RemoteAccess, null);
        }
        catch (Exception ex) when (ex is IOException or SocketException || ex.InnerException is SocketException)
        {
            // Keep the UI working on a random local port; remote control is unavailable.
            return await StartOnAsync(controller, settings, audio, requestRestart, "http://127.0.0.1:0", false,
                $"Port {settings.ApiPort} is already in use, so remote control is off. Pick another port in Settings.");
        }
    }

    private static async Task<ApiServer> StartOnAsync(
        PresentationController controller, AppSettings settings, AudioRouter audio, Action requestRestart,
        string url, bool remote, string? warning)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(url);
        // Long-lived streams (events, monitors) shouldn't hold up shutdown or restart.
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(2));
        var app = builder.Build();

        ApiServer? server = null;

        // Allow browser-based control panels on other origins (simple requests only; the
        // privileged endpoints need a custom header, which this preflight doesn't allow).
        app.Use(async (ctx, next) =>
        {
            ctx.Response.Headers.AccessControlAllowOrigin = "*";
            ctx.Response.Headers.AccessControlAllowMethods = "GET, POST, OPTIONS";
            ctx.Response.Headers.AccessControlAllowHeaders = "Content-Type";
            if (HttpMethods.IsOptions(ctx.Request.Method))
            {
                ctx.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }
            await next();
        });

        string[] getPost = ["GET", "POST"];
        IResult State() => Results.Json(controller.GetState(), Json);
        bool Privileged(HttpContext ctx) =>
            ctx.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip) &&
            ctx.Request.Headers["X-App-Token"] == server!.AppToken;

        // ---- UI ----
        app.MapGet("/", () => Asset("index.html", "text/html; charset=utf-8"));
        app.MapGet("/app.css", () => Asset("app.css", "text/css; charset=utf-8"));
        app.MapGet("/app.js", () => Asset("app.js", "text/javascript; charset=utf-8"));
        app.MapGet("/icon.svg", () => Asset("icon.svg", "image/svg+xml"));

        // ---- State ----
        app.MapGet("/api/state", State);
        app.MapGet("/api/events", (HttpContext ctx) => StreamEventsAsync(ctx, controller));
        app.MapGet("/api/info", (HttpContext ctx) => Results.Json(new
        {
            port = server!.Port,
            remoteEnabled = server.RemoteEnabled,
            remoteUrls = server.RemoteEnabled ? LanAddresses().Select(a => $"http://{a}:{server.Port}/").ToArray() : [],
            warning = server.Warning,
            isLocal = ctx.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip),
            settingsPath = AppSettings.FilePath,
            version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3),
        }, Json));

        // ---- Presentation ----
        app.MapMethods("/api/load", getPost, async (HttpRequest req) =>
        {
            string? loadUrl = req.Query["url"];
            if (string.IsNullOrWhiteSpace(loadUrl) && req.HasJsonContentType())
                loadUrl = (await req.ReadFromJsonAsync<LoadRequest>(Json))?.Url;
            if (string.IsNullOrWhiteSpace(loadUrl))
                return Results.BadRequest(new { error = "Provide a url (query string or JSON body {\"url\": \"...\"})." });
            return await RunLoad(controller, loadUrl);
        });
        app.MapMethods("/api/refresh", getPost, () => RunLoad(controller, null));
        app.MapMethods("/api/close", getPost, async () => { await controller.CloseAsync(); return State(); });
        app.MapDelete("/api/recent/{id}", (string id) => { controller.RemoveRecent(id); return State(); });

        // ---- Navigation ----
        app.MapMethods("/api/next", getPost, () => { controller.Next(); return State(); });
        app.MapMethods("/api/previous", getPost, () => { controller.Previous(); return State(); });
        app.MapMethods("/api/prev", getPost, () => { controller.Previous(); return State(); });
        app.MapMethods("/api/goto/{slide:int}", getPost, (int slide) =>
        {
            var count = controller.GetState().SlideCount;
            if (slide < 1 || slide > count)
                return Results.BadRequest(new { error = $"Slide must be between 1 and {count}." });
            controller.GoTo(slide);
            return State();
        });

        // ---- Video ----
        app.MapMethods("/api/video/play", getPost, () => { controller.ControlVideo(VideoCommand.Play); return State(); });
        app.MapMethods("/api/video/pause", getPost, () => { controller.ControlVideo(VideoCommand.Pause); return State(); });
        app.MapMethods("/api/video/toggle", getPost, () => { controller.ControlVideo(VideoCommand.Toggle); return State(); });
        app.MapMethods("/api/video/restart", getPost, () => { controller.ControlVideo(VideoCommand.Restart); return State(); });

        // ---- Audio ----
        app.MapGet("/api/audio/devices", () => Results.Json(new { devices = AudioRouter.ListDevices(), error = audio.Error }, Json));
        app.MapGet("/api/meter", (HttpContext ctx) => StreamMeterAsync(ctx, audio));
        app.MapPut("/api/audio", async (HttpContext ctx) =>
        {
            if (!Privileged(ctx)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var dto = await ctx.Request.ReadFromJsonAsync<AudioDto>(Json);
            if (dto == null) return Results.BadRequest(new { error = "Missing audio settings." });
            settings.AudioOutputDevice = string.IsNullOrWhiteSpace(dto.OutputDevice) ? AudioRouter.DefaultDevice : dto.OutputDevice;
            settings.AudioOutputVolume = Math.Clamp(dto.OutputVolume, 0, 100);
            settings.AudioMonitorDevice = string.IsNullOrWhiteSpace(dto.MonitorDevice) ? AudioRouter.NoDevice : dto.MonitorDevice;
            settings.AudioMonitorVolume = Math.Clamp(dto.MonitorVolume, 0, 100);
            audio.Configure(settings.AudioOutputDevice, settings.AudioOutputVolume, settings.AudioMonitorDevice, settings.AudioMonitorVolume);
            settings.Save();
            return Results.Json(new { error = audio.Error }, Json);
        });

        // ---- Notes ----
        app.MapMethods("/api/notes/increase", getPost, () => { controller.IncreaseNotesSize(); return State(); });
        app.MapMethods("/api/notes/decrease", getPost, () => { controller.DecreaseNotesSize(); return State(); });
        app.MapMethods("/api/notes/size/{size:float}", getPost, (float size) => { controller.SetNotesSize(size); return State(); });
        app.MapMethods("/api/notes/autofit/on", getPost, () => { controller.SetNotesAutoFit(true); return State(); });
        app.MapMethods("/api/notes/autofit/off", getPost, () => { controller.SetNotesAutoFit(false); return State(); });
        app.MapMethods("/api/notes/autofit/toggle", getPost, () => { controller.ToggleNotesAutoFit(); return State(); });
        app.MapMethods("/api/notes/scroll/down", getPost, () => { controller.ScrollNotesDown(); return State(); });
        app.MapMethods("/api/notes/scroll/up", getPost, () => { controller.ScrollNotesUp(); return State(); });
        app.MapMethods("/api/notes/scroll/top", getPost, () => { controller.ScrollNotesToTop(); return State(); });

        // ---- Images ----
        app.MapGet("/api/preview/slide.png", () => Png(controller.GetSlidePreview()));
        app.MapGet("/api/preview/notes.png", () => Png(controller.GetNotesPreview()));
        app.MapGet("/api/stream/{which}", (HttpContext ctx, string which, int? w) => StreamMjpegAsync(ctx, controller, which, w));
        app.MapGet("/api/slides/{n:int}.jpg", (HttpContext ctx, int n, int? w) =>
        {
            var jpeg = controller.GetSlideThumbnail(n, w ?? 320);
            if (jpeg == null) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "public, max-age=31536000, immutable"; // URLs carry ?v=<version>
            return Results.Bytes(jpeg, "image/jpeg");
        });

        // ---- App (desktop window only) ----
        app.MapGet("/api/settings", () => Results.Json(SettingsDto.From(settings), Json));
        app.MapPut("/api/settings", async (HttpContext ctx) =>
        {
            if (!Privileged(ctx)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var dto = await ctx.Request.ReadFromJsonAsync<SettingsDto>(Json);
            if (dto == null) return Results.BadRequest(new { error = "Missing settings." });
            if (dto.Validate() is { } error) return Results.BadRequest(new { error });
            dto.ApplyTo(settings);
            settings.Save();
            return Results.Json(new { saved = true, restartRequired = true }, Json);
        });
        app.MapPost("/api/app/restart", (HttpContext ctx) =>
        {
            if (!Privileged(ctx)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            requestRestart();
            return Results.Ok();
        });

        await app.StartAsync();
        var port = new Uri(app.Urls.First().Replace("0.0.0.0", "127.0.0.1")).Port;
        server = new ApiServer(app, port, remote, warning);
        return server;
    }

    /// <summary>Server-Sent Events: pushes the state JSON whenever it changes (and at least once a second for receiver counts).</summary>
    private static async Task StreamEventsAsync(HttpContext ctx, PresentationController controller)
    {
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        var ct = ctx.RequestAborted;
        var signal = new SemaphoreSlim(0);
        void Changed() { if (signal.CurrentCount == 0) signal.Release(); }
        controller.StateChanged += Changed;
        try
        {
            var last = "";
            while (!ct.IsCancellationRequested)
            {
                var json = JsonSerializer.Serialize(controller.GetState(), Json);
                if (json != last)
                {
                    last = json;
                    await ctx.Response.WriteAsync($"data: {json}\n\n", ct);
                    await ctx.Response.Body.FlushAsync(ct);
                }
                await signal.WaitAsync(1000, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        finally
        {
            controller.StateChanged -= Changed;
        }
    }

    /// <summary>Server-Sent Events with the audio level (linear peak per channel) for the meter, ~16 times a second while there's sound.</summary>
    private static async Task StreamMeterAsync(HttpContext ctx, AudioRouter audio)
    {
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        var ct = ctx.RequestAborted;
        var wasActive = true;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var (l, r) = audio.GetLevels();
                var active = l > 0.0005f || r > 0.0005f;
                if (active || wasActive)
                {
                    var json = string.Create(CultureInfo.InvariantCulture, $"{{\"l\":{l:0.0000},\"r\":{r:0.0000}}}");
                    await ctx.Response.WriteAsync($"data: {json}\n\n", ct);
                    await ctx.Response.Body.FlushAsync(ct);
                }
                wasActive = active;
                await Task.Delay(60, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    /// <summary>Motion-JPEG stream of an output (slide or notes) for the UI monitors.</summary>
    private static async Task StreamMjpegAsync(HttpContext ctx, PresentationController controller, string which, int? w)
    {
        var notes = which is "notes.mjpg" or "notes";
        if (!notes && which is not ("slide.mjpg" or "slide"))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var width = Math.Clamp(w ?? 960, 160, 1920);
        var ct = ctx.RequestAborted;
        ctx.Response.ContentType = "multipart/x-mixed-replace; boundary=frame";
        ctx.Response.Headers.CacheControl = "no-cache, no-store";
        var body = ctx.Response.Body;
        try
        {
            await body.WriteAsync("--frame\r\n"u8.ToArray(), ct);
            long lastVersion = -1;
            byte[]? last = null;
            var lastSent = DateTime.MinValue;
            async Task Send(byte[] jpeg)
            {
                var header = Encoding.ASCII.GetBytes($"Content-Type: image/jpeg\r\nContent-Length: {jpeg.Length}\r\n\r\n");
                await body.WriteAsync(header, ct);
                await body.WriteAsync(jpeg, ct);
                await body.WriteAsync("\r\n--frame\r\n"u8.ToArray(), ct);
                await body.FlushAsync(ct);
                lastSent = DateTime.UtcNow;
            }

            while (!ct.IsCancellationRequested)
            {
                var version = controller.FrameVersion;
                if (version != lastVersion)
                {
                    lastVersion = version;
                    using var bmp = notes ? controller.GetNotesPreview(width) : controller.GetSlidePreview(width);
                    if (bmp != null)
                    {
                        last = FrameRenderer.EncodeJpeg(bmp, 82);
                        // Browsers display an MJPEG part only once the next one starts arriving,
                        // so send each new frame twice to show it immediately.
                        await Send(last);
                        await Send(last);
                    }
                }
                else if (last != null && DateTime.UtcNow - lastSent > TimeSpan.FromSeconds(1))
                {
                    await Send(last); // keep-alive
                }
                await Task.Delay(40, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    private static async Task<IResult> RunLoad(PresentationController controller, string? url)
    {
        try
        {
            await controller.LoadAsync(url);
            return Results.Json(controller.GetState(), Json);
        }
        catch (Exception ex)
        {
            return Results.Json(new { error = ex.Message }, Json, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static IResult Png(Bitmap? bmp)
    {
        if (bmp == null) return Results.NotFound();
        using (bmp)
        {
            var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return Results.Bytes(ms.ToArray(), "image/png");
        }
    }

    private static IResult Asset(string name, string contentType)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("wwwroot/" + name);
        if (stream == null) return Results.NotFound();
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return Results.Bytes(ms.ToArray(), contentType);
    }

    private static IEnumerable<string> LanAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            // Skip virtual adapters (WSL, Hyper-V, host-only) — real networks have a gateway.
            .Where(n => n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !a.ToString().StartsWith("169.254."))
            .Select(a => a.ToString())
            .Distinct();

    private sealed record LoadRequest(string? Url);

    private sealed record AudioDto(string? OutputDevice, int OutputVolume, string? MonitorDevice, int MonitorVolume);

    private sealed class SettingsDto
    {
        public string SlidesNdiName { get; set; } = "";
        public string NotesNdiName { get; set; } = "";
        public string NdiDiscoveryServer { get; set; } = "";
        public int ApiPort { get; set; }
        public bool RemoteAccess { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int FrameRate { get; set; }

        // Read-only here: audio is changed live through PUT /api/audio.
        public string AudioOutputDevice { get; set; } = "";
        public int AudioOutputVolume { get; set; }
        public string AudioMonitorDevice { get; set; } = "";
        public int AudioMonitorVolume { get; set; }

        public static SettingsDto From(AppSettings s) => new()
        {
            SlidesNdiName = s.SlidesNdiName, NotesNdiName = s.NotesNdiName, NdiDiscoveryServer = s.NdiDiscoveryServer, ApiPort = s.ApiPort,
            RemoteAccess = s.RemoteAccess, Width = s.Width, Height = s.Height, FrameRate = s.FrameRate,
            AudioOutputDevice = s.AudioOutputDevice, AudioOutputVolume = s.AudioOutputVolume,
            AudioMonitorDevice = s.AudioMonitorDevice, AudioMonitorVolume = s.AudioMonitorVolume,
        };

        public string? Validate()
        {
            if (string.IsNullOrWhiteSpace(SlidesNdiName) || string.IsNullOrWhiteSpace(NotesNdiName))
                return "NDI source names can't be empty.";
            if (Ndi.NdiConfig.Normalise(NdiDiscoveryServer) == null)
                return "Discovery server should be an address like 192.168.1.10 or ndi-ds.local:5959. Separate several with commas.";
            if (SlidesNdiName.Trim() == NotesNdiName.Trim())
                return "The two NDI sources need different names.";
            if (ApiPort is < 1024 or > 65535) return "Port must be between 1024 and 65535.";
            if (Width is < 640 or > 3840 || Height is < 360 or > 2160) return "Unsupported resolution.";
            if (FrameRate is < 1 or > 60) return "Frame rate must be between 1 and 60.";
            return null;
        }

        public void ApplyTo(AppSettings s)
        {
            s.SlidesNdiName = SlidesNdiName.Trim();
            s.NotesNdiName = NotesNdiName.Trim();
            s.NdiDiscoveryServer = Ndi.NdiConfig.Normalise(NdiDiscoveryServer) ?? "";
            s.ApiPort = ApiPort;
            s.RemoteAccess = RemoteAccess;
            s.Width = Width;
            s.Height = Height;
            s.FrameRate = FrameRate;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
