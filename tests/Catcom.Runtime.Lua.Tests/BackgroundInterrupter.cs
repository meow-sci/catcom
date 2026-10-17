namespace Catcom.Runtime.Lua.Tests;

/// <summary>
///     Calls <see cref="LuaRuntime.Interrupt"/> from a background thread once a script signals that it is running,
///     and keeps asking (every 25 ms, without ever delaying the happy path) until the test marks the call finished.
/// </summary>
internal sealed class BackgroundInterrupter : IDisposable
{
    private readonly ManualResetEventSlim _done = new(false);
    private readonly Task _task;

    /// <summary>Starts waiting for <paramref name="running"/>.</summary>
    /// <param name="runtime">The runtime to interrupt.</param>
    /// <param name="running">Completes when the script is inside the call to interrupt.</param>
    public BackgroundInterrupter(LuaRuntime runtime, Task running)
    {
        _task = Task.Run(async () =>
        {
            await running.WaitAsync(TimeSpan.FromSeconds(10));
            do
            {
                runtime.Interrupt();
            }
            while (!_done.Wait(TimeSpan.FromMilliseconds(25)));
        });
    }

    /// <summary>Tells the interrupter the call has returned and waits for it to stop.</summary>
    public void Finish()
    {
        _done.Set();
        Assert.That(_task.Wait(TimeSpan.FromSeconds(10)), Is.True, "the interrupter did not stop");
    }

    /// <inheritdoc />
    public void Dispose() => _done.Dispose();
}
