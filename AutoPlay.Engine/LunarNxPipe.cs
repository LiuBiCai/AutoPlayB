using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace AutoPlay.Engine;

public sealed class LunarNxPipe : IFrameSource, IGamepadOutput, IDisposable
{
    private const int MaxDimension = 4096;
    private readonly NamedPipeClientStream pipe;
    private readonly SemaphoreSlim gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private bool disposed;

    private LunarNxPipe(NamedPipeClientStream pipe) => this.pipe = pipe;

    public static async Task<LunarNxPipe> ConnectAsync(string pipeName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        const string prefix = @"\\.\pipe\";
        if (pipeName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) pipeName = pipeName[prefix.Length..];
        if (pipeName.Length == 0 || pipeName.Contains('\\') || pipeName.Contains('/'))
            throw new ArgumentException("Use a local pipe name or a local \\\\.\\pipe\\ path.", nameof(pipeName));
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(cancellationToken);
            return new LunarNxPipe(pipe);
        }
        catch { pipe.Dispose(); throw; }
    }

    public async Task SetStateAsync(GamepadState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        await ExchangeAsync(new { type = "input", state }, false, cancellationToken);
    }

    public async Task ReleaseAsync(CancellationToken cancellationToken = default)
        => _ = await ExchangeAsync(new { type = "release" }, false, cancellationToken);

    public async ValueTask<Bitmap> CaptureAsync(CancellationToken cancellationToken = default)
    {
        var (data, width, height) = await ExchangeAsync(new { type = "screenshot" }, true, cancellationToken);
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        try
        {
            var bits = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[width * 4];
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int input = (y * width + x) * 3, output = x * 4;
                        row[output] = data[input + 2];
                        row[output + 1] = data[input + 1];
                        row[output + 2] = data[input];
                        row[output + 3] = 255;
                    }
                    Marshal.Copy(row, 0, bits.Scan0 + y * bits.Stride, row.Length);
                }
            }
            finally { bitmap.UnlockBits(bits); }
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    private async Task<(byte[] Data, int Width, int Height)> ExchangeAsync<T>(T request, bool screenshot, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            try
            {
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, JsonOptions) + "\n");
                await pipe.WriteAsync(bytes, timeout.Token);
                await pipe.FlushAsync(timeout.Token);
                using var header = JsonDocument.Parse(await ReadLineAsync(timeout.Token));
                var root = header.RootElement;
                if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new InvalidDataException("Invalid LunarNX response.");
                if (!ok.GetBoolean())
                {
                    var error = root.TryGetProperty("error", out var value) ? value.ToString() : "unknown error";
                    throw new InvalidOperationException($"LunarNX request failed: {error}");
                }
                if (!screenshot) return ([], 0, 0);
                if (root.GetProperty("format").GetString() != "RGB24") throw new InvalidDataException("Unsupported LunarNX screenshot format.");
                int width = root.GetProperty("width").GetInt32(), height = root.GetProperty("height").GetInt32();
                if (width <= 0 || height <= 0 || width > MaxDimension || height > MaxDimension ||
                    root.GetProperty("bytes").GetInt32() != width * height * 3)
                    throw new InvalidDataException("Invalid LunarNX screenshot dimensions or byte count.");
                var data = new byte[width * height * 3];
                await pipe.ReadExactlyAsync(data, timeout.Token);
                return (data, width, height);
            }
            catch
            {
                // A partially read or canceled response cannot safely be reused.
                Dispose();
                throw;
            }
        }
        finally { gate.Release(); }
    }

    private async Task<byte[]> ReadLineAsync(CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var single = new byte[1];
        while (buffer.Length < 8192)
        {
            if (await pipe.ReadAsync(single, cancellationToken) == 0) throw new EndOfStreamException("LunarNX pipe closed before response.");
            if (single[0] == '\n') return buffer.ToArray();
            buffer.WriteByte(single[0]);
        }
        throw new InvalidDataException("LunarNX response header is too long.");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        pipe.Dispose();
    }
}
