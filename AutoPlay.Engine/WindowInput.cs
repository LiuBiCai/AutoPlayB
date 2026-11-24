using System.Drawing;

namespace AutoPlay.Engine;

public interface IWindowInput
{
    void Click(Point point);
    void KeyPress(int virtualKey);
}
