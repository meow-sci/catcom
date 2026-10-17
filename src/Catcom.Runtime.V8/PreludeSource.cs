using System.Reflection;

namespace Catcom.Runtime.V8;

/// <summary>The embedded source of the <c>ksa</c> prelude module (the real prelude from Core, or the placeholder).</summary>
internal static class PreludeSource
{
    /// <summary>The logical name of the embedded resource.</summary>
    public const string ResourceName = "Catcom.Runtime.V8.ksa.js";

    private static readonly Lazy<string> Source = new(Read, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The prelude text.</summary>
    public static string Text => Source.Value;

    private static string Read()
    {
        using var stream = typeof(PreludeSource).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"embedded resource '{ResourceName}' (the ksa prelude) is missing");
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
