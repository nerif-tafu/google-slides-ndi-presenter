using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace GoogleSlidesNdiPresenter;

/// <summary>The static parts of the presenter (notes) frame, plus where the live current slide goes.</summary>
public sealed class PresenterBackground : IDisposable
{
    public required Bitmap Bitmap { get; init; }
    public required RectangleF CurrentRect { get; init; }
    public required float Scroll { get; init; }
    public required float MaxScroll { get; init; }
    /// <summary>The notes font size actually used (differs from the requested size when auto-fit is on).</summary>
    public required float FontSize { get; init; }
    public void Dispose() => Bitmap.Dispose();
}

public enum HoldingScene { Slides, Presenter }

/// <summary>Renders the slide and presenter-notes output frames.</summary>
public static class FrameRenderer
{
    private static readonly Color Muted = Color.FromArgb(150, 150, 150);
    private static readonly Color Frame = Color.FromArgb(80, 80, 80);

    public static Bitmap RenderSlide(byte[]? png, int width, int height)
    {
        var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Black);
        if (png == null) return bmp;

        using var ms = new MemoryStream(png);
        using var img = Image.FromStream(ms);
        DrawFit(g, img, new RectangleF(0, 0, width, height), InterpolationMode.HighQualityBicubic);
        return bmp;
    }

    /// <summary>
    /// The frame sent while no presentation is open: a dark, softly lit background with the app
    /// mark and a short message (audience-friendly on the slides feed, instructional on the presenter view).
    /// </summary>
    public static Bitmap RenderHolding(HoldingScene scene, int width, int height)
    {
        var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        var u = width / 1920f;

        using (var bg = new LinearGradientBrush(new Point(0, 0), new Point(0, height), Color.FromArgb(12, 12, 17), Color.FromArgb(22, 21, 30)))
            g.FillRectangle(bg, 0, 0, width, height);

        // Soft accent glow behind the mark.
        var glow = new RectangleF(width / 2f - 620 * u, height / 2f - 470 * u, 1240 * u, 760 * u);
        using (var path = new GraphicsPath())
        {
            path.AddEllipse(glow);
            using var pg = new PathGradientBrush(path)
            {
                CenterColor = Color.FromArgb(46, 124, 108, 255),
                SurroundColors = [Color.FromArgb(0, 124, 108, 255)],
            };
            g.FillPath(pg, path);
        }

        // App mark: a screen with a play symbol.
        var cx = width / 2f;
        var markW = 150 * u; var markH = 98 * u;
        var markTop = height / 2f - 150 * u;
        using (var pen = new Pen(Color.FromArgb(200, 255, 255, 255), 9 * u) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            using var screen = RoundedRect(new RectangleF(cx - markW / 2, markTop, markW, markH), 16 * u);
            g.DrawPath(pen, screen);
            g.DrawLine(pen, cx - 32 * u, markTop + markH + 30 * u, cx + 32 * u, markTop + markH + 30 * u);
        }
        using (var white = new SolidBrush(Color.FromArgb(220, 255, 255, 255)))
            g.FillPolygon(white, new PointF[]
            {
                new(cx - 14 * u, markTop + 28 * u), new(cx - 14 * u, markTop + markH - 28 * u), new(cx + 22 * u, markTop + markH / 2),
            });

        var (title, sub) = scene == HoldingScene.Slides
            ? ("The presentation will begin shortly", "")
            : ("No presentation open", "Open a Google Slides deck in Google Slides NDI Presenter");

        using var center = new StringFormat { Alignment = StringAlignment.Center };
        using var titleFont = new Font("Segoe UI Semibold", 52 * u, FontStyle.Regular, GraphicsUnit.Pixel);
        using var titleBrush = new SolidBrush(Color.FromArgb(235, 255, 255, 255));
        var titleTop = markTop + markH + 90 * u;
        g.DrawString(title, titleFont, titleBrush, new RectangleF(0, titleTop, width, 80 * u), center);
        if (sub.Length > 0)
        {
            using var subFont = new Font("Segoe UI", 30 * u, FontStyle.Regular, GraphicsUnit.Pixel);
            using var subBrush = new SolidBrush(Color.FromArgb(140, 255, 255, 255));
            g.DrawString(sub, subFont, subBrush, new RectangleF(0, titleTop + 86 * u, width, 50 * u), center);
        }
        return bmp;
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

    /// <summary>Decodes a live-player frame, crops off the player's navbar, and fits it to the output size.</summary>
    public static Bitmap RenderLiveFrame(LiveFrame frame, int width, int height)
    {
        var bytes = Convert.FromBase64String(frame.Base64Jpeg);
        using var ms = new MemoryStream(bytes);
        using var img = Image.FromStream(ms);

        var cropH = (float)img.Height * frame.ContentHeight / frame.ViewportHeight;
        var src = new RectangleF(0, 0, img.Width, Math.Min(img.Height, cropH));

        var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Black);
        g.InterpolationMode = src.Width == width && Math.Abs(src.Height - height) < 1
            ? InterpolationMode.NearestNeighbor
            : InterpolationMode.Bilinear;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(img, FitRect(src.Size, new RectangleF(0, 0, width, height)), src, GraphicsUnit.Pixel);
        return bmp;
    }

    /// <summary>
    /// Renders the presenter view: current slide (filled in live by <see cref="ComposePresenter"/>)
    /// and next slide on the left, scrollable speaker notes on the right.
    /// </summary>
    public static PresenterBackground RenderPresenterBackground(
        string? notes, int index, int count, byte[]? nextPng, bool hasNext,
        float fontSize, bool autoFit, float minFontSize, float maxFontSize, float scroll, int width, int height)
    {
        var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Black);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var u = width / 1920f; // layout unit, so other resolutions scale proportionally
        var margin = 40 * u;
        var leftW = width * 0.44f;
        using var labelFont = new Font("Segoe UI", 28 * u, FontStyle.Bold, GraphicsUnit.Pixel);
        using var mutedBrush = new SolidBrush(Muted);
        var labelH = labelFont.GetHeight(g);

        // Current slide
        var currentLabel = count > 0 ? $"CURRENT  ·  SLIDE {index + 1} / {count}" : "NO PRESENTATION LOADED";
        g.DrawString(currentLabel, labelFont, mutedBrush, margin, margin);
        var currentRect = new RectangleF(margin, margin + labelH + 8 * u, leftW, leftW * 9 / 16);

        // Next slide
        var nextLabelY = currentRect.Bottom + 28 * u;
        g.DrawString("NEXT", labelFont, mutedBrush, margin, nextLabelY);
        var nextW = Math.Min(leftW * 0.8f, (height - margin - (nextLabelY + labelH + 8 * u)) * 16 / 9);
        var nextRect = new RectangleF(margin, nextLabelY + labelH + 8 * u, nextW, nextW * 9 / 16);
        if (nextPng != null)
        {
            using var ms = new MemoryStream(nextPng);
            using var img = Image.FromStream(ms);
            DrawFit(g, img, nextRect, InterpolationMode.HighQualityBicubic);
        }
        else if (count > 0)
        {
            using var endFont = new Font("Segoe UI", 30 * u, FontStyle.Italic, GraphicsUnit.Pixel);
            using var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(hasNext ? "" : "End of presentation", endFont, mutedBrush, nextRect, center);
        }
        using (var pen = new Pen(Frame, 2 * u))
        {
            g.DrawRectangle(pen, currentRect.X - u, currentRect.Y - u, currentRect.Width + 2 * u, currentRect.Height + 2 * u);
            g.DrawRectangle(pen, nextRect.X - u, nextRect.Y - u, nextRect.Width + 2 * u, nextRect.Height + 2 * u);
        }

        // Notes column
        var notesX = margin * 2 + leftW;
        var notesW = width - notesX - margin;
        g.DrawString("NOTES", labelFont, mutedBrush, notesX, margin);
        var view = new RectangleF(notesX, margin + labelH + 8 * u, notesW, height - (margin + labelH + 8 * u) - margin);

        float maxScroll = 0;
        if (string.IsNullOrWhiteSpace(notes))
        {
            if (count > 0)
            {
                using var italic = new Font("Segoe UI", fontSize, FontStyle.Italic, GraphicsUnit.Pixel);
                g.DrawString("(No speaker notes)", italic, mutedBrush, view);
            }
            scroll = 0;
        }
        else
        {
            using var format = new StringFormat();
            if (autoFit)
                fontSize = LargestFittingFontSize(g, notes, format, view.Size, minFontSize, maxFontSize);
            using var font = new Font("Segoe UI", fontSize, FontStyle.Regular, GraphicsUnit.Pixel);
            var textH = g.MeasureString(notes, font, new SizeF(view.Width, 100_000), format).Height;
            maxScroll = Math.Max(0, textH - view.Height);
            scroll = Math.Clamp(scroll, 0, maxScroll);

            var state = g.Save();
            g.SetClip(view);
            g.DrawString(notes, font, Brushes.White, new RectangleF(view.X, view.Y - scroll, view.Width, textH + fontSize), format);
            g.Restore(state);

            // Scroll indicators
            using var arrowFont = new Font("Segoe UI", 26 * u, FontStyle.Bold, GraphicsUnit.Pixel);
            using var accent = new SolidBrush(Color.FromArgb(255, 200, 60));
            using var right = new StringFormat { Alignment = StringAlignment.Far };
            if (scroll > 0.5f)
                g.DrawString("▲ more", arrowFont, accent, new RectangleF(notesX, margin, notesW, labelH), right);
            if (scroll < maxScroll - 0.5f)
                g.DrawString("▼ more", arrowFont, accent, new RectangleF(notesX, height - margin + 4 * u, notesW, margin), right);
        }

        return new PresenterBackground
        {
            Bitmap = bmp, CurrentRect = currentRect, Scroll = scroll, MaxScroll = maxScroll, FontSize = fontSize,
        };
    }

    /// <summary>Binary-searches the largest font size (whole pixels) at which the notes fit the view without scrolling.</summary>
    private static float LargestFittingFontSize(Graphics g, string text, StringFormat format, SizeF view, float min, float max)
    {
        bool Fits(float size)
        {
            using var font = new Font("Segoe UI", size, FontStyle.Regular, GraphicsUnit.Pixel);
            var measured = g.MeasureString(text, font, new SizeF(view.Width, 100_000), format);
            return measured.Height <= view.Height && measured.Width <= view.Width + 0.5f;
        }

        int lo = (int)Math.Ceiling(min), hi = (int)Math.Floor(max);
        if (!Fits(lo)) return lo; // too long even at the minimum size: fall back to scrolling
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (Fits(mid)) lo = mid;
            else hi = mid - 1;
        }
        return lo;
    }

    /// <summary>Draws the current slide (live frame) onto a copy of the presenter background.</summary>
    public static Bitmap ComposePresenter(PresenterBackground bg, Bitmap current)
    {
        var bmp = new Bitmap(bg.Bitmap);
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = InterpolationMode.Bilinear;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        DrawFit(g, current, bg.CurrentRect, InterpolationMode.Bilinear);
        return bmp;
    }

    private static readonly ImageCodecInfo JpegCodec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    public static byte[] EncodeJpeg(Bitmap bmp, long quality)
    {
        using var ms = new MemoryStream();
        using var ps = new EncoderParameters(1);
        ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
        bmp.Save(ms, JpegCodec, ps);
        return ms.ToArray();
    }

    /// <summary>Returns a downscaled copy for UI previews.</summary>
    public static Bitmap Thumbnail(Bitmap src, int maxWidth)
    {
        var w = Math.Min(maxWidth, src.Width);
        var h = Math.Max(1, src.Height * w / src.Width);
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        g.DrawImage(src, 0, 0, w, h);
        return bmp;
    }

    private static void DrawFit(Graphics g, Image img, RectangleF area, InterpolationMode mode)
    {
        g.InterpolationMode = mode;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(img, FitRect(img.Size, area));
    }

    private static RectangleF FitRect(SizeF src, RectangleF area)
    {
        var scale = Math.Min(area.Width / src.Width, area.Height / src.Height);
        var w = src.Width * scale;
        var h = src.Height * scale;
        return new RectangleF(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);
    }

    /// <summary>Copies a 32bpp ARGB bitmap into a tightly packed BGRA byte array (GDI+ stores ARGB as BGRA in memory).</summary>
    public static byte[] ToBgra(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var rowBytes = bmp.Width * 4;
            var result = new byte[rowBytes * bmp.Height];
            for (var y = 0; y < bmp.Height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, result, y * rowBytes, rowBytes);
            return result;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }
}
