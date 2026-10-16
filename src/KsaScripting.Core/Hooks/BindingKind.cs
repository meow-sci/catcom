namespace KsaScripting.Core.Hooks;

/// <summary>What a binding attaches an exported function to.</summary>
public enum BindingKind
{
    /// <summary>A named game or host event.</summary>
    Event,

    /// <summary>A recurring tick: every frame, every landed sim step, or on an interval.</summary>
    Tick,

    /// <summary>A long-running service started once (<c>main(ctx)</c> or <c>ksa.service</c>).</summary>
    Service,

    /// <summary>A user-defined console command.</summary>
    Command,
}
