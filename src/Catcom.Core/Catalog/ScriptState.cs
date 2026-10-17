namespace Catcom.Core.Catalog;

/// <summary>Where a script is in its lifecycle.</summary>
public enum ScriptState
{
    /// <summary>Known on disk, not loaded.</summary>
    Idle,

    /// <summary>Being read, compiled and evaluated.</summary>
    Loading,

    /// <summary>Loaded: its code ran and its hooks and bindings are active.</summary>
    Running,

    /// <summary>Loaded and running its <c>main(ctx)</c> as a service.</summary>
    Service,

    /// <summary>The last load or start failed (see <see cref="ScriptInfo.LastError"/>).</summary>
    Error,

    /// <summary>Latched off by the scheduler (fault limit or watchdog) until reloaded.</summary>
    Disabled,
}
