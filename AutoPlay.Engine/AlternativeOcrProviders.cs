using RapidOCRLib;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Tesseract;

namespace AutoPlay.Engine;

public sealed class RapidOcrProvider : IOcrProvider
{
    private readonly string detPath;
    private readonly string clsPath;
    private readonly string recPath;
    private readonly string dictionaryPath;
    private readonly int cpuThreads;
    private readonly SemaphoreSlim gate = new(1, 1);
    private OcrLite? engine;

    public RapidOcrProvider(string detPath, string clsPath, string recPath, string dictionaryPath, int cpuThreads = 1)
    {
        this.detPath = Path.GetFullPath(detPath);
        this.clsPath = Path.GetFullPath(clsPath);
        this.recPath = Path.GetFullPath(recPath);
        this.dictionaryPath = Path.GetFullPath(dictionaryPath);
        if (cpuThreads < 1) throw new ArgumentOutOfRangeException(nameof(cpuThreads));
        this.cpuThreads = cpuThreads;
    }

    public async Task<IReadOnlyList<OcrTextLine>> RecognizeAsync(Bitmap image, string? languageTag = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        await gate.WaitAsync(cancellationToken);
        try
        {
            engine ??= await CreateEngineAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var result = await engine.DetectAsync(image, padding: 0);
            cancellationToken.ThrowIfCancellationRequested();
            if (result?.TextBlocks is null) throw new InvalidDataException("RapidOCR returned no text-block collection.");
            return result.TextBlocks.Select(block =>
            {
                if (block.BoxPoints is not { Count: > 0 }) return null;
                int left = block.BoxPoints.Min(point => point.X);
                int top = block.BoxPoints.Min(point => point.Y);
                int right = block.BoxPoints.Max(point => point.X);
                int bottom = block.BoxPoints.Max(point => point.Y);
                return right <= left || bottom <= top || string.IsNullOrWhiteSpace(block.Text)
                    ? null
                    : new OcrTextLine(Rectangle.FromLTRB(left, top, right, bottom), block.Text, block.BoxScore);
            }).Where(line => line is not null).Cast<OcrTextLine>().ToArray();
        }
        finally { gate.Release(); }
    }

    private async Task<OcrLite> CreateEngineAsync(CancellationToken cancellationToken)
    {
        foreach (var path in new[] { detPath, clsPath, recPath, dictionaryPath })
            if (!File.Exists(path)) throw new FileNotFoundException("RapidOCR model or dictionary file not found.", path);
        var instance = new OcrLite { ThreadNum = cpuThreads };
        await instance.InitModels(detPath, clsPath, recPath, dictionaryPath, cpuThreads);
        cancellationToken.ThrowIfCancellationRequested();
        return instance;
    }
}

public sealed class TesseractOcrProvider : IOcrProvider, IDisposable
{
    private readonly string dataPath;
    private readonly string defaultLanguage;
    private readonly ConcurrentDictionary<string, TesseractEngine> engines = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool disposed;

    public TesseractOcrProvider(string dataPath, string defaultLanguage = "eng")
    {
        this.dataPath = Path.GetFullPath(dataPath);
        if (!Directory.Exists(this.dataPath)) throw new DirectoryNotFoundException($"Tesseract tessdata directory does not exist: {this.dataPath}");
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultLanguage);
        this.defaultLanguage = defaultLanguage;
    }

    public async Task<IReadOnlyList<OcrTextLine>> RecognizeAsync(Bitmap image, string? languageTag = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        await gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            string language = MapLanguage(languageTag ?? defaultLanguage);
            var engine = engines.GetOrAdd(language, value => new TesseractEngine(dataPath, value, EngineMode.Default));
            using var encoded = new MemoryStream();
            image.Save(encoded, System.Drawing.Imaging.ImageFormat.Png);
            using var pix = Pix.LoadFromMemory(encoded.ToArray());
            using var page = engine.Process(pix);
            using var iterator = page.GetIterator();
            var lines = new List<OcrTextLine>();
            iterator.Begin();
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? text = iterator.GetText(PageIteratorLevel.TextLine)?.Trim();
                if (string.IsNullOrEmpty(text) || !iterator.TryGetBoundingBox(PageIteratorLevel.TextLine, out var bounds)) continue;
                lines.Add(new OcrTextLine(
                    Rectangle.FromLTRB(bounds.X1, bounds.Y1, bounds.X2, bounds.Y2),
                    text,
                    iterator.GetConfidence(PageIteratorLevel.TextLine) / 100.0));
            }
            while (iterator.Next(PageIteratorLevel.TextLine));
            return lines;
        }
        finally { gate.Release(); }
    }

    private static string MapLanguage(string language) => language.ToLowerInvariant() switch
    {
        "en" or "en-us" or "en-gb" => "eng",
        "zh" or "zh-cn" => "chi_sim",
        "zh-tw" => "chi_tra",
        "ja" or "ja-jp" => "jpn",
        "ko" or "ko-kr" => "kor",
        _ => language
    };

    public void Dispose()
    {
        if (disposed) return;
        gate.Wait();
        try
        {
            if (disposed) return;
            disposed = true;
            foreach (var engine in engines.Values) engine.Dispose();
            engines.Clear();
        }
        finally { gate.Release(); }
    }
}

