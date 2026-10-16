using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KsaScripting.Core.Runtime;
using Microsoft.ClearScript;
using Microsoft.ClearScript.V8;

namespace KsaScripting.Runtime.V8;

/// <summary>
///     Transpiles TypeScript to ES2022 modules inside a private V8 runtime/engine ("ksa-ts-compiler"), so the ~30 MiB
///     compiler heap never counts against the script heap limit. Uses <c>typescript.js</c> (<c>ts.transpileModule</c>) with a
///     persisted V8 code cache, and falls back to the much smaller sucrase bundle when the compiler is absent or fails to
///     load. Results are cached by (file name, SHA-256 of the source) so reloads are cheap.
/// </summary>
internal sealed class TypeScriptCompiler : IDisposable
{
    private const int MaxCachedResults = 256;

    private const string TypeScriptHelper = """
        (function () {
          return function (src, fileName) {
            const r = ts.transpileModule(src, {
              fileName: fileName,
              reportDiagnostics: true,
              compilerOptions: {
                module: ts.ModuleKind.ESNext,
                target: ts.ScriptTarget.ES2022,
                isolatedModules: true,
                inlineSourceMap: true,
                inlineSources: true
              }
            });
            const diagnostics = [];
            const found = r.diagnostics || [];
            for (let i = 0; i < found.length; i++) {
              const d = found[i];
              let line = 0, column = 0;
              if (d.file && d.start !== undefined) {
                const p = d.file.getLineAndCharacterOfPosition(d.start);
                line = p.line + 1;
                column = p.character + 1;
              }
              diagnostics.push({
                message: ts.flattenDiagnosticMessageText(d.messageText, "\n"),
                line: line,
                column: column,
                category: d.category
              });
            }
            return JSON.stringify({ code: r.outputText, diagnostics: diagnostics });
          };
        })()
        """;

    private const string SucraseHelper = """
        (function () {
          return function (src, fileName) {
            try {
              const r = sucrase.transform(src, {
                transforms: ['typescript'],
                disableESTransforms: true,
                keepUnusedImports: true,
                filePath: fileName,
                sourceMapOptions: { compiledFilename: fileName }
              });
              return JSON.stringify({ code: r.code, map: r.sourceMap ? JSON.stringify(r.sourceMap) : null, diagnostics: [] });
            } catch (e) {
              const text = String(e && e.message !== undefined ? e.message : e);
              const m = /\((\d+):(\d+)\)\s*$/.exec(text);
              return JSON.stringify({
                code: "",
                diagnostics: [{
                  message: text.replace(/\s*\(\d+:\d+\)\s*$/, ""),
                  line: m ? +m[1] : 0,
                  column: m ? +m[2] + 1 : 0,
                  category: 1
                }]
              });
            }
          };
        })()
        """;

    private readonly object _gate = new();
    private readonly Dictionary<(string File, string Hash), TranspileResult> _results = new();
    private readonly Action<int, string> _log;
    private V8Runtime? _runtime;
    private V8ScriptEngine? _engine;
    private ScriptObject? _transpile;
    private bool _cacheWarningLogged;
    private bool _disposed;

    private TypeScriptCompiler(Action<int, string> log)
    {
        _log = log;
        Version = "";
    }

    /// <summary>Whether the sucrase fallback is in use instead of <c>typescript.js</c>.</summary>
    public bool UsingSucrase { get; private set; }

    /// <summary>The transpiler version: <c>ts.version</c> or <c>"sucrase"</c>.</summary>
    public string Version { get; private set; }

    /// <summary>The V8 code cache file used for <c>typescript.js</c>, or <c>null</c> when sucrase is in use.</summary>
    public string? CodeCachePath { get; private set; }

    /// <summary>
    ///     Whether the persisted V8 code cache was accepted when <c>typescript.js</c> was compiled: <c>true</c> accepted,
    ///     <c>false</c> absent/rejected (a new cache was produced), <c>null</c> when no code cache is involved.
    /// </summary>
    public bool? CodeCacheAccepted { get; private set; }

