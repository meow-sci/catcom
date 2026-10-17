using Catcom.Core.Runtime;
using Microsoft.ClearScript;
using Microsoft.ClearScript.JavaScript;

namespace Catcom.Runtime.V8;

/// <summary>
///     Serves ES modules to one script engine: the <c>ksa</c> prelude, and <c>.ts</c> / <c>.js</c> / <c>.mjs</c> files under the
///     scripts folder (TypeScript is transpiled on load). The loader itself enforces containment — nothing outside the
///     scripts folder is ever read, whatever ClearScript's <c>AccessFlags</c> say. One instance per engine; it caches one
///     <see cref="Document"/> per file so every importer shares ONE module instance.
/// </summary>
/// <remarks>
///     Paths are compared as spelled ON DISK (see <see cref="PathCanonicalizer"/>), never by an OS-wide case rule: on a
///     case-folding volume <c>../SCRIPTS/x.ts</c> is the scripts folder and shares its module instance with <c>./x.ts</c>; on
///     a case-sensitive volume (Linux, or an APFS volume formatted that way) <c>../Scripts/x.ts</c> is a different folder
///     and is refused.
/// </remarks>
internal sealed class TsDocumentLoader : DocumentLoader
{
    /// <summary>The specifier of the prelude module.</summary>
    public const string PreludeSpecifier = "ksa";

    private readonly object _gate = new();
    private readonly PathCanonicalizer _paths = new();
    private readonly string _scriptsDir;
    private readonly string _realScriptsDir;
    private readonly TypeScriptCompiler _compiler;
    private readonly string _preludeCode;
    private readonly Dictionary<string, (string Code, bool IsTypeScript)> _overrides = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Document> _documents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LoadedFile> _files = new(StringComparer.Ordinal);
    private Document? _prelude;

    /// <summary>Creates a loader.</summary>
    /// <param name="scriptsDir">The scripts directory (already absolute); the only place modules may be loaded from.</param>
    /// <param name="compiler">The TypeScript compiler.</param>
    /// <param name="preludeCode">The source of the <c>ksa</c> prelude module.</param>
    public TsDocumentLoader(string scriptsDir, TypeScriptCompiler compiler, string preludeCode)
    {
        var configured = TrimSeparator(Path.GetFullPath(scriptsDir));
        _paths.Pin(configured);
        _scriptsDir = TrimSeparator(_paths.CaseExact(configured));
        _realScriptsDir = TrimSeparator(_paths.Real(_scriptsDir));
        _compiler = compiler;
        _preludeCode = preludeCode;
    }

    /// <summary>The scripts directory (absolute, no trailing separator).</summary>
    public string ScriptsDir => _scriptsDir;

    /// <summary>The most recent exception thrown from <see cref="LoadDocumentAsync"/> (ClearScript wraps it in a script error).</summary>
    public Exception? LastError { get; set; }

    /// <summary>Registers the entry module's source: served from memory (never from disk) and exempt from containment.</summary>
    /// <param name="fullPath">The entry's absolute path.</param>
    /// <param name="code">The entry's source text.</param>
    /// <param name="isTypeScript">Whether the code is TypeScript (otherwise plain JavaScript).</param>
    public void AddOverride(string fullPath, string code, bool isTypeScript)
    {
        var key = _paths.CaseExact(Path.GetFullPath(fullPath));
        lock (_gate)
            _overrides[key] = (code, isTypeScript);
    }

    /// <summary>Looks up a served file by the file name V8 reports in stack frames (absolute path or file URI).</summary>
    /// <param name="fileName">The script name from a stack frame.</param>
    /// <param name="file">The file, when known.</param>
    /// <returns><c>true</c> when the loader served that file.</returns>
    public bool TryGetFile(string fileName, out LoadedFile file)
    {
        lock (_gate)
        {
            if (_files.TryGetValue(fileName, out file!))
                return true;

            if (Uri.TryCreate(fileName, UriKind.Absolute, out var uri) && uri.IsFile
                && _files.TryGetValue(uri.LocalPath, out file!))
                return true;
        }

        file = null!;
        return false;
    }

    /// <summary>The files served so far (for diagnostics and tests).</summary>
    public IReadOnlyCollection<LoadedFile> Files
    {
        get
        {
            lock (_gate)
                return _files.Values.ToArray();
        }
    }

    /// <inheritdoc />
    public override Task<Document> LoadDocumentAsync(
        DocumentSettings settings,
        DocumentInfo? sourceInfo,
        string specifier,
        DocumentCategory category,
        DocumentContextCallback contextCallback)
    {
        try
        {
            return Task.FromResult(Load(sourceInfo, specifier, contextCallback));
        }
        catch (Exception ex)
        {
            LastError = ex;
            throw;
        }
    }

    private Document Load(DocumentInfo? sourceInfo, string specifier, DocumentContextCallback contextCallback)
    {
        if (string.Equals(specifier, PreludeSpecifier, StringComparison.Ordinal))
            return GetPrelude(contextCallback);

        var fullPath = Resolve(sourceInfo, specifier);
        lock (_gate)
        {
            if (_documents.TryGetValue(fullPath, out var cached))
                return cached;
        }

        var document = ReadDocument(fullPath, contextCallback);
        lock (_gate)
        {
            // Two importers racing for the same file must still share one document.
            if (_documents.TryGetValue(fullPath, out var existing))
                return existing;

            _documents[fullPath] = document;
            return document;
        }
    }

