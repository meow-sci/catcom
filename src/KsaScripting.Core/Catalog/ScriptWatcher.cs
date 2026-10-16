using System.Security.Cryptography;
using KsaScripting.Core.Logging;

namespace KsaScripting.Core.Catalog;

/// <summary>
///     Watches the scripts folder for edits (including edits made by the shipped Neovim). Events are
///     debounced per path and checked against the SHA-256 of the file content, so a save that did not
///     change anything (or the several events one save raises) reports once. The callback runs on a
///     thread-pool thread: the caller marshals it to the game thread.
/// </summary>
public sealed class ScriptWatcher : IDisposable
{
    private const string DeletedHash = "deleted";

    private static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(150);

    private readonly Action<string> _onChanged;
    private readonly TimeSpan _debounce;
    private readonly FileSystemWatcher _watcher;
    private readonly object _gate = new();
    private readonly Dictionary<string, Timer> _timers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _hashes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _retried = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>Starts watching a folder recursively for <c>*.lua</c> and <c>*.ts</c> files. The folder is created when missing.</summary>
    /// <param name="dir">The scripts folder.</param>
    /// <param name="onChanged">Called with the full path of a file whose content changed, appeared or was deleted.</param>
    /// <param name="debounce">The quiet period after the last event of a path (default 150 ms).</param>
    public ScriptWatcher(string dir, Action<string> onChanged, TimeSpan? debounce = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(dir);
        ArgumentNullException.ThrowIfNull(onChanged);

        _onChanged = onChanged;
        _debounce = debounce is { } d && d >= TimeSpan.Zero ? d : DefaultDebounce;

        Directory.CreateDirectory(dir);
        _watcher = new FileSystemWatcher(dir)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        _watcher.Filters.Add("*.lua");
        _watcher.Filters.Add("*.ts");
        _watcher.Changed += OnEvent;
        _watcher.Created += OnEvent;
        _watcher.Deleted += OnEvent;
        _watcher.Renamed += OnRenamed;
        _watcher.Error += OnError;
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>Stops watching and cancels the pending debounce timers.</summary>
    public void Dispose()
    {
        Timer[] timers;
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            timers = _timers.Values.ToArray();
            _timers.Clear();
        }

        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        foreach (var timer in timers)
            timer.Dispose();
    }

    private void OnEvent(object sender, FileSystemEventArgs e) => Schedule(e.FullPath);

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        Schedule(e.OldFullPath);
        Schedule(e.FullPath);
    }

    private static void OnError(object sender, ErrorEventArgs e)
        => Log.LogOnce("watcher.error", LogLevel.Warn, $"The script folder watcher reported an error: {e.GetException().Message}");

    private void Schedule(string path)
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            if (_timers.TryGetValue(path, out var existing))
            {
                existing.Change(_debounce, Timeout.InfiniteTimeSpan);
                return;
            }

            _timers[path] = new Timer(_ => Fire(path), null, _debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void Fire(string path)
    {
        string hash;
        try
        {
            hash = ComputeHash(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Probably locked by the editor mid-save: look again after another quiet period, once.
            bool retry;
            lock (_gate)
            {
                retry = !_disposed && _retried.Add(path);
                if (retry && _timers.TryGetValue(path, out var timer))
                    timer.Change(_debounce, Timeout.InfiniteTimeSpan);
            }

            if (!retry)
            {
                Log.LogOnce("watcher.read:" + path, LogLevel.Warn, $"Could not read '{path}': {ex.Message}");
                lock (_gate)
                {
                    _retried.Remove(path);
                }
            }

            return;
        }

        lock (_gate)
        {
            if (_disposed)
                return;

            _retried.Remove(path);
            if (_hashes.TryGetValue(path, out var previous) && string.Equals(previous, hash, StringComparison.Ordinal))
                return;

            _hashes[path] = hash;
        }

        try
        {
            _onChanged(path);
        }
        catch (Exception ex)
        {
            Log.LogOnce("watcher.callback", LogLevel.Warn, $"The script change callback failed: {ex.Message}");
        }
    }

    private static string ComputeHash(string path)
    {
        if (!File.Exists(path))
            return DeletedHash;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (FileNotFoundException)
        {
            return DeletedHash;
        }
        catch (DirectoryNotFoundException)
        {
            return DeletedHash;
        }
    }
}
