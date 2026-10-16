using System.Text;

namespace KsaScripting.Core.Api;

/// <summary>Emits <c>stubs/ksa.lua</c> (LuaCATS) from the schema.</summary>
internal static class LuaCatsEmitter
{
    private const string Language = "lua";
    private const string ModuleClass = "ksa";
    private const string EventsClass = "Events";

    internal static string Emit(IReadOnlyList<ApiClass> classes)
    {
        var builder = new StringBuilder();
        DocText.Line(builder, "---@meta ksa");
        DocText.Line(builder, "-- " + StubGenerators.GeneratedNotice);
        DocText.Line(builder, "");

        var module = classes.FirstOrDefault(c => c.Name == ModuleClass);
        if (module is not null)
            EmitClass(builder, module);

        var events = classes.FirstOrDefault(c => c.Name == EventsClass);
        if (events is not null)
            EmitEventAlias(builder, events);

        foreach (var cls in classes)
        {
            if (cls.Name is ModuleClass or EventsClass)
                continue;
            EmitClass(builder, cls);
        }

        DocText.Line(builder, "return ksa");
        return builder.ToString();
    }

    private static void EmitEventAlias(StringBuilder builder, ApiClass events)
    {
        DocText.Line(builder, "---Every event name `ksa.on` and `ksa.wait_event` know (any other string is accepted too).");
        DocText.Line(builder, "---Payload field names are snake_case in Lua.");
        DocText.Line(builder, "---@alias ksa.EventName");
        foreach (var member in events.Members.Where(m => m.Kind == ApiKind.Event))
        {
            var payload = member.Params.Count == 0
                ? ""
                : " Payload: " + string.Join(", ", member.Params.Select(p => ApiNames.ToSnake(p.Name))) + ".";
            DocText.Line(builder, $"---| \"{member.Name}\" # {OneLine(member.Doc)}{payload}");
        }

        DocText.Line(builder, "");
    }

    private static void EmitClass(StringBuilder builder, ApiClass cls)
    {
        var isModule = cls.Name == ModuleClass;
        var className = isModule ? ModuleClass : "ksa." + cls.Name;
        var members = cls.Members.Where(m => DocText.InLanguage(m, Language)).ToList();
        var properties = members.Where(m => m.Kind == ApiKind.Property).ToList();
        var methods = members.Where(m => m.Kind == ApiKind.Method).ToList();

        foreach (var line in DocText.Lines(DocText.ForLua(cls.Doc)))
            DocText.Line(builder, "---" + line);
        DocText.Line(builder, "---@class " + className + (cls.Extends is null ? "" : " : ksa." + cls.Extends));
        foreach (var property in properties)
            DocText.Line(builder, $"---@field {LuaNames.Member(property.Name)} {property.Type.LuaType} {OneLine(property.Doc)}".TrimEnd());

        if (methods.Count == 0 && !isModule)
        {
            DocText.Line(builder, "");
            return;
        }

        var table = isModule ? ModuleClass : cls.Name;
        DocText.Line(builder, $"local {table} = {{}}");
        DocText.Line(builder, "");

        var separator = LuaNames.UsesDotSyntax(cls.Name) ? "." : ":";
        foreach (var method in methods)
        {
            EmitMethod(builder, method);
            var parameters = string.Join(", ", method.Params.Select(p => p.Name.StartsWith("...", StringComparison.Ordinal) ? "..." : ApiNames.ToSnake(p.Name)));
            DocText.Line(builder, $"function {table}{separator}{LuaNames.Member(method.Name)}({parameters}) end");
            DocText.Line(builder, "");
        }
    }

    private static void EmitMethod(StringBuilder builder, ApiMember method)
    {
        foreach (var line in DocText.Lines(DocText.ForLua(method.Doc)))
            DocText.Line(builder, "---" + line);
        if (DocText.ActionLine(method) is { } action)
            DocText.Line(builder, "---" + action);
        if (method.Async)
            DocText.Line(builder, "---@async");
        foreach (var p in method.Params)
        {
            var variadic = p.Name.StartsWith("...", StringComparison.Ordinal);
            var name = variadic ? "..." : ApiNames.ToSnake(p.Name);
            var type = variadic && p.Type.LuaType.EndsWith("[]", StringComparison.Ordinal)
                ? p.Type.LuaType[..^2]
                : p.Type.LuaType;
            DocText.Line(builder, $"---@param {name}{(p.Optional ? "?" : "")} {type} {OneLine(p.Doc)}".TrimEnd());
        }

        if (method.Type.TsType != "void")
            DocText.Line(builder, $"---@return {method.Type.LuaType}");
    }

    private static string OneLine(string text) => string.Join(' ', DocText.Lines(DocText.ForLua(text)));
}
