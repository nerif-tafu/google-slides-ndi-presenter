using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace GoogleSlidesNdiPresenter;

/// <summary>
/// The optional Google sign-in, used to open decks that are shared with a person rather than
/// "anyone with the link". The user signs in to Google in a normal WebView2 window that uses its
/// own browser profile; the live player runs in the same profile, so it sees the deck too, and the
/// PDF/PPTX downloads borrow that profile's Google cookies. Nothing is stored by the app itself:
/// the session lives in the WebView2 profile, whose cookies Chromium encrypts with Windows DPAPI.
/// All public members are thread-safe.
/// </summary>
public sealed partial class GoogleAccount : PresentationLoader.IGoogleSession
{
    private const string ServiceUrl = "https://docs.google.com/presentation/";
    private const string SignInUrl =
        "https://accounts.google.com/ServiceLogin?service=wise&passive=1209600&continue=" + ServiceUrl + "u/0/";

    private Control? _ui;
    private CoreWebView2Environment? _env;
    private CoreWebView2Controller? _cookieHost; // hidden, only used for its cookie manager
    private SignInForm? _signIn;
    private volatile string? _email;
    private volatile bool _signedIn;

    /// <summary>
    /// The WebView2 profile holding the Google session. Each settings file gets its own, so a second
    /// copy of the app (started with --settings) can use another account, or none.
    /// </summary>
    public static string ProfileName { get; } = MakeProfileName();

