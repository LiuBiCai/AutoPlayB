using System.ComponentModel;
using System.Drawing;
using System.Text;

namespace AutoPlay.Engine;

public sealed record WindowInfo(nint Handle, string Title);

public sealed class GameWindow : IFrameSource, IWindowInput
{
    public nint Handle { get; }

    public GameWindow(nint handle)
    {
        if (!Native.IsWindow(handle)) throw new ArgumentException("Window handle is invalid.", nameof(handle));
        Handle = handle;
    }

    public static IReadOnlyList<WindowInfo> List()
    {
        var windows = new List<WindowInfo>();
        Native.EnumWindows((handle, _) =>
        {
            if (!Native.IsWindowVisible(handle)) return true;
            var title = new StringBuilder(1024);
            Native.GetWindowText(handle, title, title.Capacity);
            if (title.Length > 0) windows.Add(new WindowInfo(handle, title.ToString()));
            return true;
        }, 0);
        return windows;
    }

    internal Rectangle ClientBounds()
    {
        if (!Native.IsWindow(Handle)) throw new InvalidOperationException("The target window no longer exists.");
        if (Native.IsIconic(Handle)) throw new InvalidOperationException("The target window is minimized.");
        if (!Native.GetClientRect(Handle, out var rect)) throw new Win32Exception();
        var point = new Native.Point();
        if (!Native.ClientToScreen(Handle, ref point)) throw new Win32Exception();
        int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) throw new InvalidOperationException("The target window has an empty client area.");
        return new Rectangle(point.X, point.Y, width, height);
    }

    internal Bitmap Capture()
    {
        var bounds = ClientBounds();
        var bitmap = new Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            var dc = graphics.GetHdc();
            try
            {
                if (!Native.PrintWindow(Handle, dc, 3)) throw new Win32Exception("PrintWindow failed.");
            }

            finally { graphics.ReleaseHdc(dc); }
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    public ValueTask<Bitmap> CaptureAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Capture());
    }

    public void Click(Point point)
    {
        var bounds = ClientBounds();
        if (point.X < 0 || point.Y < 0 || point.X >= bounds.Width || point.Y >= bounds.Height ||
            point.X > short.MaxValue || point.Y > short.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(point), "Click must be inside the client area and fit in a Windows message.");
        nint position = (point.Y << 16) | (point.X & 0xffff);
        Post(0x0200, 0, position);
        Post(0x0201, 1, position);
        Post(0x0202, 0, position);
    }

    public void KeyPress(int virtualKey)
    {
        if (virtualKey is < 1 or > 255) throw new ArgumentOutOfRangeException(nameof(virtualKey));
        if (!Native.IsWindow(Handle)) throw new InvalidOperationException("The target window no longer exists.");
        uint scanCode = Native.MapVirtualKey((uint)virtualKey, 0) & 0xff;
        bool extended = virtualKey is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28
            or 0x2d or 0x2e or 0x6f or 0x90 or 0xa3 or 0xa5;
        uint flags = 1 | (scanCode << 16) | (extended ? 1u << 24 : 0);
        Post(0x0100, virtualKey, (nint)flags);
        Post(0x0101, virtualKey, (nint)(flags | 0xC0000000u));
    }

    private void Post(uint message, nint wParam, nint lParam)
    {
        if (!Native.PostMessage(Handle, message, wParam, lParam)) throw new Win32Exception();
    }
}
