using System.Reflection;
using System.Runtime.Loader;
using AutoPlay.Engine;
using System.Windows.Forms;

Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

try
{
    if (args.Length == 1 && args[0] == "list")
    {
        foreach (var item in GameWindow.List())
            Console.WriteLine($"0x{item.Handle.ToInt64():X}\t{item.Title}");
        return 0;
    }

    if (args.Length < 3 || (args[0] != "run" && args[0] != "run-pipe"))
    {
        Console.Error.WriteLine("Usage: AutoPlay.Host list | run <window-handle> <script.dll> [options] | run-pipe <pipe-name> <script.dll> [options]");
        return 2;
    }

    bool pipeMode = args[0] == "run-pipe";
    static nint ParseHandle(string text) => (nint)(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? Convert.ToInt64(text[2..], 16) : long.Parse(text));
    var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    for (int i = 3; i < args.Length; i++)
    {
        string option = args[i];
        if (option == "--test") { options.Add(option, null); continue; }
        if (option is not ("--overlay-window" or "--ocr" or "--ocr-models" or "--ocr-det" or "--ocr-rec" or "--ocr-language" or "--ocr-python" or "--ocr-threads"))
            throw new ArgumentException($"Unknown option: {option}");
        if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(args[i]))
            throw new ArgumentException($"Missing value for {option}.");
        options.Add(option, args[i]);
    }
    bool testMode = options.ContainsKey("--test");
    if (options.ContainsKey("--overlay-window") && !pipeMode)
        throw new ArgumentException("--overlay-window is only used with run-pipe.");
    if (options.ContainsKey("--overlay-window") && !testMode)
        throw new ArgumentException("--overlay-window requires --test.");
    int threads = options.TryGetValue("--ocr-threads", out var threadText)
        ? int.Parse(threadText!) : 1;
    if (threads < 1) throw new ArgumentOutOfRangeException("--ocr-threads");
    string ocrName = (options.GetValueOrDefault("--ocr") ?? "windows").ToLowerInvariant();
    string? models = options.GetValueOrDefault("--ocr-models");
    string? det = options.GetValueOrDefault("--ocr-det");
    string? rec = options.GetValueOrDefault("--ocr-rec");
    string? language = options.GetValueOrDefault("--ocr-language");
    string python = options.GetValueOrDefault("--ocr-python") ?? "python";
    if (ocrName is not ("rapid" or "tesseract" or "paddle" or "windows"))
        throw new ArgumentException($"Unknown OCR provider: {ocrName}. Choose windows, rapid, paddle, or tesseract.");
    if (ocrName != "rapid" && options.ContainsKey("--ocr-threads"))
        throw new ArgumentException("--ocr-threads is only used by RapidOCR.");
    if (ocrName != "paddle" && (det is not null || rec is not null || options.ContainsKey("--ocr-python")))
        throw new ArgumentException("--ocr-det, --ocr-rec, and --ocr-python are only used by PaddleOCR.");
    if (ocrName is "rapid" or "tesseract" ? models is null : options.ContainsKey("--ocr-models"))
        throw new ArgumentException(ocrName is "rapid" or "tesseract"
            ? $"--ocr {ocrName} requires --ocr-models."
            : "--ocr-models is only used by RapidOCR and Tesseract.");
    if (ocrName == "paddle" && (det is null || rec is null))
        throw new ArgumentException("--ocr paddle requires --ocr-det and --ocr-rec.");
    if (ocrName != "paddle" && (det is not null || rec is not null))
        throw new ArgumentException("--ocr-det and --ocr-rec are only used by PaddleOCR.");
    if (ocrName == "rapid" && options.ContainsKey("--ocr-language"))
        throw new ArgumentException("RapidOCR language is determined by its model and dictionary; --ocr-language is not supported.");
    IOcrProvider ocrProvider = ocrName switch
    {
        "windows" => new WindowsOcrProvider(),
        "tesseract" => new TesseractOcrProvider(models!, language ?? "eng"),
        "rapid" => new RapidOcrProvider(
            Path.Combine(models!, "ch_PP-OCRv5_mobile_det.onnx"),
            Path.Combine(models!, "ch_ppocr_mobile_v2.0_cls_infer.onnx"),
            Path.Combine(models!, "ch_PP-OCRv5_rec_mobile_infer.onnx"),
            Path.Combine(models!, "ppocrv5_dict.txt"), threads),
        "paddle" => new PaddleOcrProvider(det!, rec!, python, language ?? "ch"),
        _ => throw new InvalidOperationException("Validated OCR provider was not handled.")
    };
    using var disposableOcrProvider = ocrProvider as IDisposable;
    string scriptPath = Path.GetFullPath(args[2]);
    var resolver = new AssemblyDependencyResolver(scriptPath);
    AssemblyLoadContext.Default.Resolving += (_, name) =>
    {
        var path = resolver.ResolveAssemblyToPath(name);
        return path is null ? null : AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
    };
    var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(scriptPath);
    var types = assembly.GetTypes().Where(t => !t.IsAbstract && typeof(IGameScript).IsAssignableFrom(t)).ToArray();
    if (types.Length != 1) throw new InvalidOperationException("The script assembly must contain exactly one concrete IGameScript implementation.");
    var script = (IGameScript)(Activator.CreateInstance(types[0]) ?? throw new InvalidOperationException("Unable to create script."));
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    using var pipe = pipeMode ? await LunarNxPipe.ConnectAsync(args[1], cancellation.Token) : null;
    var window = pipeMode ? null : new GameWindow(ParseHandle(args[1]));
    var overlayWindow = pipeMode && options.TryGetValue("--overlay-window", out var overlayHandle) ? new GameWindow(ParseHandle(overlayHandle!)) : window;
    IFrameSource source = pipe is not null ? pipe : window!;
    using var context = new ScriptContext(source, testMode, testMode ? overlayWindow : null, ocrProvider: ocrProvider);
    Console.WriteLine($"Running {types[0].FullName} on {args[1]}. Press Ctrl+C to stop.");
    try { await script.RunAsync(context, cancellation.Token); }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    return 1;
}
