namespace KsaScripting.Runtime.V8.Tests;

[TestFixture]
public sealed class QueueTests
{
    private ScriptWorkQueue _queue = null!;

    [SetUp]
    public void SetUp()
    {
        _queue = new ScriptWorkQueue { GameThreadId = Environment.CurrentManagedThreadId };
    }

    [Test]
    public void Post_FromAnyThread_QueuesTaggedWorkInFifoOrder()
    {
        var context = new GameThreadSynchronizationContext("tagged", _queue);
        var order = new List<int>();

        context.Post(_ => order.Add(1), null);
        Task.Run(() => context.Post(_ => order.Add(2), null)).GetAwaiter().GetResult();
        context.Post(state => order.Add((int)state!), 3);

        Assert.That(_queue.IsEmpty, Is.False);
        while (_queue.TryDequeue(out var item))
        {
            Assert.That(item.Context, Is.SameAs(context));
            Assert.That(item.Context.ScriptName, Is.EqualTo("tagged"));
            item.Callback(item.State);
        }

        Assert.That(order, Is.EqualTo(new[] { 1, 2, 3 }));
        Assert.That(_queue.IsEmpty, Is.True);
        Assert.That(_queue.TryDequeue(out _), Is.False);
    }

    [Test]
    public void Send_RunsInlineOnTheGameThread_AndThrowsElsewhere()
    {
        var context = new GameThreadSynchronizationContext("sender", _queue);
        var ran = false;

        context.Send(_ => ran = true, null);
        Assert.That(ran, Is.True);

        var ex = Task.Run(() => Assert.Throws<NotSupportedException>(() => context.Send(_ => { }, null))).GetAwaiter().GetResult();
        Assert.That(ex, Is.Not.Null);
        Assert.That(_queue.IsEmpty, Is.True);
    }

    [Test]
    public void CreateCopy_ReturnsTheSameInstance()
    {
        var context = new GameThreadSynchronizationContext("copy", _queue);

        Assert.That(context.CreateCopy(), Is.SameAs(context));
    }

    [Test]
    public void DisposedContext_IgnoresPosts()
    {
        var context = new GameThreadSynchronizationContext("gone", _queue);
        context.Dispose();

        context.Post(_ => Assert.Fail("must not run"), null);

        Assert.That(context.IsDisposed, Is.True);
        Assert.That(_queue.IsEmpty, Is.True);
    }

    [Test]
    public void Scheduler_RunsTasksAsQueueItems()
    {
        var context = new GameThreadSynchronizationContext("sched", _queue);
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var continuation = completion.Task.ContinueWith(
            t => t.Result * 2,
            CancellationToken.None,
            TaskContinuationOptions.None,
            context.Scheduler);

        completion.SetResult(21);

        Assert.That(_queue.WaitForWork(TimeSpan.FromSeconds(10)), Is.True);
        Assert.That(continuation.IsCompleted, Is.False, "the continuation waits for the game thread to drain the queue");
        Assert.That(_queue.TryDequeue(out var item), Is.True);
        item.Callback(item.State);
        Assert.That(continuation.Result, Is.EqualTo(42));
    }

    [Test]
    public void WaitForWork_ReturnsFalseOnTimeout_AndTrueWhenSignalled()
    {
        var context = new GameThreadSynchronizationContext("signal", _queue);

        Assert.That(_queue.WaitForWork(TimeSpan.FromMilliseconds(1)), Is.False);
        context.Post(_ => { }, null);
        Assert.That(_queue.WaitForWork(TimeSpan.FromMilliseconds(1)), Is.True);
        Assert.That(_queue.TryDequeue(out _), Is.True);
        Assert.That(_queue.TryDequeue(out _), Is.False);
        Assert.That(_queue.WaitForWork(TimeSpan.FromMilliseconds(1)), Is.False, "the signal is reset once the queue drains");
    }

    [Test]
    public void Availability_IsStableAndProbesTheRuntimeNativeFolder()
    {
        Assert.That(V8Availability.TryEnsure(out var reason), Is.True, reason);
        Assert.That(reason, Is.Null);
        Assert.That(V8Availability.TryEnsure(out _), Is.True, "cached");
        Assert.That(V8Availability.SearchPaths().Last(), Does.EndWith(Path.Combine("runtimes", V8Availability.RuntimeIdentifier, "native")));
        Assert.That(V8Availability.RuntimeIdentifier, Does.Match("^(win|linux|osx)-(x86|x64|arm|arm64)$"));
        Assert.That(Directory.Exists(V8Availability.SearchPaths().Last()), Is.True, "natives are copied beside the assembly");
        Assert.That(Directory.GetFiles(V8Availability.SearchPaths().Last(), "ClearScriptV8.*"), Is.Not.Empty);
    }
}
