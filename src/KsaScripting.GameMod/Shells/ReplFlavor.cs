namespace KsaScripting.GameMod.Shells;

/// <summary>
///     What differs between the Lua and the TypeScript REPL: the runtime it evaluates in, the registered shell
///     id, the prompt, the script file extension and how one input line is wrapped into a module that exports
///     <c>__repl</c>.
/// </summary>
/// <param name="RuntimeId">The runtime id (<c>lua</c> / <c>v8</c>).</param>
/// <param name="ShellId">The custom shell id registered with purrTTY.</param>
/// <param name="DisplayName">The shell's display name.</param>
/// <param name="Prompt">The input prompt.</param>
/// <param name="Extension">The extension of the virtual REPL script files (with the dot).</param>
internal sealed record ReplFlavor(string RuntimeId, string ShellId, string DisplayName, string Prompt, string Extension)
{
    /// <summary>The LuaJIT REPL.</summary>
    public static ReplFlavor Lua { get; } = new("lua", "ksa-lua-repl", "ksa-scripting Lua REPL", "lua> ", ".lua");

    /// <summary>The TypeScript (V8) REPL.</summary>
    public static ReplFlavor TypeScript { get; } = new("v8", "ksa-ts-repl", "ksa-scripting TypeScript REPL", "ts> ", ".ts");

    /// <summary>The flavor for a runtime id, or <see langword="null"/>.</summary>
    /// <param name="runtimeId">The runtime id.</param>
    /// <returns>The flavor, or <see langword="null"/>.</returns>
    public static ReplFlavor? ForRuntime(string runtimeId) => runtimeId switch
    {
        "lua" => Lua,
        "v8" => TypeScript,
        _ => null,
    };

    /// <summary>
    ///     The module sources to try for one input line, in order: the expression form first (its value is the
    ///     result), the statement form when the first does not compile. The line sits on its own source line so a
    ///     trailing comment cannot swallow the closing syntax.
    /// </summary>
    /// <param name="line">The input line.</param>
    /// <returns>The candidate module sources.</returns>
    public string[] Candidates(string line) => RuntimeId == "lua"
        ?
        [
            "return { __repl = function(ctx)\nreturn " + line + "\nend }\n",
            "return { __repl = function(ctx)\n" + line + "\nend }\n",
        ]
        :
        [
            "export function __repl(ctx) {\n  return (\n" + line + "\n  );\n}\n",
            "export async function __repl(ctx) {\n" + line + "\n}\n",
        ];
}
