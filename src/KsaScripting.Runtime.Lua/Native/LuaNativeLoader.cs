#pragma warning disable CA2255 // [ModuleInitializer] in a library: the DllImport resolver must be registered before any P/Invoke.
#pragma warning disable IL3000 // Assembly.Location is empty only in single-file apps; AppContext.BaseDirectory is the fallback.

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KsaScripting.Runtime.Lua.Native;

/// <summary>
///     Locates and loads the LuaJIT native library by absolute path and serves it to the
///     <c>[LibraryImport("lua51")]</c> declarations through a <see cref="NativeLibrary.SetDllImportResolver"/>
///     resolver. The handle is cached for the life of the process and never freed.
/// </summary>
/// <remarks>
///     Probe order, first hit wins: <c>KSA_LUAJIT_LIBRARY</c> (absolute path override), then
///     <c>&lt;asmDir&gt;/runtimes/&lt;RuntimeInformation.RuntimeIdentifier&gt;/native/&lt;file&gt;</c>, then
///     <c>&lt;asmDir&gt;/runtimes/&lt;os&gt;-&lt;arch&gt;/native/&lt;file&gt;</c>, then <c>&lt;asmDir&gt;/&lt;file&gt;</c>.
/// </remarks>
internal static class LuaNativeLoader
{
    /// <summary>The logical DllImport name.</summary>
    public const string LibraryName = "lua51";

    /// <summary>The environment variable that overrides the library location (absolute path).</summary>
    public const string OverrideEnvironmentVariable = "KSA_LUAJIT_LIBRARY";

    /// <summary>The shim exports that must be present for the library to be usable.</summary>
    private static readonly string[] ShimExports = ["host_traceback", "host_raise", "host_timeout_hook"];

    private static readonly object Gate = new();
    private static bool _attempted;
    private static nint _handle;
    private static string? _reason;
    private static string? _loadedPath;
    private static bool _resolverRegistered;

    [ModuleInitializer]
    internal static void Initialize() => EnsureResolverRegistered();

    /// <summary>The path the library was loaded from, or <c>null</c> when it is not loaded.</summary>
    public static string? LoadedPath
    {
        get
        {
            lock (Gate)
                return _loadedPath;
        }
    }

    /// <summary>
    ///     Loads the library (once) and verifies the ksa shim exports. Thread-safe and idempotent.
    /// </summary>
    /// <param name="reason">Why the library is unusable, or <c>null</c> on success.</param>
    /// <returns><c>true</c> when the library is loaded and exports the shim.</returns>
    public static bool TryEnsureLoaded(out string? reason)
    {
        lock (Gate)
        {
            EnsureResolverRegistered();
            if (!_attempted)
            {
                _attempted = true;
                LoadCore();
            }

            reason = _reason;
            return _handle != 0 && _reason is null;
        }
    }

    /// <summary>Gets the raw address of a shim export. Only valid after <see cref="TryEnsureLoaded"/> returned <c>true</c>.</summary>
    /// <param name="name">The export name.</param>
    /// <returns>The function address.</returns>
    public static nint GetShimExport(string name)
    {
        lock (Gate)
        {
            if (_handle == 0)
                throw new InvalidOperationException("The LuaJIT native library is not loaded.");
            return NativeLibrary.GetExport(_handle, name);
        }
    }

    private static void EnsureResolverRegistered()
    {
        lock (Gate)
        {
            if (_resolverRegistered)
                return;
            _resolverRegistered = true;
            try
            {
                NativeLibrary.SetDllImportResolver(typeof(LuaNativeLoader).Assembly, Resolve);
            }
            catch (InvalidOperationException)
            {
                // A resolver was already registered for this assembly (e.g. by a host); P/Invokes will then use it.
            }
        }
    }

