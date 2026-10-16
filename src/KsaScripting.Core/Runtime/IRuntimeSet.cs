namespace KsaScripting.Core.Runtime;

/// <summary>The set of registered script runtimes, with lookup by id, language and file path.</summary>
public interface IRuntimeSet
{
    /// <summary>Every registered runtime, in registration order (available or not).</summary>
    IReadOnlyList<IScriptRuntime> All { get; }

    /// <summary>Finds a runtime by <see cref="IScriptRuntime.Id"/> (ordinal, ignoring case).</summary>
    /// <param name="id">The runtime id.</param>
    /// <returns>The runtime, or <c>null</c> when none is registered.</returns>
    IScriptRuntime? ForId(string id);

    /// <summary>Finds a runtime by <see cref="IScriptRuntime.Language"/> (ordinal, ignoring case).</summary>
    /// <param name="language">The language name.</param>
    /// <returns>The runtime, or <c>null</c> when none is registered.</returns>
    IScriptRuntime? ForLanguage(string language);

    /// <summary>Finds the runtime for a script file by its extension.</summary>
    /// <param name="path">The file path.</param>
    /// <returns>The runtime, or <c>null</c> when the extension is unknown or no runtime handles it.</returns>
    IScriptRuntime? ForPath(string path);
}
