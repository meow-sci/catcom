using Catcom.Core.Config;
using Catcom.Core.Logging;

namespace Catcom.Core.Api;

/// <summary>
///     Writes the generated editor files into the scripts folder on every mod start, so the editor's
///     language servers pick them up with no user action: <c>stubs/host.d.ts</c>, <c>stubs/ksa.lua</c>,
///     <c>tsconfig.json</c> and <c>.luarc.json</c>.
/// </summary>
public static class StubWriter
{
    /// <summary>
    ///     Writes the four generated files into <paramref name="scriptsDir"/> (atomically), skipping any
    ///     file whose content is already identical so editors do not see spurious changes. Never throws:
    ///     a failure is logged and the remaining files are still attempted.
    /// </summary>
    /// <param name="scriptsDir">The scripts folder.</param>
    /// <returns>The paths that were written (empty when everything was already up to date).</returns>
    public static IReadOnlyList<string> WriteAll(string scriptsDir)
    {
        var written = new List<string>();
        try
        {
            (string Relative, string Content)[] files =
            [
                (Path.Combine("stubs", "host.d.ts"), StubGenerators.TypeScriptDeclarations(ApiSchema.Classes)),
                (Path.Combine("stubs", "ksa.lua"), StubGenerators.LuaCats(ApiSchema.Classes)),
                ("tsconfig.json", StubGenerators.TsConfigJson()),
                (".luarc.json", StubGenerators.LuaRcJson()),
            ];

            foreach (var (relative, content) in files)
            {
                var path = Path.Combine(scriptsDir, relative);
                try
                {
                    if (File.Exists(path) && string.Equals(File.ReadAllText(path), content, StringComparison.Ordinal))
                        continue;

                    AtomicFile.WriteAllText(path, content);
                    written.Add(path);
                }
                catch (Exception e)
                {
                    Log.Warn($"could not write editor stub '{path}': {e.Message}");
                }
            }
        }
        catch (Exception e)
        {
            Log.Warn($"could not generate editor stubs: {e.Message}");
        }

        return written;
    }
}