public sealed class PaddleOcrProvider : IOcrProvider, IDisposable
{
    private readonly string pythonExecutable;
    private readonly string detectionModelDirectory;
    private readonly string recognitionModelDirectory;
    private readonly string defaultLanguage;
    private readonly SemaphoreSlim gate = new(1, 1);
    private Process? worker;
    private Task<string>? stderrTask;
    private bool disposed;

    public PaddleOcrProvider(
        string detectionModelDirectory,
        string recognitionModelDirectory,
        string pythonExecutable = "python",
        string defaultLanguage = "ch")
    {
        this.detectionModelDirectory = Path.GetFullPath(detectionModelDirectory);
        this.recognitionModelDirectory = Path.GetFullPath(recognitionModelDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(pythonExecutable);
        this.pythonExecutable = pythonExecutable;
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultLanguage);
        this.defaultLanguage = defaultLanguage;
    }

    public async Task<IReadOnlyList<OcrTextLine>> RecognizeAsync(Bitmap image, string? languageTag = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        await gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            await EnsureWorkerAsync(cancellationToken);
            using var encoded = new MemoryStream();
            image.Save(encoded, System.Drawing.Imaging.ImageFormat.Png);
            var request = JsonSerializer.Serialize(new
            {
                image = Convert.ToBase64String(encoded.GetBuffer(), 0, checked((int)encoded.Length)),
                language = MapLanguage(languageTag ?? defaultLanguage),
            });
            try
            {
                await worker!.StandardInput.WriteLineAsync(request.AsMemory(), cancellationToken);
                await worker.StandardInput.FlushAsync(cancellationToken);
                string? response = await worker.StandardOutput.ReadLineAsync(cancellationToken);
                if (response is null) throw new EndOfStreamException($"PaddleOCR worker exited unexpectedly. {await GetWorkerErrorAsync()}");
                using var result = JsonDocument.Parse(response);
                var root = result.RootElement;
                if (!root.GetProperty("ok").GetBoolean())
                    throw new InvalidOperationException($"PaddleOCR failed: {root.GetProperty("error").GetString()}");
                return root.GetProperty("lines").EnumerateArray().Select(line =>
                    new OcrTextLine(
                        new Rectangle(line.GetProperty("x").GetInt32(), line.GetProperty("y").GetInt32(),
                            line.GetProperty("width").GetInt32(), line.GetProperty("height").GetInt32()),
                        line.GetProperty("text").GetString() ?? string.Empty,
                        line.GetProperty("confidence").GetDouble())).ToArray();
            }
            catch
            {
                StopWorker();
                throw;
            }
        }
        finally { gate.Release(); }
    }

    private async Task EnsureWorkerAsync(CancellationToken cancellationToken)
    {
        if (worker is { HasExited: false }) return;
        if (!Directory.Exists(detectionModelDirectory))
            throw new DirectoryNotFoundException($"PaddleOCR detection model directory does not exist: {detectionModelDirectory}");
        if (!Directory.Exists(recognitionModelDirectory))
            throw new DirectoryNotFoundException($"PaddleOCR recognition model directory does not exist: {recognitionModelDirectory}");
        string workerPath = Path.Combine(AppContext.BaseDirectory, "paddle_ocr_worker.py");
        if (!File.Exists(workerPath)) throw new FileNotFoundException("PaddleOCR worker script was not deployed with the application.", workerPath);

        var startInfo = new ProcessStartInfo(pythonExecutable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add(workerPath);
        startInfo.ArgumentList.Add(JsonSerializer.Serialize(new
        {
            det_model_dir = detectionModelDirectory,
            rec_model_dir = recognitionModelDirectory,
            default_language = MapLanguage(defaultLanguage),
        }));
        worker = Process.Start(startInfo) ?? throw new InvalidOperationException($"Unable to start PaddleOCR worker: {pythonExecutable}");
        stderrTask = worker.StandardError.ReadToEndAsync();
        string? ready;
        try { ready = await worker.StandardOutput.ReadLineAsync(cancellationToken); }
        catch { StopWorker(); throw; }
        if (ready != "READY")
        {
            string error = await GetWorkerErrorAsync();
            StopWorker();
            throw new InvalidOperationException($"PaddleOCR worker failed to initialize. {error}");
        }
    }

    private static string MapLanguage(string language)
    {
        if (language.Equals("zh-CN", StringComparison.OrdinalIgnoreCase) || language.Equals("zh", StringComparison.OrdinalIgnoreCase)) return "ch";
        if (language.Equals("zh-TW", StringComparison.OrdinalIgnoreCase)) return "chinese_cht";
        if (language.Equals("en-US", StringComparison.OrdinalIgnoreCase) || language.Equals("en-GB", StringComparison.OrdinalIgnoreCase)) return "en";
        if (language.Equals("ja-JP", StringComparison.OrdinalIgnoreCase)) return "japan";
        return language;
    }

    private async Task<string> GetWorkerErrorAsync()
        => stderrTask is null ? string.Empty : await stderrTask;

    private void StopWorker()
    {
        var old = worker;
        worker = null;
        stderrTask = null;
        if (old is null) return;
        try
        {
            if (!old.HasExited) old.Kill(entireProcessTree: true);
            old.Dispose();
        }
        catch (InvalidOperationException) { old.Dispose(); }
    }

    public void Dispose()
    {
        if (disposed) return;
        gate.Wait();
        try
        {
            if (disposed) return;
            disposed = true;
            StopWorker();
        }
        finally { gate.Release(); }
    }
}
