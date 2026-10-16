using KsaScripting.Core.Hosting;
using KsaScripting.Core.Values;

namespace KsaScripting.Core.Tests.Hosting;

[TestFixture]
public sealed class HostWaitTests
{
    [Test]
    public void Ctor_ExposesIdAndKind_AndStartsPending()
    {
        var wait = new HostWait(5, "sleep");
        Assert.Multiple(() =>
        {
            Assert.That(wait.Id, Is.EqualTo(5));
            Assert.That(wait.Kind, Is.EqualTo("sleep"));
            Assert.That(wait.IsCompleted, Is.False);
            Assert.That(wait.Task.IsCompleted, Is.False);
        });
    }

    [Test]
    public async Task Complete_SetsTheTaskResult_AndASecondCompleteReturnsFalse()
    {
        var wait = new HostWait(1, "nextStep");

        var first = wait.Complete(ScriptValue.From(42));
        var second = wait.Complete(ScriptValue.From(43));
        var result = await wait.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.True);
            Assert.That(second, Is.False);
            Assert.That(wait.IsCompleted, Is.True);
            Assert.That(result.AsNumber(), Is.EqualTo(42));
            Assert.That(wait.Fail(new InvalidOperationException()), Is.False);
            Assert.That(wait.Cancel(), Is.False);
        });
    }

    [Test]
    public void Fail_FaultsTheTask()
    {
        var wait = new HostWait(1, "act");
        var error = new InvalidOperationException("nope");

        Assert.Multiple(() =>
        {
            Assert.That(wait.Fail(error), Is.True);
            Assert.That(wait.Task.IsFaulted, Is.True);
            Assert.That(wait.Task.Exception!.InnerException, Is.SameAs(error));
            Assert.That(wait.IsCompleted, Is.True);
            Assert.That(wait.Complete(ScriptValue.Null), Is.False);
        });
    }

    [Test]
    public void Cancel_CancelsTheTask()
    {
        var wait = new HostWait(1, "sleep");

        Assert.Multiple(() =>
        {
            Assert.That(wait.Cancel(), Is.True);
            Assert.That(wait.Task.IsCanceled, Is.True);
            Assert.That(wait.IsCompleted, Is.True);
            Assert.That(wait.Cancel(), Is.False);
        });
    }

    [Test]
    public void Task_RunsContinuationsAsynchronously()
    {
        var wait = new HostWait(1, "sleep");
        Assert.That(wait.Task.CreationOptions.HasFlag(TaskCreationOptions.RunContinuationsAsynchronously), Is.True);
    }

    [Test]
    public async Task Complete_DoesNotRunContinuationsInline()
    {
        var wait = new HostWait(1, "sleep");
        var continuationThread = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = wait.Task.ContinueWith(
            _ => continuationThread.TrySetResult(Environment.CurrentManagedThreadId),
            TaskContinuationOptions.ExecuteSynchronously);

        var completer = Environment.CurrentManagedThreadId;
        wait.Complete(ScriptValue.Null);
        var ranOn = await continuationThread.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.That(ranOn, Is.Not.EqualTo(completer), "RunContinuationsAsynchronously keeps foreign code off the completing (game) thread");
    }

    [Test]
    public void ToScriptValue_IsTheWaitObject()
    {
        var value = new HostWait(77, "until").ToScriptValue();
        Assert.Multiple(() =>
        {
            Assert.That(value.Kind, Is.EqualTo(ScriptValueKind.Map));
            Assert.That(value.AsMap().Keys, Is.EqualTo(new[] { "$wait" }));
            Assert.That(value["$wait"].AsNumber(), Is.EqualTo(77));
            Assert.That(value.ToJson(), Is.EqualTo("{\"$wait\":77}"));
        });
    }
}
