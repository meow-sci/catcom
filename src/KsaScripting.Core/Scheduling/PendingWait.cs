using KsaScripting.Core.Hosting;
using KsaScripting.Core.Values;

namespace KsaScripting.Core.Scheduling;

/// <summary>The scheduler's bookkeeping for one unresolved <see cref="HostWait"/>.</summary>
internal sealed class PendingWait(HostWait wait, string kind, ScriptValue arg, string script)
{
    /// <summary>The wait handed to the runtime.</summary>
    public HostWait Wait { get; } = wait;

    /// <summary>The wait kind (<c>sleep</c>, <c>nextStep</c>, ...).</summary>
    public string Kind { get; } = kind;

    /// <summary>The argument the wait was created with.</summary>
    public ScriptValue Arg { get; } = arg;

    /// <summary>The script that created the wait.</summary>
    public string Script { get; } = script;

    /// <summary>The slot index in effect when the wait was created; it can resolve in a later slot only.</summary>
    public long CreatedSlot { get; set; }

    /// <summary>The last event sequence number at creation; <c>waitEvent</c> matches only newer events.</summary>
    public long CreatedSeq { get; set; }

    /// <summary>The clock (<c>ut</c>, <c>wall</c>, <c>render</c>) that <see cref="Seconds"/> and <see cref="TimeoutSeconds"/> are measured on.</summary>
    public string Clock { get; set; } = "ut";

    /// <summary>The clock reading at creation (valid when <see cref="HasBase"/>).</summary>
    public double Base { get; set; }

    /// <summary>Whether <see cref="Base"/> has been captured (a wait created before the first slot captures it in that slot).</summary>
    public bool HasBase { get; set; }

    /// <summary><c>sleep</c>: how long to sleep.</summary>
    public double Seconds { get; set; }

    /// <summary><c>until</c> / <c>waitEvent</c>: the optional timeout.</summary>
    public double? TimeoutSeconds { get; set; }

    /// <summary><c>until</c>: the predicate (owned by the wait; disposed when it ends).</summary>
    public ScriptCallable? Fn { get; set; }

    /// <summary><c>waitEvent</c>: the event type to wait for.</summary>
    public string? EventName { get; set; }

    /// <summary><c>waitEvent</c>: the vessel filter.</summary>
    public string? Vessel { get; set; }

    /// <summary><c>termRead</c>: the terminal to read.</summary>
    public int TermId { get; set; }
}
