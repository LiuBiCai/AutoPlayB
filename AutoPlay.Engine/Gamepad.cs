namespace AutoPlay.Engine;

public sealed record GamepadState
{
    public bool A { get; init; }
    public bool B { get; init; }
    public bool X { get; init; }
    public bool Y { get; init; }
    public bool DpadUp { get; init; }
    public bool DpadDown { get; init; }
    public bool DpadLeft { get; init; }
    public bool DpadRight { get; init; }
    public bool Lb { get; init; }
    public bool Rb { get; init; }
    public bool Lt { get; init; }
    public bool Rt { get; init; }
    public bool L3 { get; init; }
    public bool R3 { get; init; }
    public bool View { get; init; }
    public bool Menu { get; init; }
    public bool Guide { get; init; }
    public bool Touchpad { get; init; }
    public short LeftStickX { get; init; }
    public short LeftStickY { get; init; }
    public short RightStickX { get; init; }
    public short RightStickY { get; init; }
    public ushort LeftTrigger { get; init; }
    public ushort RightTrigger { get; init; }
}

public interface IGamepadOutput
{
    Task SetStateAsync(GamepadState state, CancellationToken cancellationToken = default);
    Task ReleaseAsync(CancellationToken cancellationToken = default);
}

public static class GamepadOutputExtensions
{
    public static async Task HoldAsync(this IGamepadOutput gamepad, GamepadState state, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gamepad);
        ArgumentNullException.ThrowIfNull(state);
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        await gamepad.SetStateAsync(state, cancellationToken);
        Exception? failure = null;
        try
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            while (elapsed.Elapsed < duration)
            {
                var remaining = duration - elapsed.Elapsed;
                if (remaining <= TimeSpan.Zero) break;
                await Task.Delay(remaining < TimeSpan.FromMilliseconds(100) ? remaining : TimeSpan.FromMilliseconds(100), cancellationToken);
                if (elapsed.Elapsed < duration) await gamepad.SetStateAsync(state, cancellationToken);
            }
        }
        catch (Exception ex) { failure = ex; }
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await gamepad.ReleaseAsync(timeout.Token);
        }
        catch (Exception releaseError) when (failure is not null)
        {
            throw new AggregateException("Gamepad hold and release both failed.", failure, releaseError);
        }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
