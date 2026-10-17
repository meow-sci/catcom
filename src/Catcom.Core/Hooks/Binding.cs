using Catcom.Core.Hosting;

namespace Catcom.Core.Hooks;

/// <summary>One attachment of a script's exported function to a trigger.</summary>
/// <param name="Id">Unique id (the value returned by <c>Subscribe</c>).</param>
/// <param name="Script">The owning script's catalog name.</param>
/// <param name="Export">The exported function's name (<c>&lt;anonymous&gt;</c> for unnamed callables).</param>
/// <param name="Kind">What the binding attaches to.</param>
/// <param name="Key">
///     The trigger: an event name for <see cref="BindingKind.Event"/>; <c>frame</c>, <c>step</c> or
///     <c>every</c> for <see cref="BindingKind.Tick"/>; the command name for
///     <see cref="BindingKind.Command"/>; the export name for <see cref="BindingKind.Service"/>.
/// </param>
/// <param name="Options">Sync flag, vessel filter, tick clock and interval.</param>
/// <param name="FromCode"><see langword="true"/> when declared by the script's code, <see langword="false"/> when created from the UI.</param>
/// <param name="Enabled">Disabled bindings are kept but never delivered.</param>
public sealed record Binding(
    Guid Id,
    string Script,
    string Export,
    BindingKind Kind,
    string Key,
    BindingOptions Options,
    bool FromCode,
    bool Enabled);
