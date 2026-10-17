using System.Text;

namespace Catcom.Core.Api;

/// <summary>Name conversion between the TypeScript (camelCase) and Lua (snake_case) spellings of the API.</summary>
public static class ApiNames
{
    /// <summary>
    ///     Converts a camelCase name to snake_case: an underscore goes before an uppercase letter that
    ///     follows a lowercase letter or a digit, then everything is lowercased
    ///     (<c>vacThrustN</c> → <c>vac_thrust_n</c>, <c>stepIndex</c> → <c>step_index</c>,
    ///     <c>attitudeBody2Cci</c> → <c>attitude_body2_cci</c>). Names that are already snake_case are
    ///     returned unchanged. The Lua prelude applies the same rule to every host table key.
    /// </summary>
    /// <param name="camel">The camelCase (or snake_case) name.</param>
    /// <returns>The snake_case name.</returns>
    public static string ToSnake(string camel)
    {
        ArgumentNullException.ThrowIfNull(camel);

        var builder = new StringBuilder(camel.Length + 4);
        for (var i = 0; i < camel.Length; i++)
        {
            var c = camel[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && (char.IsLower(camel[i - 1]) || char.IsDigit(camel[i - 1])))
                    builder.Append('_');
                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
