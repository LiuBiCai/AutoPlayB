using AutoPlay.Engine;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using System.Drawing;
using System.Diagnostics;
using System.Windows.Forms;
using Windows.Globalization;
using Windows.Media.Ocr;
using DrawingPoint = System.Drawing.Point;
using DrawingRect = System.Drawing.Rectangle;

Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

using (var bitmap = new Bitmap(40, 30, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
{
    using (var g = Graphics.FromImage(bitmap)) g.Clear(Color.White);
    bitmap.SetPixel(12, 8, Color.FromArgb(200, 30, 40));
    bitmap.SetPixel(13, 8, Color.FromArgb(40, 80, 120));
    bitmap.SetPixel(12, 9, Color.FromArgb(20, 200, 90));
    bitmap.SetPixel(13, 9, Color.FromArgb(80, 20, 180));
    using var templateBitmap = bitmap.Clone(new DrawingRect(12, 8, 2, 2), bitmap.PixelFormat);
    using var source = BitmapConverter.ToMat(templateBitmap);
    using var template = new Mat();
    Cv2.CvtColor(source, template, ColorConversionCodes.BGRA2BGR);
    using var frame = new RecognitionFrame((Bitmap)bitmap.Clone(), _ => template, new WindowsOcrProvider(), null);
    var match = frame.FindImage("template", 0.99, new DrawingRect(5, 5, 20, 20));
    Check(match?.Bounds == new DrawingRect(12, 8, 2, 2), "Image match must return client-relative coordinates.");
    Check(frame.FindImage("template", 0.99, new DrawingRect(20, 10, 10, 10)) is null, "Image below threshold must not match.");
    Check(frame.FindColor(Color.FromArgb(201, 31, 39), 1)?.Center == new DrawingPoint(12, 8), "Color tolerance must include the matching pixel.");
    Check(frame.FindColor(Color.FromArgb(201, 31, 39), 0) is null, "Color outside tolerance must not match.");
    try { frame.FindColor(Color.Red, 0, new DrawingRect(-1, 0, 5, 5)); throw new Exception("Invalid region accepted."); }
    catch (ArgumentOutOfRangeException) { }
    string output = Path.Combine(Path.GetTempPath(), $"autoplay-test-{Guid.NewGuid():N}.png");
    try
    {
        frame.SaveAnnotated(output);
        using var saved = new Bitmap(output);
        Check(saved.Size == bitmap.Size, "Annotated screenshot dimensions changed.");
        Check(saved.GetPixel(5, 5).ToArgb() != bitmap.GetPixel(5, 5).ToArgb(), "Search region was not drawn.");
    }
    finally { if (File.Exists(output)) File.Delete(output); }
}

var english = new Language("en-US");
if (OcrEngine.IsLanguageSupported(english))
{
    using var image = new Bitmap(400, 100, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
    using (var graphics = Graphics.FromImage(image))
    {
        graphics.Clear(Color.White);
        using var font = new Font("Arial", 36, FontStyle.Bold);
        graphics.DrawString("HELLO", font, Brushes.Black, 5, 5);
    }
    using var frame = new RecognitionFrame((Bitmap)image.Clone(), _ => throw new InvalidOperationException(), new WindowsOcrProvider(), null);
    var text = await frame.FindTextAsync("HELLO", "en-US");
    Check(text is not null && text.Bounds.Width > 0, "Windows OCR failed to find rendered text.");
    Console.WriteLine("Windows OCR check passed.");
}
else
{
    using var image = new Bitmap(20, 20, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
    using var frame = new RecognitionFrame((Bitmap)image.Clone(), _ => throw new InvalidOperationException(), new WindowsOcrProvider(), null);
    try { await frame.FindTextAsync("HELLO", "en-US"); throw new Exception("Unavailable OCR language was accepted."); }
    catch (InvalidOperationException ex) when (ex.Message.Contains("language is unavailable")) { }
    Console.WriteLine("Windows en-US OCR not installed; verified explicit failure.");
}

var ready = new TaskCompletionSource<Form>(TaskCreationOptions.RunContinuationsAsynchronously);
var clicked = new TaskCompletionSource<DrawingPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
var key = new TaskCompletionSource<Keys>(TaskCreationOptions.RunContinuationsAsynchronously);
var painted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var ui = new Thread(() =>
{
    var form = new Form { Text = $"AutoPlay test {Guid.NewGuid():N}", ClientSize = new System.Drawing.Size(160, 100), BackColor = Color.White };
    form.Paint += (_, e) =>
    {
        e.Graphics.FillRectangle(Brushes.Red, 20, 20, 30, 30);
        painted.TrySetResult();
    };
    form.MouseUp += (_, e) => clicked.TrySetResult(e.Location);
    form.KeyUp += (_, e) => key.TrySetResult(e.KeyCode);
    form.Shown += (_, _) => ready.TrySetResult(form);
    Application.Run(form);
}) { IsBackground = true };
ui.SetApartmentState(ApartmentState.STA);
ui.Start();
var testForm = await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
try
{
    await painted.Task.WaitAsync(TimeSpan.FromSeconds(10));
    var window = new GameWindow(testForm.Handle);
    Check(GameWindow.List().Any(w => w.Handle == testForm.Handle), "Window listing omitted the test window.");
    using var context = new ScriptContext(window, testMode: true);
    using var frame = context.Capture();
    Check(frame.FindColor(Color.Red, region: new DrawingRect(20, 20, 30, 30)) is not null, "PrintWindow did not capture the test window.");
    window.Click(new DrawingPoint(25, 25));
    Check(await clicked.Task.WaitAsync(TimeSpan.FromSeconds(5)) == new DrawingPoint(25, 25), "Background click coordinates are wrong.");
    window.KeyPress((int)Keys.A);
    Check(await key.Task.WaitAsync(TimeSpan.FromSeconds(5)) == Keys.A, "Background key message is wrong.");

    string host = Path.GetFullPath(@"AutoPlay.Host\bin\Debug\net9.0-windows10.0.17763.0\AutoPlay.Host.dll");
    using var process = Process.Start(new ProcessStartInfo("dotnet")
    {
        ArgumentList = { host, "run", $"0x{testForm.Handle.ToInt64():X}", typeof(IntegrationScript).Assembly.Location, "--test" },
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    }) ?? throw new Exception("Unable to start host.");
    var stdoutTask = process.StandardOutput.ReadToEndAsync();
    var stderrTask = process.StandardError.ReadToEndAsync();
    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
    catch (TimeoutException) { process.Kill(entireProcessTree: true); throw; }
    string stdout = await stdoutTask;
    string stderr = await stderrTask;
    Check(process.ExitCode == 0 && stdout.Contains("SCRIPT_OK"), $"Host script execution failed: {stdout} {stderr}");
}
finally
{
    testForm.BeginInvoke(testForm.Close);
    ui.Join();
}
Console.WriteLine("All recognition and window integration checks passed.");
await PipeTests.RunAsync();
Console.WriteLine("LunarNX pipe protocol checks passed.");

public sealed class IntegrationScript : IGameScript
{
    public async Task RunAsync(ScriptContext context, CancellationToken cancellationToken)
    {
        using var frame = await context.CaptureAsync(cancellationToken);
        if (!context.TestMode || frame.FindColor(Color.Red, region: new DrawingRect(20, 20, 30, 30)) is null)
            throw new InvalidOperationException("Integration script failed.");
        Console.WriteLine("SCRIPT_OK");
    }
}
