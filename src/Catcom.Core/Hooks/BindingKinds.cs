namespace Catcom.Core.Hooks;

/// <summary>Text conversions for <see cref="BindingKind"/> (the form used in <c>bindings.toml</c> and the host facade).</summary>
public static class BindingKinds
{
    /// <summary>Converts a kind to its lowercase text form.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns><c>event</c>, <c>tick</c>, <c>service</c> or <c>command</c>.</returns>
    public static string ToText(BindingKind kind) => kind switch
    {
        BindingKind.Event => "event",
        BindingKind.Tick => "tick",
        BindingKind.Service => "service",
        BindingKind.Command => "command",
        _ => kind.ToString().ToLowerInvariant(),
    };

    /// <summary>Parses the text form of a kind, ignoring case.</summary>
    /// <param name="text">The text, e.g. <c>"Event"</c>.</param>
    /// <param name="kind">The parsed kind when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="text"/> names a kind.</returns>
    public static bool TryParse(string? text, out BindingKind kind)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "event":
                kind = BindingKind.Event;
                return true;
            case "tick":
                kind = BindingKind.Tick;
                return true;
            case "service":
                kind = BindingKind.Service;
                return true;
            case "command":
                kind = BindingKind.Command;
                return true;
            default:
                kind = BindingKind.Event;
                return false;
        }
    }
}