    private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != LibraryName)
            return 0;

        // Called once per imported entry point: cheap, idempotent, cached.
        lock (Gate)
        {
            if (!_attempted)
            {
                _attempted = true;
                LoadCore();
            }

            return _handle;
        }
    }

    // Must be called with Gate held.
    private static void LoadCore()
    {
        var result = TryLoadFrom(Candidates());
        _handle = result.Handle;
        _loadedPath = result.Path;
        _reason = result.Reason;
    }

    /// <summary>The outcome of probing a list of candidate library paths.</summary>
    /// <param name="Handle">The loaded library handle, or 0.</param>
    /// <param name="Path">The path that was loaded (or rejected for missing the shim), if any.</param>
    /// <param name="Reason">Why no usable library was found, or <c>null</c> on success.</param>
    internal readonly record struct ProbeResult(nint Handle, string? Path, string? Reason);

    /// <summary>
    ///     Probes <paramref name="candidates"/> in order and loads the first existing library that exports the
    ///     ksa host shim. Pure with respect to the loader's cached state (used by tests).
    /// </summary>
    /// <param name="candidates">Absolute library paths, in priority order.</param>
    /// <returns>The probe outcome.</returns>
    internal static ProbeResult TryLoadFrom(IEnumerable<string> candidates)
    {
        var probed = new List<string>();
        var errors = new List<string>();

        foreach (var candidate in candidates)
        {
            probed.Add(candidate);
            if (!File.Exists(candidate))
                continue;

            if (!NativeLibrary.TryLoad(candidate, out var handle))
            {
                errors.Add($"{candidate} (exists but could not be loaded)");
                continue;
            }

            if (ShimExports.Any(e => !NativeLibrary.TryGetExport(handle, e, out _)))
            {
                // A rejected library is never used, so its handle is released (only an accepted handle is cached for good).
                NativeLibrary.Free(handle);
                return new ProbeResult(0, candidate, "LuaJIT native library is missing the ksa host shim (rebuild with tools/build-luajit.*)");
            }

            return new ProbeResult(handle, candidate, null);
        }

        var sb = new System.Text.StringBuilder("LuaJIT native library not found. Probed: ");
        sb.Append(string.Join(", ", probed));
        if (errors.Count > 0)
            sb.Append(". Load failures: ").Append(string.Join("; ", errors));
        return new ProbeResult(0, null, sb.ToString());
    }

    internal static IEnumerable<string> Candidates()
    {
        var overridePath = Environment.GetEnvironmentVariable(OverrideEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
            yield return overridePath;

        var file = NativeFileName();
        var asmDir = AssemblyDirectory();

        var rid = RuntimeInformation.RuntimeIdentifier;
        if (!string.IsNullOrEmpty(rid))
            yield return Path.Combine(asmDir, "runtimes", rid, "native", file);

        var osArch = OsArchRid();
        if (osArch is not null && !string.Equals(osArch, rid, StringComparison.Ordinal))
            yield return Path.Combine(asmDir, "runtimes", osArch, "native", file);

        yield return Path.Combine(asmDir, file);
    }

    private static string AssemblyDirectory()
    {
        var location = typeof(LuaNativeLoader).Assembly.Location;
        var dir = string.IsNullOrEmpty(location) ? null : Path.GetDirectoryName(location);
        return string.IsNullOrEmpty(dir) ? AppContext.BaseDirectory : dir;
    }

    private static string NativeFileName()
    {
        if (OperatingSystem.IsWindows())
            return "lua51.dll";
        if (OperatingSystem.IsMacOS())
            return "libluajit-5.1.2.dylib";
        return "libluajit-5.1.so.2";
    }

    private static string? OsArchRid()
    {
        string os;
        if (OperatingSystem.IsWindows())
            os = "win";
        else if (OperatingSystem.IsLinux())
            os = "linux";
        else if (OperatingSystem.IsMacOS())
            os = "osx";
        else
            return null;

        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => null,
        };
        return arch is null ? null : $"{os}-{arch}";
    }
}
