// KsaProbe: inspection-only probe of the KSA reference assemblies via MetadataLoadContext.
// Usage: dotnet run --project spikes/KsaProbe -- <dllDir> <query>...
//   query forms:  Type                      -> list public+nonpublic members of the type
//                 Type::Member              -> show matching members (methods/fields/props/events)
//                 grep:<substring>          -> list types whose full name contains substring
//                 events                    -> list all event/delegate-typed fields in KSA.dll
using System.Reflection;
using System.Runtime.InteropServices;

var dllDir = args.Length > 0 ? args[0] : "../../../ksa-game-assemblies/current/dll";
var queries = args.Skip(1).ToArray();
var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
var paths = Directory.GetFiles(runtimeDir, "*.dll").Concat(Directory.GetFiles(dllDir, "*.dll")).ToList();
var resolver = new PathAssemblyResolver(paths);
using var mlc = new MetadataLoadContext(resolver, coreAssemblyName: "System.Private.CoreLib");
var ksa = mlc.LoadFromAssemblyPath(Path.GetFullPath(Path.Combine(dllDir, "KSA.dll")));
Console.WriteLine($"KSA.dll {ksa.GetName().Version}  types={SafeTypes(ksa).Count()}");

const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

static IEnumerable<Type> SafeTypes(Assembly a)
{
    try { return a.GetTypes(); }
    catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null)!; }
}

string Sig(MemberInfo m) => m switch
{
    MethodInfo mi => $"{(mi.IsStatic ? "static " : "")}{(mi.IsPublic ? "public" : mi.IsPrivate ? "private" : "internal/protected")} {mi.ReturnType.Name} {mi.Name}({string.Join(", ", mi.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})",
    FieldInfo fi => $"{(fi.IsStatic ? "static " : "")}{(fi.IsPublic ? "public" : fi.IsPrivate ? "private" : "internal/protected")} field {fi.FieldType.Name} {fi.Name}",
    PropertyInfo pi => $"property {pi.PropertyType.Name} {pi.Name} {{ {(pi.GetMethod != null ? (pi.GetMethod.IsPublic ? "get;" : "nonpublic get;") : "")} {(pi.SetMethod != null ? (pi.SetMethod.IsPublic ? "set;" : "nonpublic set;") : "")} }}",
    EventInfo ei => $"event {ei.EventHandlerType?.Name} {ei.Name}",
    ConstructorInfo ci => $"ctor({string.Join(", ", ci.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})",
    _ => m.ToString() ?? m.Name
};

foreach (var q in queries)
{
    Console.WriteLine($"\n=== {q}");
    if (q == "events")
    {
        foreach (var t in SafeTypes(ksa))
        {
            foreach (var e in t.GetEvents(All)) Console.WriteLine($"{t.FullName}: {Sig(e)}");
            foreach (var f in t.GetFields(All))
                if (f.FieldType.FullName is { } fn && (fn.StartsWith("System.Action") || fn.StartsWith("System.Func") || fn.Contains("Delegate") || fn.Contains("EventHandler")))
                    Console.WriteLine($"{t.FullName}: {Sig(f)}");
        }
        continue;
    }
    if (q.StartsWith("grep:"))
    {
        var s = q[5..];
        foreach (var t in SafeTypes(ksa).Where(t => t.FullName!.Contains(s, StringComparison.OrdinalIgnoreCase)))
            Console.WriteLine($"{t.FullName}  ({(t.IsPublic || t.IsNestedPublic ? "public" : "nonpublic")}{(t.IsAbstract && t.IsSealed ? ", static" : "")})");
        continue;
    }
    var parts = q.Split("::", 2);
    var type = SafeTypes(ksa).FirstOrDefault(t => t.FullName == parts[0] || t.Name == parts[0] || t.FullName == "KSA." + parts[0]);
    if (type == null) { Console.WriteLine("  TYPE NOT FOUND"); continue; }
    Console.WriteLine($"  type {type.FullName} : {type.BaseType?.FullName}  ({(type.IsPublic ? "public" : "nonpublic")})");
    var members = parts.Length == 1 ? type.GetMembers(All) : type.GetMember(parts[1], All);
    if (members.Length == 0) Console.WriteLine("  MEMBER NOT FOUND");
    foreach (var m in members.OrderBy(m => m.MemberType).ThenBy(m => m.Name))
        Console.WriteLine($"  {SafeSig(m)}");
}

// A member whose signature names a type from an assembly missing from dllDir (e.g. MathNet) can't be decoded.
string SafeSig(MemberInfo m)
{
    try { return Sig(m); }
    catch (FileNotFoundException e) { return $"{m.MemberType} {m.Name} <unresolved: {e.Message.Split('\'')[1]}>"; }
}
