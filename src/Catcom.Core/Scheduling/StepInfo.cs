using Catcom.Core.Values;

namespace Catcom.Core.Scheduling;

/// <summary>What the game reports for one Step slot.</summary>
/// <param name="Ut">Universal time in seconds at the slot.</param>
/// <param name="Dt">The simulation time covered by the step.</param>
/// <param name="StepIndex">The index of the most recent landed sim step.</param>
/// <param name="Frame">The render frame number.</param>
/// <param name="StepLanded">Whether a sim step landed since the previous slot (paused frames do not).</param>
/// <param name="PlayerDt">Real (player) time of the frame; the <c>render</c> clock accumulates it.</param>
public sealed record StepInfo(double Ut, double Dt, long StepIndex, ulong Frame, bool StepLanded, double PlayerDt = 0)
{
    /// <summary>The script-facing form: a Map <c>{ut, dt, stepIndex, frame}</c> (the <c>nextStep</c> wait result and tick context fields).</summary>
    /// <returns>The step as a <see cref="ScriptValue"/> Map.</returns>
    public ScriptValue ToScriptValue()
        => ScriptValue.Map(new Dictionary<string, ScriptValue>(StringComparer.Ordinal)
        {
            ["ut"] = ScriptValue.From(Ut),
            ["dt"] = ScriptValue.From(Dt),
            ["stepIndex"] = ScriptValue.From((double)StepIndex),
            ["frame"] = ScriptValue.From((double)Frame),
        });
}
