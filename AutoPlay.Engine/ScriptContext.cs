using OpenCvSharp;
using System.Collections.Concurrent;

namespace AutoPlay.Engine;

public interface IGameScript
{
    Task RunAsync(ScriptContext context, CancellationToken cancellationToken);
}

public sealed class ScriptContext : IDisposable
{
    private readonly ConcurrentDictionary<string, Mat> templates = new(StringComparer.OrdinalIgnoreCase);
    private readonly DiagnosticOverlay? overlay;
    private readonly IFrameSource source;
    public IOcrProvider Ocr { get; }
    public GameWindow Window => source as GameWindow ?? throw new InvalidOperationException("The selected frame source is not a Win32 window.");
    public IGamepadOutput Gamepad { get; }
    public IWindowInput Input { get; }
    public bool TestMode { get; }

    public ScriptContext(GameWindow window, bool testMode = false) : this(window, testMode, testMode ? window : null)
    {
    }

    public ScriptContext(IFrameSource source, bool testMode = false, GameWindow? overlayWindow = null, IGamepadOutput? gamepad = null, IWindowInput? input = null, IOcrProvider? ocrProvider = null)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        Ocr = ocrProvider ?? new WindowsOcrProvider();
        Gamepad = gamepad ?? source as IGamepadOutput ?? new UnsupportedGamepad();
        Input = input ?? source as IWindowInput ?? new UnsupportedInput();
        TestMode = testMode;
        if (overlayWindow is not null && !testMode) throw new ArgumentException("Overlay requires test mode.", nameof(overlayWindow));
        if (overlayWindow is not null) overlay = new DiagnosticOverlay(overlayWindow);
    }

    private sealed class UnsupportedGamepad : IGamepadOutput
    {
        public Task SetStateAsync(GamepadState state, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("No gamepad output is configured.");
        public Task ReleaseAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException("No gamepad output is configured.");
    }

    private sealed class UnsupportedInput : IWindowInput
    {
        public void Click(System.Drawing.Point point) => throw new NotSupportedException("No window input is configured.");
        public void KeyPress(int virtualKey) => throw new NotSupportedException("No window input is configured.");
    }

    public RecognitionFrame Capture() => new(Window.Capture(), LoadTemplate, Ocr, overlay is null ? null : overlay.Publish);

    public async ValueTask<RecognitionFrame> CaptureAsync(CancellationToken cancellationToken = default)
        => new(await source.CaptureAsync(cancellationToken), LoadTemplate, Ocr, overlay is null ? null : overlay.Publish);

    private Mat LoadTemplate(string path)
    {
        string fullPath = Path.GetFullPath(path);
        return templates.GetOrAdd(fullPath, static p =>
        {
            var image = Cv2.ImRead(p, ImreadModes.Color);
            if (image.Empty())
            {
                image.Dispose();
                throw new FileNotFoundException("Unable to load template image.", p);
            }
            return image;
        });
    }

    public void Dispose()
    {
        overlay?.Dispose();
        foreach (var template in templates.Values) template.Dispose();
        templates.Clear();
    }
}
