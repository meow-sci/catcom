namespace KsaScripting.Core.Api;

/// <summary>What kind of script-facing member an <see cref="ApiMember"/> describes.</summary>
public enum ApiKind
{
    /// <summary>A readable value (a getter in the preludes).</summary>
    Property,

    /// <summary>A callable function or method.</summary>
    Method,

    /// <summary>A subscribable event; <see cref="ApiMember.Params"/> are its payload fields.</summary>
    Event,
}
