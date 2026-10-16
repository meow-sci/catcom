using KsaScripting.Core.Config;

namespace KsaScripting.Core.Hosting;

/// <summary>The authority and diagnostics settings of the <see cref="HostFacade"/>.</summary>
/// <param name="ControlAllVessels">When <see langword="false"/>, scripts may only act on the active vessel (except <c>camera.focus</c>).</param>
/// <param name="DebugNamespace">When <see langword="false"/>, <c>debug.*</c> actions are denied.</param>
/// <param name="OutputLinesPerScript">How many output lines (log lines and errors) are kept per script for the Output tab.</param>
public sealed record HostFacadeOptions(bool ControlAllVessels = true, bool DebugNamespace = true, int OutputLinesPerScript = 500)
{
    /// <summary>Builds the options from <c>ksa-scripting.toml</c>.</summary>
    /// <param name="cfg">The loaded configuration.</param>
    /// <returns>The matching options.</returns>
    public static HostFacadeOptions FromConfig(ScriptingConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        return new HostFacadeOptions(cfg.ControlAllVessels, cfg.DebugNamespace);
    }
}