    /// <summary>The ClearScript managed assembly version used in the cache key.</summary>
    public static string ClearScriptVersion
        => typeof(V8ScriptEngine).Assembly.GetName().Version?.ToString() ?? "unknown";

    /// <summary>The cache file path for a given scripts-side stubs directory and compiler size/version key.</summary>
    /// <param name="stubsDir">The runtime options' stubs directory.</param>
    /// <param name="versionKey">The TypeScript version or file size.</param>
    /// <returns>The absolute cache file path.</returns>
    public static string CacheFilePath(string stubsDir, string versionKey)
        => Path.GetFullPath(Path.Combine(stubsDir, "..", "cache", $"typescript-{versionKey}-{ClearScriptVersion}.v8cache"));

    /// <summary>Creates the compiler: loads <c>typescript.js</c> (code cached) or the sucrase fallback.</summary>
    /// <param name="stubsDir">The stubs directory the code-cache path is derived from.</param>
    /// <param name="log">The host log (level 0 debug … 3 error).</param>
    /// <returns>A ready compiler.</returns>
    /// <exception cref="InvalidOperationException">Neither transpiler bundle could be loaded.</exception>
    public static TypeScriptCompiler Create(string stubsDir, Action<int, string> log)
    {
        var compiler = new TypeScriptCompiler(log);
        try
        {
            compiler.Load(stubsDir);
            return compiler;
        }
        catch
        {
            compiler.Dispose();
            throw;
        }
    }

    /// <summary>Transpiles a file. Never throws for syntax errors (they are returned as diagnostics).</summary>
    /// <param name="source">The TypeScript source.</param>
    /// <param name="fileName">The file name (its extension selects the script kind).</param>
    /// <returns>The generated code, source map and diagnostics.</returns>
    public TranspileResult Transpile(string source, string fileName)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(fileName);

