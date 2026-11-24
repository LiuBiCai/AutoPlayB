using System.Drawing;

namespace AutoPlay.Engine;

public interface IFrameSource
{
    // Return a Format32bppArgb bitmap; the caller owns and disposes it.
    ValueTask<Bitmap> CaptureAsync(CancellationToken cancellationToken = default);
}
