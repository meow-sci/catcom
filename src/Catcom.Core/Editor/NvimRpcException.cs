namespace Catcom.Core.Editor;

/// <summary>Neovim answered a request with an error.</summary>
public sealed class NvimRpcException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="error">The raw decoded error value Neovim sent, if any.</param>
    public NvimRpcException(string message, object? error = null)
        : base(message)
    {
        Error = error;
    }

    /// <summary>The raw decoded error value (Neovim sends <c>[type, message]</c>), or <c>null</c>.</summary>
    public object? Error { get; }
}
