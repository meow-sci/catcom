using System.Collections.Concurrent;
using Catcom.Core.Catalog;

namespace Catcom.Core.Tests.Catalog;

[TestFixture]
public sealed class ScriptWatcherTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(50);

    private string _dir = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "catcom-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp folder is harmless.
        }
    }

    /// <summary>Collects reported paths and lets a test wait (with a deadline) for the next one.</summary>
    private sealed class Collector
    {
        private readonly ConcurrentQueue<string> _paths = new();
        private readonly SemaphoreSlim _arrived = new(0);

        public IReadOnlyCollection<string> Paths => _paths.ToArray();

        public Action<string> Callback => path =>
        {
            _paths.Enqueue(path);
            _arrived.Release();
        };

        public bool WaitForNext(TimeSpan timeout) => _arrived.Wait(timeout);

        public string WaitForName(string fileName)
        {
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (deadline.Elapsed < Deadline)
            {
                var hit = _paths.FirstOrDefault(p => Path.GetFileName(p) == fileName);
                if (hit is not null)
                    return hit;
                _arrived.Wait(TimeSpan.FromMilliseconds(100));
            }

            Assert.Fail($"no change was reported for '{fileName}' within {Deadline.TotalSeconds:0} s");
            return "";
        }

        public void Drain(TimeSpan quiet)
        {
            while (_arrived.Wait(quiet))
            {
            }
        }
    }

    [Test]
    public void ANewScriptFile_IsReported()
    {
        var collector = new Collector();
        using var watcher = new ScriptWatcher(_dir, collector.Callback, Debounce);

        File.WriteAllText(Path.Combine(_dir, "a.lua"), "print('hi')");

        var path = collector.WaitForName("a.lua");
        Assert.That(Path.GetFileName(path), Is.EqualTo("a.lua"));
    }

    [Test]
    public void RewritingIdenticalContent_DoesNotReportAgain()
    {
        var collector = new Collector();
        using var watcher = new ScriptWatcher(_dir, collector.Callback, Debounce);
        var file = Path.Combine(_dir, "a.lua");
        File.WriteAllText(file, "same content");
        collector.WaitForName("a.lua");
        collector.Drain(Settle);
        var reported = collector.Paths.Count;

        File.WriteAllText(file, "same content");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));

        Assert.Multiple(() =>
        {
            Assert.That(collector.WaitForNext(QuietPeriod), Is.False, "the content hash did not change");
            Assert.That(collector.Paths, Has.Count.EqualTo(reported));
        });
    }

    [Test]
    public void ChangedContent_IsReportedAgain()
    {
        var collector = new Collector();
        using var watcher = new ScriptWatcher(_dir, collector.Callback, Debounce);
        var file = Path.Combine(_dir, "a.lua");
        File.WriteAllText(file, "version 1");
        collector.WaitForName("a.lua");
        collector.Drain(Settle);
        var before = collector.Paths.Count;

        File.WriteAllText(file, "version 2");

        Assert.That(collector.WaitForNext(Deadline), Is.True, "the edit was never reported");
        Assert.That(collector.Paths.Count, Is.GreaterThan(before));
    }

    [Test]
    public void ADeletedFile_IsReportedOnce()
    {
        var collector = new Collector();
        using var watcher = new ScriptWatcher(_dir, collector.Callback, Debounce);
        var file = Path.Combine(_dir, "a.lua");
        File.WriteAllText(file, "bye");
        collector.WaitForName("a.lua");
        collector.Drain(Settle);
        var before = collector.Paths.Count;

        File.Delete(file);

        Assert.That(collector.WaitForNext(Deadline), Is.True, "the deletion was never reported");
        collector.Drain(Settle);
        Assert.That(collector.Paths.Count, Is.EqualTo(before + 1));
    }

    [Test]
    public void FilesInSubfolders_AndTypeScriptFiles_AreReported_ButOtherExtensionsAreNot()
    {
        var collector = new Collector();
        using var watcher = new ScriptWatcher(_dir, collector.Callback, Debounce);
        // The folders appear after the watcher started: a script written into a just-created folder
        // must still be reported (on Linux the new folder's inotify watch arrives late; the watcher
        // scans new folders to cover that).
        Directory.CreateDirectory(Path.Combine(_dir, "sub", "deeper"));

        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "ignored");
        File.WriteAllText(Path.Combine(_dir, "sub", "readme.md"), "ignored");
        File.WriteAllText(Path.Combine(_dir, "sub", "deeper", "x.ts"), "export {}");
        collector.WaitForName("x.ts");
        collector.Drain(Settle);

        Assert.That(collector.Paths.Select(Path.GetFileName).Distinct(), Is.EqualTo(new[] { "x.ts" }));
    }

    [Test]
    public void LibraryFiles_AreReportedToo_TheCatalogDecidesWhatToDo()
    {
        var collector = new Collector();
        using var watcher = new ScriptWatcher(_dir, collector.Callback, Debounce);
        Directory.CreateDirectory(Path.Combine(_dir, "lib"));

        File.WriteAllText(Path.Combine(_dir, "lib", "tui.lua"), "return {}");

        collector.WaitForName("tui.lua");
    }

    [Test]
    public void RapidWrites_AreCoalescedByTheDebounce()
    {
        var collector = new Collector();
        using var watcher = new ScriptWatcher(_dir, collector.Callback, TimeSpan.FromMilliseconds(400));
        var file = Path.Combine(_dir, "a.lua");

        for (var i = 0; i < 20; i++)
            File.WriteAllText(file, "revision " + i);

        collector.WaitForName("a.lua");
        collector.Drain(TimeSpan.FromMilliseconds(600));
        Assert.That(collector.Paths.Count, Is.InRange(1, 3), "twenty writes inside the debounce window report at most a few times");
    }

    [Test]
    public void AfterDispose_NothingIsReported()
    {
        var collector = new Collector();
        var watcher = new ScriptWatcher(_dir, collector.Callback, Debounce);
        File.WriteAllText(Path.Combine(_dir, "a.lua"), "one");
        collector.WaitForName("a.lua");
        collector.Drain(Settle);
        var before = collector.Paths.Count;

        watcher.Dispose();
        watcher.Dispose();
        File.WriteAllText(Path.Combine(_dir, "b.lua"), "two");
        File.WriteAllText(Path.Combine(_dir, "a.lua"), "changed");

        Assert.Multiple(() =>
        {
            Assert.That(collector.WaitForNext(QuietPeriod), Is.False);
            Assert.That(collector.Paths, Has.Count.EqualTo(before));
        });
    }

    [Test]
    public void DisposingWithAPendingDebounce_NeverFires()
    {
        var collector = new Collector();
        var watcher = new ScriptWatcher(_dir, collector.Callback, TimeSpan.FromMilliseconds(300));
        File.WriteAllText(Path.Combine(_dir, "a.lua"), "one");
        System.Threading.Thread.Yield();

        watcher.Dispose();

        Assert.That(collector.WaitForNext(TimeSpan.FromMilliseconds(800)), Is.False);
    }

    [Test]
    public void AThrowingCallback_DoesNotStopFurtherReports()
    {
        var calls = new ConcurrentQueue<string>();
        var done = new SemaphoreSlim(0);
        using var watcher = new ScriptWatcher(_dir, path =>
        {
            calls.Enqueue(path);
            done.Release();
            throw new InvalidOperationException("callback exploded");
        }, Debounce);

        File.WriteAllText(Path.Combine(_dir, "a.lua"), "one");
        Assert.That(done.Wait(Deadline), Is.True);
        File.WriteAllText(Path.Combine(_dir, "a.lua"), "two");

        Assert.That(done.Wait(Deadline), Is.True, "the second edit is still reported");
    }

    [Test]
    public void AMissingFolder_IsCreated()
    {
        var nested = Path.Combine(_dir, "not", "yet");
        var collector = new Collector();

        using var watcher = new ScriptWatcher(nested, collector.Callback, Debounce);

        Assert.That(Directory.Exists(nested), Is.True);
    }
}
