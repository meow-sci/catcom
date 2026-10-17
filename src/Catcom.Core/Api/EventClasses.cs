using Catcom.Core.Hooks;

namespace Catcom.Core.Api;

/// <summary>The <c>Events</c> class: one event member per <see cref="EventCatalog"/> entry.</summary>
internal static class EventClasses
{
    internal static IReadOnlyList<ApiClass> Create() =>
    [
        new("Events",
            "The event catalog: every name `ksa.on` and `ksa.waitEvent` accept, with the fields of its payload (`ctx.payload` / `event.payload`). Payload field names are camelCase in TypeScript and snake_case in Lua. Custom and targeted event names are also accepted.",
            EventCatalog.All
                .Select(e => Dsl.Event(e.Name, e.Description, e.PayloadFields))
                .ToArray())
    ];
}
