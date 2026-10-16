using KsaScripting.Core.Commands;
using KsaScripting.Core.Values;

namespace KsaScripting.Core.Hosting;

/// <summary>Conversions of <see cref="CommandResult"/> for the script boundary.</summary>
public static class CommandResultExtensions
{
    /// <summary>
    ///     Converts a result to the map scripts receive: <c>{ok: bool, outcome: "Ok"|"Invalid"|…, message: string|null}</c>
    ///     (keys in that order).
    /// </summary>
    /// <param name="result">The command result.</param>
    /// <returns>The map value.</returns>
    public static ScriptValue ToScriptValue(this CommandResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return ScriptValue.Map(new Dictionary<string, ScriptValue>
        {
            ["ok"] = ScriptValue.From(result.IsSuccess),
            ["outcome"] = ScriptValue.From(result.Outcome.ToString()),
            ["message"] = ScriptValue.From(result.Message),
        });
    }
}
