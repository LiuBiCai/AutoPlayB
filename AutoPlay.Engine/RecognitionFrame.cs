using System.Drawing;
using System.Drawing.Imaging;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using DrawingPoint = System.Drawing.Point;
using DrawingRect = System.Drawing.Rectangle;

namespace AutoPlay.Engine;

public sealed record RecognitionHit(DrawingRect Bounds, string Text, double? Score = null)
{
    public DrawingPoint Center => new(Bounds.Left + Bounds.Width / 2, Bounds.Top + Bounds.Height / 2);
}

internal sealed record Mark(DrawingRect Bounds, string Label, bool Hit);

public sealed class RecognitionFrame : IDisposable
{
    private readonly Bitmap bitmap;
    private readonly Func<string, Mat> loadTemplate;
    private readonly IOcrProvider ocrProvider;
    private readonly Action<IReadOnlyList<Mark>, System.Drawing.Size>? publish;
    private readonly List<Mark> marks = [];
    private Mat? pixels;

    internal RecognitionFrame(Bitmap bitmap, Func<string, Mat> loadTemplate, IOcrProvider ocrProvider, Action<IReadOnlyList<Mark>, System.Drawing.Size>? publish)
    {
        this.bitmap = bitmap;
        this.loadTemplate = loadTemplate;
        this.ocrProvider = ocrProvider;
        this.publish = publish;
    }

    private Mat Pixels
    {
        get
        {
            if (pixels is null)
            {
                using var source = BitmapConverter.ToMat(bitmap);
                pixels = new Mat();
                Cv2.CvtColor(source, pixels, ColorConversionCodes.BGRA2BGR);
            }
            return pixels;
        }
    }

    private DrawingRect Area(DrawingRect? region)
    {
        var full = new DrawingRect(0, 0, bitmap.Width, bitmap.Height);
        var area = region ?? full;
        if (area.Width <= 0 || area.Height <= 0 || !full.Contains(area))
            throw new ArgumentOutOfRangeException(nameof(region), "Region must be nonempty and inside the captured client area.");
        return area;
    }

    public RecognitionHit? FindImage(string templatePath, double threshold = 0.9, DrawingRect? region = null)
    {
        if (threshold is < 0 or > 1 || double.IsNaN(threshold)) throw new ArgumentOutOfRangeException(nameof(threshold));
        var area = Area(region);
        var template = loadTemplate(templatePath);
        if (template.Width > area.Width || template.Height > area.Height)
            throw new ArgumentException("Template is larger than the search region.", nameof(templatePath));
        using var roi = new Mat(Pixels, new OpenCvSharp.Rect(area.X, area.Y, area.Width, area.Height));
        using var result = new Mat();
        Cv2.MatchTemplate(roi, template, result, TemplateMatchModes.SqDiff);
        Cv2.MinMaxLoc(result, out double minimum, out _, out var location, out _);
        double score = 1 - Math.Sqrt(Math.Max(0, minimum) / (template.Width * (double)template.Height * 3)) / 255;
        var hit = score >= threshold
            ? new RecognitionHit(new DrawingRect(area.X + location.X, area.Y + location.Y, template.Width, template.Height), Path.GetFileName(templatePath), score)
            : null;
        Record(area, $"image >= {threshold:F2}", hit);
        return hit;
    }

    public RecognitionHit? FindColor(Color color, int tolerance = 0, DrawingRect? region = null)
    {
        if (tolerance is < 0 or > 255) throw new ArgumentOutOfRangeException(nameof(tolerance));
        var area = Area(region);
        using var roi = new Mat(Pixels, new OpenCvSharp.Rect(area.X, area.Y, area.Width, area.Height));
        using var mask = new Mat();
        Cv2.InRange(roi,
            new Scalar(Math.Max(0, color.B - tolerance), Math.Max(0, color.G - tolerance), Math.Max(0, color.R - tolerance)),
            new Scalar(Math.Min(255, color.B + tolerance), Math.Min(255, color.G + tolerance), Math.Min(255, color.R + tolerance)), mask);
        Cv2.MinMaxLoc(mask, out _, out double maximum, out _, out var location);
        var hit = maximum > 0
            ? new RecognitionHit(new DrawingRect(area.X + location.X, area.Y + location.Y, 1, 1), $"#{color.R:X2}{color.G:X2}{color.B:X2}")
            : null;
        Record(area, $"color #{color.R:X2}{color.G:X2}{color.B:X2}", hit);
        return hit;
    }

    public async Task<RecognitionHit?> FindTextAsync(string text, string? languageTag = null, DrawingRect? region = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Text is required.", nameof(text));
        var area = Area(region);
        using var crop = bitmap.Clone(area, PixelFormat.Format32bppArgb);
        var lines = await ocrProvider.RecognizeAsync(crop, languageTag, cancellationToken);
        RecognitionHit? hit = null;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line.Text) || !line.Text.Contains(text, StringComparison.OrdinalIgnoreCase)) continue;
            var bounds = line.Bounds;
            if (bounds.Width <= 0 || bounds.Height <= 0 || !new DrawingRect(0, 0, area.Width, area.Height).Contains(bounds))
                throw new InvalidDataException("OCR provider returned a text box outside the supplied image.");
            hit = new RecognitionHit(new DrawingRect(area.X + bounds.X, area.Y + bounds.Y, bounds.Width, bounds.Height), line.Text, line.Confidence);
            break;
        }
        Record(area, $"text: {text}", hit);
        return hit;
    }

    private void Record(DrawingRect area, string label, RecognitionHit? hit)
    {
        marks.Add(new Mark(area, label, false));
        if (hit is not null) marks.Add(new Mark(hit.Bounds, hit.Text, true));
        publish?.Invoke(marks.ToArray(), bitmap.Size);
    }

    public void SaveAnnotated(string path)
    {
        using var output = (Bitmap)bitmap.Clone();
        using var graphics = Graphics.FromImage(output);
        DrawMarks(graphics, marks);
        output.Save(path, ImageFormat.Png);
    }

    internal static void DrawMarks(Graphics graphics, IEnumerable<Mark> marks)
    {
        foreach (var mark in marks)
        {
            using var pen = new Pen(mark.Hit ? Color.Lime : Color.Orange, 2);
            var rect = mark.Bounds;
            if (rect.Width < 4 || rect.Height < 4) rect = new DrawingRect(rect.X - 3, rect.Y - 3, 7, 7);
            graphics.DrawRectangle(pen, rect);
            var labelPoint = new PointF(Math.Max(0, rect.X), Math.Max(0, rect.Y - 16));
            var size = graphics.MeasureString(mark.Label, SystemFonts.DefaultFont);
            graphics.FillRectangle(Brushes.Black, labelPoint.X, labelPoint.Y, size.Width, size.Height);
            graphics.DrawString(mark.Label, SystemFonts.DefaultFont, mark.Hit ? Brushes.Lime : Brushes.Orange, labelPoint);
        }
    }

    public void Dispose()
    {
        pixels?.Dispose();
        bitmap.Dispose();
    }
}
