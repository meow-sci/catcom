using Catcom.Core.Values;

namespace Catcom.Core.Tests.Values;

[TestFixture]
public sealed class ScriptCallableTests
{
    [Test]
    public void Create_ExposesItsIdentity()
    {
        var callable = ScriptCallable.Create("v8", 12, "onTick", _ => { });
        Assert.Multiple(() =>
        {
            Assert.That(callable.RuntimeId, Is.EqualTo("v8"));
            Assert.That(callable.Token, Is.EqualTo(12));
            Assert.That(callable.Name, Is.EqualTo("onTick"));
            Assert.That(callable.IsDisposed, Is.False);
            Assert.That(callable.ToString(), Is.EqualTo("v8:fn#12(onTick)"));
        });
    }

    [Test]
    public void Dispose_InvokesTheCallbackOnce_WithTheCallable()
    {
        var calls = new List<ScriptCallable>();
        var callable = ScriptCallable.Create("lua", 1, null, calls.Add);

        callable.Dispose();
        callable.Dispose();

        Assert.Multiple(() =>
        {
            Assert.That(calls, Has.Count.EqualTo(1));
            Assert.That(calls[0], Is.SameAs(callable));
            Assert.That(callable.IsDisposed, Is.True);
        });
    }

    [Test]
    public async Task Dispose_Concurrently_InvokesTheCallbackExactlyOnce()
    {
        var count = 0;
        var callable = ScriptCallable.Create("lua", 1, "f", _ => Interlocked.Increment(ref count));
        using var start = new ManualResetEventSlim(false);

        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            start.Wait();
            callable.Dispose();
        })).ToArray();
        start.Set();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.That(count, Is.EqualTo(1));
    }

    [Test]
    public void NullArguments_AreRejected()
    {
        Assert.Multiple(() =>
        {
            Assert.That(() => ScriptCallable.Create(null!, 1, null, _ => { }), Throws.ArgumentNullException);
            Assert.That(() => ScriptCallable.Create("lua", 1, null, null!), Throws.ArgumentNullException);
        });
    }
}