        var key = (fileName, Hash(source));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_results.TryGetValue(key, out var cached))
                return cached;

            var result = TranspileCore(source, fileName);
            if (_results.Count >= MaxCachedResults)
                _results.Clear();
            _results[key] = result;
            return result;
        }
    }

    /// <summary>Transpiles a file, throwing <see cref="ScriptLoadException"/> for the first error diagnostic.</summary>
    /// <param name="source">The TypeScript source.</param>
    /// <param name="path">The file path (reported in the exception).</param>
    /// <returns>The transpile result (no error diagnostics).</returns>
    /// <exception cref="ScriptLoadException">The source has a syntax error.</exception>
    public TranspileResult TranspileOrThrow(string source, string path)
    {
        var result = Transpile(source, path);
        foreach (var diagnostic in result.Diagnostics)
        {
            if (!diagnostic.IsError)
                continue;

            throw new ScriptLoadException(
                diagnostic.Message,
                path,
                diagnostic.Line,
                diagnostic.Column,
                SourceMap.GetLine(source, diagnostic.Line));
        }

        return result;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            DisposeEngine();
            _results.Clear();
        }
    }

    private TranspileResult TranspileCore(string source, string fileName)
    {
        var json = (string)_transpile!.InvokeAsFunction(source, fileName);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var diagnostics = new List<TranspileDiagnostic>();
        foreach (var d in root.GetProperty("diagnostics").EnumerateArray())
        {
            // ts.DiagnosticCategory: Warning = 0, Error = 1, Suggestion = 2, Message = 3.
            diagnostics.Add(new TranspileDiagnostic(
                d.GetProperty("message").GetString() ?? "",
                d.GetProperty("line").GetInt32(),
                d.GetProperty("column").GetInt32(),
                d.GetProperty("category").GetInt32() == 1));
        }

        var code = root.GetProperty("code").GetString() ?? "";
        string? mapJson;
        if (UsingSucrase)
        {
            mapJson = root.TryGetProperty("map", out var mapElement) && mapElement.ValueKind == JsonValueKind.String
                ? mapElement.GetString()
                : null;
            if (mapJson is not null)
                code = code + "\n" + SourceMap.ToInlineComment(mapJson) + "\n";
        }
        else
        {
            mapJson = SourceMap.ExtractInlineJson(code);
        }

        return new TranspileResult(code, mapJson, diagnostics);
    }

    private void Load(string stubsDir)
    {
        var typeScriptPath = V8Assets.TypeScriptPath;
        var sucrasePath = V8Assets.SucrasePath;

        if (File.Exists(typeScriptPath))
        {
            try
            {
                CreateEngine();
                LoadTypeScript(typeScriptPath, stubsDir);
                return;
            }
            catch (Exception ex) when (File.Exists(sucrasePath))
            {
                _log(2, $"typescript.js failed to load ({ex.GetBaseException().Message}); falling back to sucrase");
                DisposeEngine();
                CodeCachePath = null;
                CodeCacheAccepted = null;
            }
        }

        if (!File.Exists(sucrasePath))
        {
            throw new InvalidOperationException(
                $"TypeScript transpiler assets missing (run tools/fetch-ts.sh): neither {typeScriptPath} nor {sucrasePath} could be loaded");
        }

        CreateEngine();
        LoadSucrase(sucrasePath);
    }

    private void CreateEngine()
    {
        // Default constraints on purpose: the compiler has its own isolate and heap.
        _runtime = new V8Runtime("ksa-ts-compiler");
        _engine = _runtime.CreateScriptEngine("ksa-ts-compiler");
    }

    private void DisposeEngine()
    {
        _transpile?.Dispose();
        _transpile = null;
        _engine?.Dispose();
        _engine = null;
        _runtime?.Dispose();
        _runtime = null;
    }

    private void LoadTypeScript(string path, string stubsDir)
    {
        var engine = _engine!;
        var source = File.ReadAllText(path);
        var info = new DocumentInfo("typescript.js");
        var versionKey = new FileInfo(path).Length.ToString(CultureInfo.InvariantCulture);
        var cachePath = CacheFilePath(stubsDir, versionKey);
        CodeCachePath = cachePath;

        V8Script script;
        var cacheBytes = TryReadCache(cachePath);
        if (cacheBytes is not null)
        {
            script = engine.Compile(info, source, V8CacheKind.Code, cacheBytes, out var accepted);
            CodeCacheAccepted = accepted;
            if (!accepted)
            {
                // Stale (different V8 build or compiler): regenerate and replace.
                script = engine.Compile(info, source, V8CacheKind.Code, out byte[] fresh);
                TryWriteCache(cachePath, fresh);
            }
        }
        else
        {
            script = engine.Compile(info, source, V8CacheKind.Code, out byte[] fresh);
            CodeCacheAccepted = false;
            TryWriteCache(cachePath, fresh);
        }

        using (script)
            engine.Execute(script);
        Version = Convert.ToString(engine.Evaluate("ts.version"), CultureInfo.InvariantCulture) ?? "unknown";
        _transpile = (ScriptObject)engine.Evaluate(new DocumentInfo("ksa-ts-helper.js"), TypeScriptHelper);
        UsingSucrase = false;
    }

    private void LoadSucrase(string path)
    {
        var engine = _engine!;
        engine.Execute(new DocumentInfo("sucrase.bundle.js"), File.ReadAllText(path));
        _transpile = (ScriptObject)engine.Evaluate(new DocumentInfo("ksa-sucrase-helper.js"), SucraseHelper);
        UsingSucrase = true;
        Version = "sucrase";
        CodeCachePath = null;
        CodeCacheAccepted = null;
    }

    private byte[]? TryReadCache(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            WarnCacheOnce($"cannot read TypeScript code cache {path}: {ex.Message}");
            return null;
        }
    }

    private void TryWriteCache(string path, byte[] bytes)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            // Atomic replace: concurrent readers see the old or the new file, never a partial one.
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, bytes);
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            WarnCacheOnce($"cannot write TypeScript code cache {path}: {ex.Message}");
        }
    }

    private void WarnCacheOnce(string message)
    {
        if (_cacheWarningLogged)
            return;

        _cacheWarningLogged = true;
        _log(2, message);
    }

    private static string Hash(string source)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
}
