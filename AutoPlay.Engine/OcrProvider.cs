using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace AutoPlay.Engine;

public sealed record OcrTextLine(Rectangle Bounds, string Text, double? Confidence = null);

public interface IOcrProvider
{
    Task<IReadOnlyList<OcrTextLine>> RecognizeAsync(
        Bitmap image,
        string? languageTag = null,
        CancellationToken cancellationToken = default);
}

public sealed class WindowsOcrProvider : IOcrProvider
{
    private readonly ConcurrentDictionary<string, OcrEngine> engines = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<OcrTextLine>> RecognizeAsync(
        Bitmap image,
        string? languageTag = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        cancellationToken.ThrowIfCancellationRequested();
        if (image.Width > OcrEngine.MaxImageDimension || image.Height > OcrEngine.MaxImageDimension)
            throw new ArgumentException("OCR image exceeds the Windows OCR maximum image dimension.", nameof(image));

        string key = languageTag ?? string.Empty;
        var engine = engines.GetOrAdd(key, static tag =>
        {
            var instance = tag.Length == 0
                ? OcrEngine.TryCreateFromUserProfileLanguages()
                : OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language(tag));
            return instance ?? throw new InvalidOperationException(
                $"Windows OCR language is unavailable: {(tag.Length == 0 ? "user profile" : tag)}. Install a Windows OCR language pack.");
        });

        using var encoded = new MemoryStream();
        image.Save(encoded, ImageFormat.Png);
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(encoded.ToArray());
            await writer.StoreAsync().AsTask(cancellationToken);
            await writer.FlushAsync().AsTask(cancellationToken);
        }
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);
        using var softwareBitmap = await decoder.GetSoftwareBitmapAsync().AsTask(cancellationToken);
        var result = await engine.RecognizeAsync(softwareBitmap).AsTask(cancellationToken);

        return result.Lines
            .Where(line => line.Words.Count > 0)
            .Select(line =>
            {
                double left = line.Words.Min(word => word.BoundingRect.Left);
                double top = line.Words.Min(word => word.BoundingRect.Top);
                double right = line.Words.Max(word => word.BoundingRect.Right);
                double bottom = line.Words.Max(word => word.BoundingRect.Bottom);
                return new OcrTextLine(
                    Rectangle.FromLTRB((int)Math.Floor(left), (int)Math.Floor(top),
                        (int)Math.Ceiling(right), (int)Math.Ceiling(bottom)),
                    line.Text);
            })
            .ToArray();
    }
}