    private static string MakeProfileName()
    {
        if (AppSettings.FilePath == AppSettings.DefaultFilePath) return "Google";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(AppSettings.FilePath.ToLowerInvariant()));
        return "Google-" + Convert.ToHexString(hash, 0, 6);
    }

    public bool SignedIn => _signedIn;

    /// <summary>The signed-in account's address, when Google reports it.</summary>
    public string? Email => _email;

    /// <summary>Raised (on any thread) when the account signs in or out.</summary>
    public event Action? Changed;

    /// <summary>Options that put a WebView in the Google profile. Call on the UI thread.</summary>
    public static CoreWebView2ControllerOptions ControllerOptions(CoreWebView2Environment env)
    {
        var options = env.CreateCoreWebView2ControllerOptions();
        options.ProfileName = ProfileName;
        return options;
    }

    /// <summary>Reads the saved session (if any). Call once on the UI thread.</summary>
    public async Task InitializeAsync(Control ui, CoreWebView2Environment env)
    {
        _ui = ui;
        _env = env;
        _cookieHost = await env.CreateCoreWebView2ControllerAsync(ui.Handle, ControllerOptions(env));
        _cookieHost.IsVisible = false;
        await RefreshStatusAsync();
    }

    /// <summary>
    /// Opens the sign-in window (or brings it forward) and completes when it closes.
    /// Returns an error message, or null when the account is signed in.
    /// </summary>
    public Task<string?> SignInAsync() => OnUiAsync(async () =>
    {
        if (_env == null) return "The sign-in window needs the WebView2 runtime.";
        if (_signIn != null)
        {
            _signIn.Activate();
            return await _signIn.Result;
        }

        // One account at a time: start from a clean slate so Google doesn't pick another one.
        await ClearSessionAsync();
        _signIn = new SignInForm(_env, _ui as Form);
        try
        {
            _signIn.Show(_ui as Form);
            var error = await _signIn.Result;
            await RefreshStatusAsync();
            return error ?? (_signedIn ? null : "The sign-in window was closed before you finished signing in.");
        }
        finally
        {
            _signIn.Dispose();
            _signIn = null;
        }
    });

    public Task SignOutAsync() => OnUiAsync(async () =>
    {
        await ClearSessionAsync();
        await RefreshStatusAsync();
        return (string?)null;
    });

    private async Task ClearSessionAsync()
    {
        if (_cookieHost == null) return;
        var profile = _cookieHost.CoreWebView2.Profile;
        profile.CookieManager.DeleteAllCookies();
        await profile.ClearBrowsingDataAsync(
            CoreWebView2BrowsingDataKinds.AllDomStorage | CoreWebView2BrowsingDataKinds.Cookies |
            CoreWebView2BrowsingDataKinds.CacheStorage | CoreWebView2BrowsingDataKinds.DiskCache);
    }

    /// <summary>The Google cookies of the signed-in session (empty when signed out).</summary>
    public Task<IReadOnlyList<Cookie>> GetCookiesAsync() => OnUiAsync(GetCookiesOnUiAsync);

    private async Task<IReadOnlyList<Cookie>> GetCookiesOnUiAsync()
    {
        if (_cookieHost == null) return [];
        var all = await _cookieHost.CoreWebView2.CookieManager.GetCookiesAsync("");
        return all.Where(c => IsGoogleDomain(c.Domain)).Select(c => c.ToSystemNetCookie()).ToList();
    }

    private static bool IsGoogleDomain(string domain)
    {
        domain = domain.TrimStart('.');
        return domain is "google.com" || domain.EndsWith(".google.com", StringComparison.OrdinalIgnoreCase) ||
               domain.EndsWith(".googleusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    private async Task RefreshStatusAsync()
    {
        var cookies = await GetCookiesOnUiAsync();
        var wasSignedIn = _signedIn;
        var oldEmail = _email;
        // SID is Google's main session cookie; it's only there while an account is signed in.
        _signedIn = cookies.Any(c => c.Name is "SID" or "__Secure-1PSID" or "__Secure-3PSID");
        _email = _signedIn ? await Task.Run(() => FetchEmailAsync(cookies)) : null;
        if (wasSignedIn != _signedIn || oldEmail != _email) Changed?.Invoke();
    }

    [GeneratedRegex(@"[A-Za-z0-9._%+'-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailRegex();

    /// <summary>Asks Google which account the cookies belong to (the same request Chrome uses).</summary>
    private static async Task<string?> FetchEmailAsync(IReadOnlyList<Cookie> cookies)
    {
        try
        {
            var jar = new CookieContainer();
            foreach (var c in cookies) jar.Add(c);
            using var http = new HttpClient(new HttpClientHandler { CookieContainer = jar }) { Timeout = TimeSpan.FromSeconds(15) };
            using var content = new StringContent("", Encoding.UTF8, "application/x-www-form-urlencoded");
            using var resp = await http.PostAsync("https://accounts.google.com/ListAccounts?gpsia=1&source=ChromiumBrowser&json=standard", content);
            if (!resp.IsSuccessStatusCode) return null;
            var m = EmailRegex().Match(await resp.Content.ReadAsStringAsync());
            return m.Success ? m.Value : null;
        }
        catch
        {
            return null;
        }
    }

    private Task<T> OnUiAsync<T>(Func<Task<T>> action)
    {
        var ui = _ui;
        if (ui == null || ui.IsDisposed) return Task.FromResult(default(T)!);
        if (!ui.InvokeRequired) return action();
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        ui.BeginInvoke(async () =>
        {
            try { tcs.SetResult(await action()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    /// <summary>A plain window showing Google's sign-in page. It closes itself once the user is signed in.</summary>
    private sealed class SignInForm : Form
    {
        private readonly TaskCompletionSource<string?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
        private readonly CoreWebView2Environment _env;

        public Task<string?> Result => _result.Task;

        public SignInForm(CoreWebView2Environment env, Form? owner)
        {
            _env = env;
            Text = "Sign in to Google – Google Slides NDI Presenter";
            Icon = owner?.Icon;
            StartPosition = owner == null ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent;
            ClientSize = new Size(520, 720);
            MinimumSize = new Size(420, 560);
            Controls.Add(_web);
            Shown += async (_, _) => await StartAsync();
            FormClosed += (_, _) => _result.TrySetResult(null);
        }

        private async Task StartAsync()
        {
            try
            {
                await _web.EnsureCoreWebView2Async(_env, ControllerOptions(_env));
                var core = _web.CoreWebView2;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.DocumentTitleChanged += (_, _) => Text = core.DocumentTitle is { Length: > 0 } t ? t : Text;
                // Help and privacy links open in the default browser rather than in a popup.
                core.NewWindowRequested += (_, e) =>
                {
                    e.Handled = true;
                    if (e.Uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri) { UseShellExecute = true }); }
                        catch { /* ignore */ }
                    }
                };
                // Google sends you on to Slides once you're in; that's the cue to close.
                core.SourceChanged += (_, _) =>
                {
                    if (core.Source.StartsWith(ServiceUrl, StringComparison.OrdinalIgnoreCase))
                    {
                        _result.TrySetResult(null);
                        Close();
                    }
                };
                core.Navigate(SignInUrl);
            }
            catch (Exception ex)
            {
                _result.TrySetResult("The sign-in window couldn't open: " + ex.Message);
                Close();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _web.Dispose();
            base.Dispose(disposing);
        }
    }
}
