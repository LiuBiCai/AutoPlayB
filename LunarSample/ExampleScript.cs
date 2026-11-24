using AutoPlay.Engine;

namespace LunarSample;

public sealed class ExampleScript : IGameScript
{
    public async Task RunAsync(ScriptContext context, CancellationToken cancellationToken)
    {
        while (true)
        {
            using var frame = await context.CaptureAsync(cancellationToken);
            var text = await frame.FindTextAsync("开始", "zh-CN", cancellationToken: cancellationToken);
            Console.WriteLine($"Text: {text?.Text} {text?.Center}");
            if (context.TestMode)
                frame.SaveAnnotated("lunarnx-diagnostics.png");
            else if (text is not null)
                await context.Gamepad.HoldAsync(new GamepadState { A = true }, TimeSpan.FromMilliseconds(150), cancellationToken);
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }
}