    private Document GetPrelude(DocumentContextCallback contextCallback)
    {
        lock (_gate)
        {
            // Normally ClearScript serves "ksa" from the registered system document and never reaches this loader;
            // either way there is exactly one prelude document per engine.
            return _prelude ??= new StringDocument(
                new DocumentInfo(PreludeSpecifier) { Category = ModuleCategory.Standard, ContextCallback = contextCallback },
                _preludeCode);
        }
    }

    private Document ReadDocument(string fullPath, DocumentContextCallback contextCallback)
    {
        string source;
        bool isTypeScript;
        bool overridden;
        lock (_gate)
        {
            overridden = _overrides.TryGetValue(fullPath, out var entry);
            (source, isTypeScript) = overridden ? entry : ("", false);
        }

        if (!overridden)
        {
            var extension = Path.GetExtension(fullPath);
            if (extension.Equals(".ts", StringComparison.OrdinalIgnoreCase))
                isTypeScript = true;
            else if (extension.Equals(".js", StringComparison.OrdinalIgnoreCase) || extension.Equals(".mjs", StringComparison.OrdinalIgnoreCase))
                isTypeScript = false;
            else
                throw new ScriptLoadException($"unsupported module type '{extension}' (only .ts, .js and .mjs can be imported): {fullPath}", fullPath);

            source = File.ReadAllText(fullPath);
        }

        string code;
        SourceMap? map = null;
        if (isTypeScript)
        {
            var result = _compiler.TranspileOrThrow(source, fullPath);
            code = result.Code;
            map = SourceMap.TryParse(result.SourceMapJson);
        }
        else
        {
            code = source;
        }

        var file = new LoadedFile(fullPath, source, map);
        var uri = new Uri(fullPath);
        lock (_gate)
        {
            // V8 reports the document's local path (or its URI) in stack frames; accept every spelling.
            _files[fullPath] = file;
            _files[uri.LocalPath] = file;
            _files[uri.AbsoluteUri] = file;
        }

        return new StringDocument(
            new DocumentInfo(uri) { Category = ModuleCategory.Standard, ContextCallback = contextCallback },
            code);
    }

    private string Resolve(DocumentInfo? sourceInfo, string specifier)
    {
        var requested = ToRequestedPath(sourceInfo, specifier);
        foreach (var candidate in Candidates(requested))
        {
            // The spelling the file system lists: one module per file however an import writes the path.
            var canonical = _paths.CaseExact(candidate);
            if (!Exists(canonical, out var overridden))
                continue;

            if (!overridden)
                EnsureInsideScripts(canonical, specifier);
            return canonical;
        }

        // Nothing exists: still refuse paths outside the folder with the containment error rather than a probe result.
        EnsureInsideScripts(_paths.CaseExact(requested), specifier);
        throw new FileNotFoundException($"cannot find module '{specifier}' (looked for {requested}, {requested}.ts, {Path.Combine(requested, "index.ts")})", requested);
    }

    private string ToRequestedPath(DocumentInfo? sourceInfo, string specifier)
    {
        if (specifier.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(specifier, UriKind.Absolute, out var uri) || !uri.IsFile)
                throw new FileNotFoundException($"invalid module URI '{specifier}'");

            return Path.GetFullPath(uri.LocalPath);
        }

        if (specifier.StartsWith('.'))
        {
            var baseDirectory = sourceInfo?.Uri is { IsFile: true } importer
                ? Path.GetDirectoryName(importer.LocalPath) ?? _scriptsDir
                : _scriptsDir;
            return Path.GetFullPath(Path.Combine(baseDirectory, specifier));
        }

        if (Path.IsPathRooted(specifier))
            return Path.GetFullPath(specifier);

        throw new ScriptLoadException(
            $"bare module specifier '{specifier}' is not supported (only \"ksa\" and relative paths)",
            sourceInfo?.Uri is { IsFile: true } from ? from.LocalPath : "");
    }

    private static IEnumerable<string> Candidates(string requested)
    {
        yield return requested;
        yield return requested + ".ts";
        yield return Path.Combine(requested, "index.ts");
        yield return requested + ".js";
        yield return Path.Combine(requested, "index.js");
    }

    private bool Exists(string candidate, out bool overridden)
    {
        lock (_gate)
            overridden = _overrides.ContainsKey(candidate);

        return overridden || File.Exists(candidate);
    }

    private void EnsureInsideScripts(string fullPath, string specifier)
    {
        // Real path (links followed, on-disk spelling) against the real scripts folder: covers ../, symlinked files and
        // directories, and differently-cased spellings alike, whatever the file system's case rule.
        if (!IsInside(_realScriptsDir, _paths.Real(fullPath)))
            throw new UnauthorizedAccessException($"module path escapes the scripts folder: {specifier}");
    }

    private static bool IsInside(string directory, string path)
        => path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private static string TrimSeparator(string path)
    {
        var root = Path.GetPathRoot(path) ?? "";
        return path.Length > root.Length ? path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : path;
    }
}
