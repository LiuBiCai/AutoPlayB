using AutoPlay.Engine;
using System.Drawing;

namespace SampleScript;

public sealed class ExampleScript : IGameScript
{
    public async Task RunAsync(ScriptContext context, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var frame = context.Capture();
            var color = frame.FindColor(Color.Red, tolerance: 10);
            var text = await frame.FindTextAsync("开始", "zh-CN", cancellationToken: cancellationToken);
            Console.WriteLine($"Red: {color?.Center}; text: {text?.Text} {text?.Center}");

            if (context.TestMode)
                frame.SaveAnnotated($"diagnostics-{context.Window.Handle.ToInt64():X}.png");
            else if (text is not null)
                context.Window.Click(text.Center);

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }
}
