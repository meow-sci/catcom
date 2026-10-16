namespace KsaScripting.Core.Runtime;

/// <summary>
///     The default <see cref="IRuntimeSet"/>. Returns registered runtimes even when they report
///     <see cref="IScriptRuntime.IsAvailable"/> <c>false</c>; callers check availability.
/// </summary>
public sealed class RuntimeSet : IRuntimeSet
{
    private readonly IScriptRuntime[] _runtimes;

    /// <summary>Creates a set from the given runtimes.</summary>
    /// <param name="runtimes">The runtimes to register, in order.</param>
    public RuntimeSet(IEnumerable<IScriptRuntime> runtimes)
    {
        ArgumentNullException.ThrowIfNull(runtimes);
        _runtimes = runtimes.ToArray();
    }

    /// <inheritdoc />
    public IReadOnlyList<IScriptRuntime> All => _runtimes;

    /// <inheritdoc />
    public IScriptRuntime? ForId(string id)
        => _runtimes.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc />
    public IScriptRuntime? ForLanguage(string language)
        => _runtimes.FirstOrDefault(r => string.Equals(r.Language, language, StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc />
    public IScriptRuntime? ForPath(string path)
    {
        var language = ScriptLanguages.ForPath(path);
        return language is null ? null : ForLanguage(language);
    }
}
