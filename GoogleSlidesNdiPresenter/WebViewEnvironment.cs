using Microsoft.Web.WebView2.Core;

namespace GoogleSlidesNdiPresenter;

/// <summary>
/// One WebView2 environment shared by the main window UI and the offscreen live player
/// (WebViews that share a user data folder must be created with identical options).
/// </summary>
public static class WebViewEnvironment
{
    private static Task<CoreWebView2Environment>? _env;

    /// <summary>Creates (once) and returns the shared environment. Call on the UI thread.</summary>
    public static Task<CoreWebView2Environment> GetAsync()
    {
        return _env ??= Create();

        static Task<CoreWebView2Environment> Create()
        {
            var userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GoogleSlidesNdiPresenter", "WebView2");
            var options = new CoreWebView2EnvironmentOptions(
                "--autoplay-policy=no-user-gesture-required " +
                "--disable-features=CalculateNativeWinOcclusion " +
                "--disable-background-timer-throttling " +
                "--disable-renderer-backgrounding " +
                "--disable-backgrounding-occluded-windows");
            return CoreWebView2Environment.CreateAsync(null, userData, options);
        }
    }
}
