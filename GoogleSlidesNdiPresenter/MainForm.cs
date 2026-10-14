using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace GoogleSlidesNdiPresenter;

/// <summary>
/// The app window: a dark native frame hosting the web UI (served by <see cref="ApiServer"/>)
/// in WebView2. Also owns the offscreen live player.
/// </summary>
public sealed class MainForm : Form
{
    private static readonly Color Background = Color.FromArgb(8, 8, 11);

    private readonly PresentationController _controller;
    private readonly AppSettings _settings;
    private readonly ApiServer _api;
    private readonly AudioRouter _audio;
    private readonly GoogleAccount _google;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Background };
    private LivePresenter? _live;

    public MainForm(PresentationController controller, AppSettings settings, ApiServer api, AudioRouter audio, GoogleAccount google)
    {
        _google = google;
        _audio = audio;
        _controller = controller;
        _settings = settings;
        _api = api;

        Text = "Google Slides NDI Presenter";
        Icon = AppIcon.Create();
        BackColor = Background;
        MinimumSize = new Size(940, 620);
        StartPosition = FormStartPosition.Manual;
        RestorePlacement();
        Controls.Add(_web);

        Shown += async (_, _) => await StartAsync();
        // Keyboard shortcuts and presentation clickers should work as soon as the window is active.
        Activated += (_, _) => _web.Focus();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        DarkTitleBar.Apply(Handle, Background);
    }

    private async Task StartAsync()
    {
        try
        {
            var env = await WebViewEnvironment.GetAsync();
            await _web.EnsureCoreWebView2Async(env);
            ConfigureWebView(_web.CoreWebView2);
            _web.CoreWebView2.Navigate(_api.LocalUrl + "#token=" + _api.AppToken);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "The app window needs the Microsoft Edge WebView2 Runtime.\n\n" +
                "The controls will open in your web browser instead. Install WebView2 from microsoft.com/edge/webview2 to fix this.\n\n" + ex.Message,
                "WebView2 missing", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            OpenExternal(_api.LocalUrl);
        }

        try
        {
            await _google.InitializeAsync(this, await WebViewEnvironment.GetAsync());
        }
        catch
        {
            // Without WebView2 there's no Google sign-in; public decks still work.
        }

        try
        {
            _live = await LivePresenter.CreateAsync(_settings.Width, _settings.Height);
            _controller.AttachLive(_live);
            _live.AudioCaptured += _audio.Feed;
        }
        catch (Exception ex)
        {
            _controller.SetLiveError("The live player couldn't start (" + ex.Message + "), so slides are static images.");
        }

        if (!string.IsNullOrWhiteSpace(_settings.LastUrl))
        {
            try { await _controller.LoadAsync(_settings.LastUrl); }
            catch { /* reported in the UI via the state's status */ }
        }
    }

    private void ConfigureWebView(CoreWebView2 core)
    {
        var s = core.Settings;
        s.AreBrowserAcceleratorKeysEnabled = false; // no F5 / Ctrl+R / Ctrl+P / Ctrl+F in an app
        s.IsZoomControlEnabled = false;
        s.IsPinchZoomEnabled = false;
        s.IsStatusBarEnabled = false;
        s.IsSwipeNavigationEnabled = false;
        s.AreDevToolsEnabled = Debugger.IsAttached;

        // Only text fields get a context menu (cut/copy/paste); no "Back / Reload / Inspect".
        core.ContextMenuRequested += (_, e) =>
        {
            if (!e.ContextMenuTarget.IsEditable) e.Handled = true;
        };

        var origin = new Uri(_api.LocalUrl).GetLeftPart(UriPartial.Authority);

        // Links such as "Edit in Slides" open in the default browser.
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenExternal(e.Uri);
        };
        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith(origin, StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
                OpenExternal(e.Uri);
            }
        };

        // The open dialog offers a Slides link found on the clipboard.
        core.PermissionRequested += (_, e) =>
        {
            if (e.PermissionKind == CoreWebView2PermissionKind.ClipboardRead &&
                e.Uri.StartsWith(origin, StringComparison.OrdinalIgnoreCase))
                e.State = CoreWebView2PermissionState.Allow;
        };
    }

    private static void OpenExternal(string url)
    {
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    private void RestorePlacement()
    {
        var p = _settings.Window;
        var bounds = p == null ? Rectangle.Empty : new Rectangle(p.X, p.Y, p.Width, p.Height);
        if (p != null && Screen.AllScreens.Any(sc => sc.WorkingArea.IntersectsWith(bounds)) && p.Width >= 600 && p.Height >= 400)
        {
            Bounds = bounds;
            if (p.Maximized) WindowState = FormWindowState.Maximized;
        }
        else
        {
            var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1600, 900);
            var size = new Size(Math.Min(1440, area.Width - 80), Math.Min(900, area.Height - 80));
            Bounds = new Rectangle(area.X + (area.Width - size.Width) / 2, area.Y + (area.Height - size.Height) / 2, size.Width, size.Height);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        _settings.Window = new WindowPlacement
        {
            X = b.X, Y = b.Y, Width = b.Width, Height = b.Height,
            Maximized = WindowState == FormWindowState.Maximized,
        };
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _live?.Dispose();
        _web.Dispose();
        base.OnFormClosed(e);
    }

    /// <summary>Win11 dark caption that matches the app background (falls back to plain dark mode on Win10).</summary>
    private static class DarkTitleBar
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public static void Apply(IntPtr hwnd, Color caption)
        {
            int on = 1;
            DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));           // DWMWA_USE_IMMERSIVE_DARK_MODE
            int color = caption.R | (caption.G << 8) | (caption.B << 16);   // COLORREF
            DwmSetWindowAttribute(hwnd, 35, ref color, sizeof(int));        // DWMWA_CAPTION_COLOR
            int text = 0xE8E8EE;
            DwmSetWindowAttribute(hwnd, 36, ref text, sizeof(int));         // DWMWA_TEXT_COLOR
        }
    }
}

/// <summary>Draws the app icon (matches wwwroot/icon.svg).</summary>
internal static class AppIcon
{
    public static Icon Create()
    {
        using var bmp = new Bitmap(64, 64);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = RoundedRect(new RectangleF(0, 0, 64, 64), 15);
            using var bg = new LinearGradientBrush(new Point(0, 0), new Point(64, 64), Color.FromArgb(155, 140, 255), Color.FromArgb(91, 69, 240));
            g.FillPath(bg, path);
            using var pen = new Pen(Color.White, 3.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var screen = RoundedRect(new RectangleF(13, 16, 38, 25), 4);
            g.DrawPath(pen, screen);
            g.FillPolygon(Brushes.White, new PointF[] { new(28, 23.5f), new(28, 33.5f), new(36.5f, 28.5f) });
            g.DrawLine(pen, 24, 48, 40, 48);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
