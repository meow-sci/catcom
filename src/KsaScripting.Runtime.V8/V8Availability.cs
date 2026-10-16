using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.ClearScript;
using Microsoft.ClearScript.V8;

namespace KsaScripting.Runtime.V8;

/// <summary>
///     Probes whether the ClearScript V8 native library can be loaded on this machine, once per process. Configures
///     <see cref="HostSettings.AuxiliarySearchPath"/> first so a mod folder layout (<c>runtimes/&lt;rid&gt;/native</c>)
///     is found even when the managed assembly was loaded from a custom location.
/// </summary>
internal static class V8Availability
{
    private static readonly object Gate = new();
    private static bool _checked;
    private static string? _reason;

    /// <summary>The runtime identifier of this machine in ClearScript's naming (<c>osx-arm64</c>, <c>linux-x64</c>, <c>win-x64</c>, …).</summary>
    public static string RuntimeIdentifier
    {
        get
        {
            var os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win"
                : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx"
                : "linux";
            var architecture = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X86 => "x86",
                Architecture.X64 => "x64",
                Architecture.Arm => "arm",
                Architecture.Arm64 => "arm64",
                var other => other.ToString().ToLowerInvariant(),
            };
            return os + "-" + architecture;
        }
    }

    /// <summary>The directories probed for the native library (assembly directory first).</summary>
    public static IReadOnlyList<string> SearchPaths()
    {
        var assemblyDirectory = V8Assets.AssemblyDirectory;
        return [assemblyDirectory, Path.Combine(assemblyDirectory, "runtimes", RuntimeIdentifier, "native")];
    }

    /// <summary>
    ///     Ensures the native library loads. The first call creates (and disposes) a throwaway <see cref="V8Runtime"/>;
    ///     the result is cached for the life of the process. Thread-safe and idempotent.
    /// </summary>
    /// <param name="reason">Why V8 is unavailable, or <c>null</c> when it is available.</param>
    /// <returns><c>true</c> when V8 can be used.</returns>
    public static bool TryEnsure(out string? reason)
    {
        lock (Gate)
        {
            if (!_checked)
            {
                _reason = Probe();
                _checked = true;
            }

            reason = _reason;
            return _reason is null;
        }
    }

    private static string? Probe()
    {
        var paths = SearchPaths();
        try
        {
            ConfigureSearchPath(paths);
            using var runtime = new V8Runtime("ksa-v8-probe");
            return null;
        }
        catch (Exception ex)
        {
            // TypeInitializationException, DllNotFoundException, BadImageFormatException, anything else: the
            // native library is unusable. Report the innermost message, it names the file or glibc problem.
            var inner = ex is TypeInitializationException { InnerException: not null } ? ex.InnerException! : ex;
            return $"ClearScript V8 native library failed to load: {inner.Message} (probed {string.Join(", ", paths)})";
        }
    }

    private static void ConfigureSearchPath(IReadOnlyList<string> paths)
    {
        InstallNativeResolver();

        // AuxiliarySearchPath is a semicolon-delimited list; keep whatever the host already configured.
        var existing = HostSettings.AuxiliarySearchPath;
        var parts = string.IsNullOrWhiteSpace(existing)
            ? new List<string>()
            : existing.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        foreach (var path in paths)
        {
            if (!parts.Contains(path, StringComparer.Ordinal))
                parts.Add(path);
        }

        HostSettings.AuxiliarySearchPath = string.Join(';', parts);
    }

    /// <summary>
    ///     ClearScript first loads its native library by ABSOLUTE path (probing the assembly folder, <c>runtimes/&lt;rid&gt;/native</c>,
    ///     the app base and <see cref="HostSettings.AuxiliarySearchPath"/>) but then binds every entry point through
    ///     <c>[DllImport("ClearScriptV8.&lt;rid&gt;.&lt;ext&gt;")]</c> by BARE name. That second step only works when the OS loader can
    ///     find the already-loaded image by name (always on Windows; on Linux/macOS only if the file is also on the default
    ///     probing path). A <see cref="NativeLibrary.SetDllImportResolver"/> for the ClearScript assembly makes the bare-name
    ///     lookup use the same directories, so a mod folder layout works on every OS (verified on osx-arm64 with the library
    ///     reachable only through <c>AuxiliarySearchPath</c>).
    /// </summary>
    private static void InstallNativeResolver()
    {
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(V8Runtime).Assembly, ResolveNativeLibrary);
        }
        catch (InvalidOperationException)
        {
            // Another resolver is already registered for the assembly; leave it alone.
        }
    }

    private static IntPtr ResolveNativeLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!libraryName.StartsWith("ClearScriptV8.", StringComparison.Ordinal))
            return IntPtr.Zero;

        var directories = new List<string>(SearchPaths());
        if (HostSettings.AuxiliarySearchPath is { Length: > 0 } auxiliary)
            directories.AddRange(auxiliary.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        foreach (var directory in directories)
        {
            var candidate = Path.Combine(directory, libraryName);
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
                return handle;
        }

        // Not ours to find: fall back to the runtime's default probing.
        return IntPtr.Zero;
    }
}
