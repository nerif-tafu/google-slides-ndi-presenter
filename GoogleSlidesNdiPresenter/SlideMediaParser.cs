using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GoogleSlidesNdiPresenter;

/// <summary>
/// A video or audio clip on a slide, as set up in Google Slides.
/// Start is "automatic", "on click" or "manual"; Muted is whether the clip starts muted.
/// Source is "drive" or "youtube" (FileId is then the Drive file id or the YouTube video id).
/// Access says whether it will play here: "ok", "no access" (a Drive file not shared with this
/// account), "private" (a private or removed YouTube video), "not embeddable" (YouTube video whose
/// owner turned off embedding), or null if it couldn't be checked.
/// </summary>
public sealed record SlideMedia(int Slide, string PageId, string Kind, string? Name, string? FileId, string Start, bool Muted,
    string Source = "drive", string? Access = null);

/// <summary>
/// Reads the audio and video on each slide from the model data embedded in the deck's page
/// (the same page the title and revision come from). Google doesn't document this format, so
/// everything here is best effort: anything unexpected is skipped rather than failing the load.
///
/// What the model looks like (one entry per object, entries are arrays):
///   [12, pageId, index, kind, ...]          a page; kind 0 = slide (1 layout, 2 master), index = slide order
///   [3, shapeId, type, transform, props, pageId]  an object on a page; type 157 = video;
///                                            props are key/value pairs: 57 = video source (1 YouTube,
///                                            2 Drive), 58 = Drive file id or YouTube video id,
///                                            119 = muted (video), 124 = plays automatically (audio)
///   [44, shapeId, fileName, ...]             the file name of a video or audio object
///   [35, shapeId, 0, [0, 19, 1, trigger], pageId]  "play" animation; trigger 1 = on click,
///                                            2/3 = with/after previous (automatic); none = manual
/// </summary>
public static partial class SlideMediaParser
{
    [GeneratedRegex(@"var modelChunk = (\{.*?\});\s*(?:var|DOCS_modelChunkLoadStart|</script>)", RegexOptions.Singleline)]
    private static partial Regex ChunkRegex();

    public static IReadOnlyList<SlideMedia> Parse(string html)
    {
        try
        {
            var entries = new List<JsonArray>();
            foreach (Match m in ChunkRegex().Matches(html))
            {
                try
                {
                    if (JsonNode.Parse(m.Groups[1].Value)?["chunk"] is JsonArray chunk)
                        entries.AddRange(chunk.OfType<JsonArray>());
                }
                catch
                {
                    // A chunk we can't read: skip it.
                }
            }

            // Slides in order; their index becomes the slide number the player shows.
            var slideNumbers = entries
                .Where(e => Int(e, 0) == 12 && Int(e, 3) == 0 && Str(e, 1) != null)
                .OrderBy(e => Int(e, 2) ?? int.MaxValue)
                .Select((e, i) => (Id: Str(e, 1)!, Number: i + 1))
                .ToDictionary(p => p.Id, p => p.Number);

            var names = entries.Where(e => Int(e, 0) == 44 && Str(e, 1) != null)
                .GroupBy(e => Str(e, 1)!)
                .ToDictionary(g => g.Key, g => Str(g.First(), 2));

            var triggers = entries.Where(e => Int(e, 0) == 35 && Str(e, 1) != null)
                .GroupBy(e => Str(e, 1)!)
                .ToDictionary(g => g.Key, g => g.First()[3] is JsonArray a && a.Count > 3 ? (int?)a[3]?.GetValue<int>() : null);

            var media = new List<SlideMedia>();
            foreach (var e in entries.Where(e => Int(e, 0) == 3))
            {
                var id = Str(e, 1);
                var pageId = Str(e, 5);
                if (id == null || pageId == null || !slideNumbers.TryGetValue(pageId, out var slide)) continue;
                if (e[4] is not JsonArray propList) continue;
                var props = Props(propList);
                var type = Int(e, 2);
                var fileId = props.TryGetValue(58, out var f) ? AsString(f) : null;

                if (type == 157)
                {
                    var start = triggers.TryGetValue(id, out var t)
                        ? (t == 1 ? "on click" : "automatic")
                        : "manual";
                    var muted = props.TryGetValue(119, out var mu) && AsInt(mu) == 1;
                    var source = props.TryGetValue(57, out var src) && AsInt(src) == 1 ? "youtube" : "drive";
                    media.Add(new SlideMedia(slide, pageId, "video", names.GetValueOrDefault(id), fileId, start, muted, source));
                }
                else if (fileId != null && names.ContainsKey(id))
                {
                    // Audio: a file-backed object that isn't a video. Audio has no mute option in Slides.
                    var auto = props.TryGetValue(124, out var a) && AsInt(a) == 1;
                    media.Add(new SlideMedia(slide, pageId, "audio", names.GetValueOrDefault(id), fileId, auto ? "automatic" : "on click", false));
                }
            }
            return media.OrderBy(m => m.Slide).ThenBy(m => m.Kind == "video" ? 0 : 1).ToList();
        }
        catch
        {
            return [];
        }
    }

    private static Dictionary<int, JsonNode?> Props(JsonArray list)
    {
        var props = new Dictionary<int, JsonNode?>();
        for (var i = 0; i + 1 < list.Count; i += 2)
            if (AsInt(list[i]) is int key) props.TryAdd(key, list[i + 1]);
        return props;
    }

    private static int? Int(JsonArray e, int i) => i < e.Count ? AsInt(e[i]) : null;
    private static string? Str(JsonArray e, int i) => i < e.Count ? AsString(e[i]) : null;

    private static int? AsInt(JsonNode? n)
    {
        try { return n is JsonValue v && v.TryGetValue<double>(out var d) ? (int)d : null; }
        catch { return null; }
    }

    private static string? AsString(JsonNode? n)
    {
        try { return n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null; }
        catch { return null; }
    }
}
