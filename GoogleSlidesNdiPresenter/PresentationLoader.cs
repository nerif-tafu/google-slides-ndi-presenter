using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Windows.Data.Pdf;
using Windows.Storage.Streams;

namespace GoogleSlidesNdiPresenter;

public sealed record LoadedPresentation(string Id, string Title, IReadOnlyList<byte[]> SlidePngs, IReadOnlyList<string> Notes, long? Revision, IReadOnlyList<SlideMedia> Media);

/// <summary>
/// Downloads a Google Slides presentation without OAuth by using Google's export endpoints:
/// the PDF export provides the slide images and the PPTX export provides the speaker notes.
/// The presentation must be shared as "Anyone with the link can view", or with the Google
/// account signed in through <see cref="Google"/>, whose cookies are then sent along.
/// </summary>
public static partial class PresentationLoader
{
    private static readonly GoogleCookieHandler Cookies = new();
    private static readonly HttpClient Http = CreateClient();

    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";

    /// <summary>The signed-in Google account, if the app has one.</summary>
    public interface IGoogleSession
    {
        bool SignedIn { get; }
        string? Email { get; }
        Task<IReadOnlyList<Cookie>> GetCookiesAsync();
    }

    public static IGoogleSession? Google { get; set; }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(Cookies) { Timeout = TimeSpan.FromSeconds(120) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) GoogleSlidesNdiPresenter/1.0");
        return client;
    }

    [GeneratedRegex(@"/presentation/d/([a-zA-Z0-9_-]{20,})")]
    private static partial Regex UrlIdRegex();

    [GeneratedRegex(@"^[a-zA-Z0-9_-]{20,}$")]
    private static partial Regex BareIdRegex();

    /// <summary>Extracts the presentation ID from an edit/view URL or a bare ID.</summary>
    public static string? ExtractId(string urlOrId)
    {
        urlOrId = urlOrId.Trim();
        if (urlOrId.Contains("/presentation/d/e/"))
            return null; // "Publish to web" URLs can't be exported; need the normal share link.
        var m = UrlIdRegex().Match(urlOrId);
        if (m.Success) return m.Groups[1].Value;
        return BareIdRegex().IsMatch(urlOrId) ? urlOrId : null;
    }

    public static async Task<LoadedPresentation> LoadAsync(string url, int width, int height, CancellationToken ct = default)
    {
        var id = ExtractId(url) ?? throw new ArgumentException(
            "That doesn't look like a Google Slides link. Use the normal share link " +
            "(https://docs.google.com/presentation/d/<id>/edit).");

        await Cookies.SyncAsync(Google);
        var baseUrl = $"https://docs.google.com/presentation/d/{id}/export";
        var pageTask = FetchPageInfoAsync(id, ct);
        var mediaTask = CheckMediaAsync(pageTask, ct);
        var pdfTask = DownloadAsync($"{baseUrl}/pdf", "%PDF"u8.ToArray(), ct);
        var pptxTask = DownloadAsync($"{baseUrl}/pptx", "PK"u8.ToArray(), ct);
        await Task.WhenAll(pdfTask, pptxTask);

        var slides = await RenderPdfAsync(pdfTask.Result, width, height);
        var notesInfo = ParsePptxNotes(pptxTask.Result);
        var page = await pageTask;
        var media = await mediaTask;
        var title = page.Title ?? ParsePptxTitle(pptxTask.Result);

        // Align notes to rendered pages. Depending on Google's export, skipped slides may or may
        // not appear in the PDF, so match whichever count lines up.
        List<string> notes;
        var visible = notesInfo.Where(n => !n.Hidden).Select(n => n.Notes).ToList();
        if (notesInfo.Count == slides.Count)
            notes = notesInfo.Select(n => n.Notes).ToList();
        else if (visible.Count == slides.Count)
            notes = visible;
        else
            notes = Enumerable.Range(0, slides.Count)
                .Select(i => i < notesInfo.Count ? notesInfo[i].Notes : "").ToList();

        return new LoadedPresentation(id, string.IsNullOrWhiteSpace(title) ? "Untitled presentation" : title, slides, notes, page.Revision, media);
    }

    private static async Task<IReadOnlyList<SlideMedia>> CheckMediaAsync(Task<PageInfo> pageTask, CancellationToken ct)
    {
        var page = await pageTask;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var checks = page.Media
            .Where(m => m.FileId != null)
            .Select(m => (m.Source, Id: m.FileId!))
            .Distinct()
            .ToDictionary(k => k, k => CheckAccessAsync(k.Source, k.Id, timeout.Token));
        try { await Task.WhenAll(checks.Values); } catch { /* individual failures are reported as null */ }
        return page.Media
            .Select(m => m.FileId != null && checks.TryGetValue((m.Source, m.FileId), out var t) && t.IsCompletedSuccessfully
                ? m with { Access = t.Result }
                : m)
            .ToList();
    }

    /// <summary>
    /// Whether a clip will play for the signed-in account (or anyone, when signed out). Slides plays
    /// Drive videos and audio only for people who can open the file itself, whoever can open the deck.
    /// </summary>
    private static async Task<string?> CheckAccessAsync(string source, string fileId, CancellationToken ct)
    {
        if (source == "youtube")
        {
            // oEmbed answers 401 when embedding is turned off and 403/404 for private or removed videos.
            var url = "https://www.youtube.com/oembed?format=json&url=" +
                Uri.EscapeDataString("https://www.youtube.com/watch?v=" + fileId);
            using var yt = await Http.GetAsync(url, ct);
            return (int)yt.StatusCode switch
            {
                200 => "ok",
                401 => "not embeddable",
                403 or 404 => "private",
                _ => null,
            };
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://drive.google.com/file/d/{Uri.EscapeDataString(fileId)}/view");
        req.Headers.AcceptLanguage.ParseAdd("en-US");
        using var resp = await Http.SendAsync(req, ct);
        var status = (int)resp.StatusCode;
        if (status is 401 or 403 or 404) return "no access";
        if (status != 200) return null;
        // A sign-in page instead of the file also means no access.
        var host = resp.RequestMessage?.RequestUri?.Host ?? "";
        return host.StartsWith("accounts.", StringComparison.OrdinalIgnoreCase) ? "no access" : "ok";
    }

    private static async Task<byte[]> DownloadAsync(string url, byte[] magic, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, ct);
        if (resp.StatusCode is HttpStatusCode.NotFound)
            throw new InvalidOperationException(Google?.SignedIn == true
                ? "Presentation not found. Check the link, and that it's shared with " + AccountName() + "."
                : "Presentation not found (404). Check the link.");
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw NoAccess();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Google returned {(int)resp.StatusCode} {resp.ReasonPhrase}. Try again in a moment.");

        // Without access Google answers with a sign-in or "request access" page instead of the file.
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length < magic.Length || !bytes.AsSpan(0, magic.Length).SequenceEqual(magic))
            throw NoAccess();
        return bytes;
    }

    private static string AccountName() => Google?.Email ?? "the Google account you're signed in with";

    private static InvalidOperationException NoAccess() => new(Google?.SignedIn == true
        ? $"{AccountName()} can't open this presentation. Ask the owner to share it with that account, " +
          "or to share it as \"Anyone with the link can view\"."
        : "This presentation isn't shared publicly. Share it as \"Anyone with the link can view\", " +
          "or sign in to a Google account that can open it (Settings → Google account).");

    /// <summary>
    /// Sends the signed-in account's Google cookies with every request, redirects included.
    /// Redirects are followed here so the cookie jar can be swapped between loads (sign in/out).
    /// </summary>
    private sealed class GoogleCookieHandler() : DelegatingHandler(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false })
    {
        private volatile CookieContainer _jar = new();

        public async Task SyncAsync(IGoogleSession? session)
        {
            var jar = new CookieContainer();
            if (session?.SignedIn == true)
            {
                foreach (var c in await session.GetCookiesAsync())
                {
                    try { jar.Add(c); } catch (CookieException) { /* skip odd cookies */ }
                }
            }
            _jar = jar;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            for (var hop = 0; ; hop++)
            {
                var jar = _jar;
                var uri = request.RequestUri!;
                request.Headers.Remove("Cookie");
                if (jar.GetCookieHeader(uri) is { Length: > 0 } header) request.Headers.Add("Cookie", header);

                var resp = await base.SendAsync(request, ct);
                if (resp.Headers.TryGetValues("Set-Cookie", out var setCookies))
                {
                    foreach (var sc in setCookies)
                    {
                        try { jar.SetCookies(uri, sc); } catch (CookieException) { /* ignore */ }
                    }
                }

                var status = (int)resp.StatusCode;
                if (status is not (301 or 302 or 303 or 307 or 308) || resp.Headers.Location is not { } location || hop >= 10)
                    return resp;
                resp.Dispose();
                var next = new HttpRequestMessage(status == 303 ? HttpMethod.Get : request.Method, new Uri(uri, location));
                foreach (var h in request.Headers.Where(h => h.Key != "Cookie")) next.Headers.TryAddWithoutValidation(h.Key, h.Value);
                request = next;
            }
        }
    }

    private static async Task<List<byte[]>> RenderPdfAsync(byte[] pdf, int width, int height)
    {
        using var input = new InMemoryRandomAccessStream();
        var writer = input.AsStreamForWrite();
        await writer.WriteAsync(pdf);
        await writer.FlushAsync();
        input.Seek(0);

        var doc = await PdfDocument.LoadFromStreamAsync(input);
        var result = new List<byte[]>((int)doc.PageCount);
        for (uint i = 0; i < doc.PageCount; i++)
        {
            using var page = doc.GetPage(i);
            var size = page.Size;
            var scale = Math.Min(width / size.Width, height / size.Height);
            var options = new PdfPageRenderOptions
            {
                DestinationWidth = (uint)Math.Max(1, Math.Round(size.Width * scale)),
                DestinationHeight = (uint)Math.Max(1, Math.Round(size.Height * scale)),
            };

            using var output = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(output, options); // PNG by default
            output.Seek(0);
            using var ms = new MemoryStream();
            await output.AsStreamForRead().CopyToAsync(ms);
            result.Add(ms.ToArray());
        }
        return result;
    }

    private sealed record SlideNotes(bool Hidden, string Notes);

    [GeneratedRegex(@"<title>(.*?)</title>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex TitleRegex();

    [GeneratedRegex(@"""revision"":(\d+)")]
    private static partial Regex RevisionRegex();

    /// <summary>
    /// The deck's current revision number, which Google bumps on every edit. Null if the page
    /// couldn't be read. Used to spot decks that changed since they were loaded.
    /// </summary>
    public static async Task<long?> FetchRevisionAsync(string id, CancellationToken ct = default) =>
        (await FetchPageInfoAsync(id, ct)).Revision;

    private sealed record PageInfo(string? Title, long? Revision, IReadOnlyList<SlideMedia> Media);

    /// <summary>
    /// Reads the presentation's normal page for its name (from the page title, "Name - Google Slides")
    /// and its revision number (the page embeds "revision":N in its model data).
    /// </summary>
    private static async Task<PageInfo> FetchPageInfoAsync(string id, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://docs.google.com/presentation/d/{id}/edit");
            req.Headers.AcceptLanguage.ParseAdd("en-US");
            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return new(null, null, []);
            var html = await resp.Content.ReadAsStringAsync(ct);
            return new(ParseTitle(html), ParseRevision(html), SlideMediaParser.Parse(html));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new(null, null, []);
        }
    }

    private static string? ParseTitle(string html)
    {
        var m = TitleRegex().Match(html);
        if (!m.Success) return null;
        var title = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
        // Drop the product suffix, which Google localises ("- Google Slides", "- Presentaciones de Google", …).
        var cut = title.LastIndexOf(" - ", StringComparison.Ordinal);
        if (cut > 0 && title[(cut + 3)..].Contains("Google", StringComparison.OrdinalIgnoreCase)) title = title[..cut];
        return title.Length == 0 || title.Equals("Google Slides", StringComparison.OrdinalIgnoreCase) ? null : title;
    }

    // The page repeats the revision once per model chunk; take the highest.
    private static long? ParseRevision(string html)
    {
        long? revision = null;
        foreach (Match m in RevisionRegex().Matches(html))
            if (long.TryParse(m.Groups[1].ValueSpan, out var r) && r > (revision ?? -1)) revision = r;
        return revision;
    }

    private static string? ParsePptxTitle(byte[] pptx)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(pptx), ZipArchiveMode.Read);
            XNamespace dc = "http://purl.org/dc/elements/1.1/";
            return LoadXml(zip, "docProps/core.xml")?.Root?.Element(dc + "title")?.Value.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static List<SlideNotes> ParsePptxNotes(byte[] pptx)
    {
        using var zip = new ZipArchive(new MemoryStream(pptx), ZipArchiveMode.Read);

        var presentation = LoadXml(zip, "ppt/presentation.xml")
            ?? throw new InvalidOperationException("Invalid PPTX export (missing presentation.xml).");
        var presRels = LoadRels(zip, "ppt/presentation.xml");

        var result = new List<SlideNotes>();
        var slideIds = presentation.Root?.Element(P + "sldIdLst")?.Elements(P + "sldId") ?? [];
        foreach (var sldId in slideIds)
        {
            var rid = (string?)sldId.Attribute(R + "id");
            if (rid == null || !presRels.TryGetValue(rid, out var slidePath)) continue;

            var slide = LoadXml(zip, slidePath);
            var hidden = (string?)slide?.Root?.Attribute("show") is "0" or "false";

            var notes = "";
            var notesPath = LoadRels(zip, slidePath)
                .Values.FirstOrDefault(p => p.Contains("notesSlides/", StringComparison.OrdinalIgnoreCase));
            if (notesPath != null && LoadXml(zip, notesPath) is { } notesXml)
                notes = ExtractNotesText(notesXml);

            result.Add(new SlideNotes(hidden, notes));
        }
        return result;
    }

    private static string ExtractNotesText(XDocument notesXml)
    {
        // The speaker notes live in the shape whose placeholder type is "body".
        var bodyShape = notesXml.Descendants(P + "sp").FirstOrDefault(sp =>
            (string?)sp.Element(P + "nvSpPr")?.Element(P + "nvPr")?.Element(P + "ph")?.Attribute("type") == "body");
        var txBody = bodyShape?.Element(P + "txBody");
        if (txBody == null) return "";

        var sb = new StringBuilder();
        foreach (var para in txBody.Elements(A + "p"))
        {
            if (sb.Length > 0) sb.Append('\n');
            foreach (var node in para.Elements())
            {
                if (node.Name == A + "r" || node.Name == A + "fld")
                    sb.Append((string?)node.Element(A + "t"));
                else if (node.Name == A + "br")
                    sb.Append('\n');
            }
        }
        return sb.ToString().Trim();
    }

    private static XDocument? LoadXml(ZipArchive zip, string path)
    {
        var entry = zip.GetEntry(path);
        if (entry == null) return null;
        using var s = entry.Open();
        return XDocument.Load(s);
    }

    /// <summary>Returns relationship Id -> absolute part path for the given part.</summary>
    private static Dictionary<string, string> LoadRels(ZipArchive zip, string partPath)
    {
        var dir = Path.GetDirectoryName(partPath)?.Replace('\\', '/') ?? "";
        var relsPath = $"{dir}/_rels/{Path.GetFileName(partPath)}.rels";
        var rels = new Dictionary<string, string>();
        var doc = LoadXml(zip, relsPath);
        if (doc?.Root == null) return rels;

        foreach (var r in doc.Root.Elements(Rel + "Relationship"))
        {
            var id = (string?)r.Attribute("Id");
            var target = (string?)r.Attribute("Target");
            if (id == null || target == null || (string?)r.Attribute("TargetMode") == "External") continue;
            rels[id] = ResolvePath(dir, target);
        }
        return rels;
    }

    private static string ResolvePath(string baseDir, string target)
    {
        if (target.StartsWith('/')) return target.TrimStart('/');
        var parts = new List<string>(baseDir.Split('/', StringSplitOptions.RemoveEmptyEntries));
        foreach (var seg in target.Split('/'))
        {
            if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else if (seg != "." && seg != "") parts.Add(seg);
        }
        return string.Join('/', parts);
    }
}
