using System.Text;

namespace Catcom.Core.Api;

/// <summary>Emits <c>stubs/host.d.ts</c> from the schema.</summary>
internal static class TypeScriptEmitter
{
    private const string Language = "ts";
    private const string ModuleClass = "ksa";
    private const string EventsClass = "Events";

    internal static string Emit(IReadOnlyList<ApiClass> classes)
    {
        var builder = new StringBuilder();
        DocText.Line(builder, "// " + StubGenerators.GeneratedNotice);
        DocText.Line(builder, "");
        DocText.Line(builder, "declare namespace ksa {");

        var events = classes.FirstOrDefault(c => c.Name == EventsClass);
        var first = true;
        if (events is not null)
        {
            EmitEventName(builder, events);
            first = false;
        }

        foreach (var cls in classes)
        {
            if (!first)
                DocText.Line(builder, "");
            first = false;
            if (cls.Name == ModuleClass)
                EmitModule(builder, cls);
            else if (cls.Name == EventsClass)
                EmitEvents(builder, cls);
            else
                EmitInterface(builder, cls);
        }

        DocText.Line(builder, "}");
        DocText.Line(builder, "");
        DocText.Line(builder, "// `import { ksa } from \"ksa\"` and `import ksa from \"ksa\"` resolve to the same namespace as the global `ksa`.");
        DocText.Line(builder, "declare module \"ksa\" {");
        DocText.Line(builder, "  import _ksa = ksa;");
        DocText.Line(builder, "  export { _ksa as ksa };");
        DocText.Line(builder, "  export default _ksa;");
        DocText.Line(builder, "}");
        return builder.ToString();
    }

    private static void EmitEventName(StringBuilder builder, ApiClass events)
    {
        DocText.Line(builder, "  /**");
        DocText.Line(builder, "   * Every event name `ksa.on` and `ksa.waitEvent` know. Any other string (custom or targeted event names) is accepted too.");
        DocText.Line(builder, "   */");
        DocText.Line(builder, "  type EventName =");
        foreach (var member in events.Members.Where(m => m.Kind == ApiKind.Event))
            DocText.Line(builder, $"    | \"{member.Name}\"");
        DocText.Line(builder, "    | (string & {});");
    }

    private static void EmitEvents(StringBuilder builder, ApiClass events)
    {
        EmitDoc(builder, "  ", events.Doc, [], []);
        DocText.Line(builder, "  interface Events {");
        foreach (var member in events.Members.Where(m => m.Kind == ApiKind.Event))
        {
            EmitDoc(builder, "    ", member.Doc, [], []);
            var payload = member.Params.Count == 0
                ? "Record<string, never>"
                : "{ " + string.Join("; ", member.Params.Select(p => $"{p.Name}: {p.Type.TsType}")) + " }";
            DocText.Line(builder, $"    \"{member.Name}\": {payload};");
        }

        DocText.Line(builder, "  }");
    }

    private static void EmitModule(StringBuilder builder, ApiClass module)
    {
        foreach (var line in DocText.Wrap(DocText.ForTs(module.Doc), 96))
            DocText.Line(builder, "  // " + line);
        DocText.Line(builder, "  //");
        DocText.Line(builder, "  // The members of the ksa module itself follow.");
        DocText.Line(builder, "");
        var members = module.Members.Where(m => DocText.InLanguage(m, Language)).ToList();
        for (var i = 0; i < members.Count; i++)
        {
            var member = members[i];
            if (i > 0)
                DocText.Line(builder, "");
            EmitMember(builder, "  ", member, isModule: true);
        }
    }

    private static void EmitInterface(StringBuilder builder, ApiClass cls)
    {
        EmitDoc(builder, "  ", cls.Doc, [], []);
        var extends = cls.Extends is null ? "" : " extends " + cls.Extends;
        var members = cls.Members.Where(m => DocText.InLanguage(m, Language)).ToList();
        var indexSignature = cls.Name == "State";
        if (members.Count == 0 && !indexSignature)
        {
            DocText.Line(builder, $"  interface {cls.Name}{extends} {{}}");
            return;
        }

        DocText.Line(builder, $"  interface {cls.Name}{extends} {{");
        foreach (var member in members)
            EmitMember(builder, "    ", member, isModule: false);
        if (indexSignature)
        {
            EmitDoc(builder, "    ", "Any other key is a persisted value too: `ksa.state.counter = 1`.", [], []);
            DocText.Line(builder, "    [key: string]: unknown;");
        }

        DocText.Line(builder, "  }");
    }

    private static void EmitMember(StringBuilder builder, string indent, ApiMember member, bool isModule)
    {
        var extra = new List<string>();
        if (DocText.ActionLine(member) is { } action)
            extra.Add(action);
        var parameters = member.Kind == ApiKind.Method ? member.Params : [];
        EmitDoc(builder, indent, member.Doc, extra, parameters);

        if (member.Kind == ApiKind.Property)
        {
            DocText.Line(builder, isModule
                ? $"{indent}const {member.Name}: {member.Type.TsType};"
                : $"{indent}readonly {member.Name}: {member.Type.TsType};");
            return;
        }

        var result = member.Async ? $"Promise<{member.Type.TsType}>" : member.Type.TsType;
        var signature = $"{member.Name}({string.Join(", ", member.Params.Select(Parameter))}): {result};";
        DocText.Line(builder, isModule ? $"{indent}function {signature}" : $"{indent}{signature}");
    }

    private static string Parameter(ApiParam p)
        => p.Name.StartsWith("...", StringComparison.Ordinal)
            ? $"{p.Name}: {p.Type.TsType}"
            : $"{p.Name}{(p.Optional ? "?" : "")}: {p.Type.TsType}";

    private static void EmitDoc(StringBuilder builder, string indent, string doc, IReadOnlyList<string> extraLines, IReadOnlyList<ApiParam> parameters)
    {
        var lines = new List<string>(DocText.Lines(DocText.ForTs(doc)));
        if (extraLines.Count > 0)
        {
            lines.Add("");
            lines.AddRange(extraLines);
        }

        foreach (var p in parameters.Where(p => p.Doc.Length > 0))
            lines.Add($"@param {p.Name.TrimStart('.')} {DocText.ForTs(p.Doc)}");

        if (lines.Count == 1)
        {
            DocText.Line(builder, $"{indent}/** {Escape(lines[0])} */");
            return;
        }

        DocText.Line(builder, indent + "/**");
        foreach (var line in lines)
            DocText.Line(builder, line.Length == 0 ? indent + " *" : $"{indent} * {Escape(line)}");
        DocText.Line(builder, indent + " */");
    }

    private static string Escape(string text) => text.Replace("*/", "*\\/", StringComparison.Ordinal);
}
