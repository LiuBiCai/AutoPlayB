using AutoPlay.Engine;
using System.Drawing;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Diagnostics;

internal static class PipeTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    public static async Task RunAsync()
    {
        string name = "AutoPlay-test-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var serving = ServeAsync(server);
        using var client = await LunarNxPipe.ConnectAsync(@"\\.\pipe\" + name);
        using (var context = new ScriptContext(client, testMode: true))
        using (var frame = await context.CaptureAsync())
        {
            Check(context.TestMode, "Test mode must work without an overlay.");
            Check(frame.FindColor(Color.FromArgb(10, 20, 30))?.Bounds.Location == new Point(0, 0), "RGB24 conversion failed.");
            Check(frame.FindColor(Color.FromArgb(200, 150, 100))?.Bounds.Location == new Point(1, 0), "RGB24 row conversion failed.");
            await context.Gamepad.SetStateAsync(new GamepadState { A = true, LeftStickX = 1234, LeftTrigger = 54321 });
            await context.Gamepad.ReleaseAsync();
        }
        try { await client.CaptureAsync(); throw new Exception("Invalid screenshot accepted."); }
        catch (InvalidDataException) { }
        try { await client.ReleaseAsync(); throw new Exception("Broken connection was reused."); }
        catch (ObjectDisposedException) { }
        await serving.WaitAsync(TimeSpan.FromSeconds(5));

        var gamepad = new RecordingGamepad();
        await gamepad.HoldAsync(new GamepadState { A = true }, TimeSpan.FromMilliseconds(250));
        Check(gamepad.Inputs >= 2 && gamepad.Releases == 1, "Hold must refresh input and release.");
        using (var cancellation = new CancellationTokenSource(80))
        {
            try { await gamepad.HoldAsync(new GamepadState { A = true }, TimeSpan.FromSeconds(2), cancellation.Token); throw new Exception("Hold ignored cancellation."); }
            catch (OperationCanceledException) { }
        }
        Check(gamepad.Releases == 2, "Canceled hold must release.");

        var fakeOcr = new FakeOcrProvider();
        using (var context = new ScriptContext(new SolidFrameSource(), gamepad: gamepad, ocrProvider: fakeOcr))
        using (var frame = await context.CaptureAsync())
        {
            Check(frame.FindColor(Color.Blue) is not null, "Custom frame source not used.");
            var hit = await frame.FindTextAsync("START", "en-US", new Rectangle(4, 3, 10, 5));
            Check(hit?.Bounds == new Rectangle(6, 4, 5, 3) && hit.Score == 0.75, "Injected OCR result or region offset is wrong.");
            Check(fakeOcr.LastLanguage == "en-US" && fakeOcr.LastImageSize == new Size(10, 5), "OCR provider did not receive language and cropped image.");
            await context.Gamepad.ReleaseAsync();
            try { context.Input.Click(Point.Empty); throw new Exception("Unavailable output was accepted."); }
            catch (NotSupportedException) { }
        }
        await TestCanceledResponseAsync();
        await TestAlternativeProviderConfigurationAsync();
        await RunHostAsync();
        string? executable = Environment.GetEnvironmentVariable("LUNARNX_AUTOMATION_TEST");
        if (!string.IsNullOrEmpty(executable)) await RunLunarNxAsync(executable);
    }

    private static async Task ServeAsync(NamedPipeServerStream server)
    {
        await server.WaitForConnectionAsync();
        using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
        for (int i = 0; i < 4; i++)
        {
            string line = await reader.ReadLineAsync() ?? throw new EndOfStreamException();
            using var request = JsonDocument.Parse(line);
            var root = request.RootElement;
            string? type = root.GetProperty("type").GetString();
            if (i == 0)
            {
                Check(type == "screenshot", "Expected screenshot request.");
                await WriteAsync(server, "{\"ok\":true,\"format\":\"RGB24\",\"width\":2,\"height\":1,\"bytes\":6}\n");
                await server.WriteAsync(new byte[] { 10, 20, 30 });
                await server.WriteAsync(new byte[] { 200, 150, 100 });
            }
            else if (i == 1)
            {
                Check(type == "input", "Expected input request.");
                var state = root.GetProperty("state");
                Check(state.GetProperty("a").GetBoolean() && state.GetProperty("left_stick_x").GetInt32() == 1234 &&
                    state.GetProperty("left_trigger").GetInt32() == 54321 && !state.GetProperty("b").GetBoolean(), "Incorrect complete gamepad state.");
                await WriteAsync(server, "{\"ok\":true}\n");
            }
            else if (i == 2)
            {
                Check(type == "release", "Expected release request.");
                await WriteAsync(server, "{\"ok\":true}\n");
            }
            else
            {
                Check(type == "screenshot", "Expected second screenshot request.");
                await WriteAsync(server, "{\"ok\":true,\"format\":\"RGB24\",\"width\":2,\"height\":1,\"bytes\":5}\n");
            }
        }
    }

    private static Task WriteAsync(Stream stream, string text) => stream.WriteAsync(Encoding.UTF8.GetBytes(text)).AsTask();

    private static async Task TestCanceledResponseAsync()
    {
        string name = "AutoPlay-cancel-test-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var connection = server.WaitForConnectionAsync();
        using var client = await LunarNxPipe.ConnectAsync(name);
        await connection;
        using var timeout = new CancellationTokenSource(200);
        try { using var image = await client.CaptureAsync(timeout.Token); throw new Exception("Cancellation was ignored."); }
        catch (OperationCanceledException) { }
        try { await client.ReleaseAsync(); throw new Exception("Canceled pipe was reused."); }
        catch (ObjectDisposedException) { }
    }

    private static async Task TestAlternativeProviderConfigurationAsync()
    {
        using var bitmap = new Bitmap(4, 4, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var rapid = new RapidOcrProvider("missing-det.onnx", "missing-cls.onnx", "missing-rec.onnx", "missing-dict.txt");
        try { await rapid.RecognizeAsync(bitmap); throw new Exception("RapidOCR accepted missing models."); }
        catch (FileNotFoundException) { }

        try { _ = new TesseractOcrProvider("missing-tessdata"); throw new Exception("Tesseract accepted missing tessdata."); }
        catch (DirectoryNotFoundException) { }

        var paddle = new PaddleOcrProvider("missing-paddle-det", "missing-paddle-rec");
        try { await paddle.RecognizeAsync(bitmap); throw new Exception("PaddleOCR accepted missing model directories."); }
        catch (DirectoryNotFoundException) { }
        finally { paddle.Dispose(); }

        string worker = Path.Combine(AppContext.BaseDirectory, "paddle_ocr_worker.py");
        Check(File.Exists(worker), "PaddleOCR worker was not copied to the test output.");
    }

    private static async Task RunHostAsync()
    {
        string name = "AutoPlay-host-test-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        string host = Path.GetFullPath(@"AutoPlay.Host\bin\Debug\net9.0-windows10.0.17763.0\AutoPlay.Host.dll");
        using var process = Process.Start(new ProcessStartInfo("dotnet")
        {
            ArgumentList = { host, "run-pipe", name, typeof(IntegrationScript).Assembly.Location, "--test" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw new Exception("Unable to start pipe host.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await server.WaitForConnectionAsync(timeout.Token);
            using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
            using var request = JsonDocument.Parse(await reader.ReadLineAsync(timeout.Token) ?? throw new EndOfStreamException());
            Check(request.RootElement.GetProperty("type").GetString() == "screenshot", "Host did not request screenshot.");
            await WriteAsync(server, "{\"ok\":true,\"format\":\"RGB24\",\"width\":160,\"height\":100,\"bytes\":48000}\n");
            var pixels = new byte[48000];
            for (int y = 20; y < 50; y++)
                for (int x = 20; x < 50; x++)
                    pixels[(y * 160 + x) * 3] = 255;
            await server.WriteAsync(pixels, timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            Check(process.ExitCode == 0 && (await stdout).Contains("SCRIPT_OK"), $"Pipe host failed: {await stderr}");
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
    }

    private sealed class RecordingGamepad : IGamepadOutput
    {
        public int Inputs { get; private set; }
        public int Releases { get; private set; }
        public Task SetStateAsync(GamepadState state, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Inputs++;
            return Task.CompletedTask;
        }

        public Task ReleaseAsync(CancellationToken cancellationToken = default)
        {
            Releases++;
            return Task.CompletedTask;
        }
    }

    private sealed class SolidFrameSource : IFrameSource
    {
        public ValueTask<Bitmap> CaptureAsync(CancellationToken cancellationToken = default)
        {
            var image = new Bitmap(20, 10, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(image);
            graphics.Clear(Color.Blue);
            return ValueTask.FromResult(image);
        }
    }

    private sealed class FakeOcrProvider : IOcrProvider
    {
        public string? LastLanguage { get; private set; }
        public Size LastImageSize { get; private set; }

        public Task<IReadOnlyList<OcrTextLine>> RecognizeAsync(Bitmap image, string? languageTag = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastLanguage = languageTag;
            LastImageSize = image.Size;
            IReadOnlyList<OcrTextLine> result = [new OcrTextLine(new Rectangle(2, 1, 5, 3), "Start", 0.75)];
            return Task.FromResult(result);
        }
    }

    private static async Task RunLunarNxAsync(string executable)
    {
        string name = "AutoPlay-real-test-" + Guid.NewGuid().ToString("N");
        using var process = Process.Start(new ProcessStartInfo(executable)
        {
            ArgumentList = { @"\\.\pipe\" + name },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))!
        }) ?? throw new Exception("Unable to start LunarNX test server.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var client = await LunarNxPipe.ConnectAsync(name, timeout.Token);
            using var frame = await client.CaptureAsync(timeout.Token);
            Check(frame.Width == 320 && frame.Height == 240, "Unexpected LunarNX screenshot dimensions.");
            await client.SetStateAsync(new GamepadState { A = true, LeftStickX = 1234, LeftStickY = -2345, LeftTrigger = 54321 }, timeout.Token);
            await Task.Delay(80, timeout.Token);
            await client.ReleaseAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            Check(process.ExitCode == 0, $"LunarNX server rejected integration: {await stdout} {await stderr}");
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
        Console.WriteLine("Real LunarNX automation server check passed.");
    }
}
