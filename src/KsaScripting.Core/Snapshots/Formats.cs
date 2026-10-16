using System.Globalization;

namespace KsaScripting.Core.Snapshots;

/// <summary>
///     The fixed text formatting of snapshot scalars used in event detail strings (ported from gatOS
///     <c>Formats</c>, reduced to the pure scalar/vector helpers; the gatOS-only spec-line formatters are
///     not part of ksa-scripting). Event detail text is script-visible, so change it deliberately.
/// </summary>
public static class Formats
{
    /// <summary>Formats a double: <c>G9</c>, invariant culture.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The text.</returns>
    public static string Scalar(double value) => value.ToString("G9", CultureInfo.InvariantCulture);

    /// <summary>Formats a boolean as <c>0</c>/<c>1</c>.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The text.</returns>
    public static string Flag(bool value) => value ? "1" : "0";

    /// <summary>Formats a vector as <c>x y z</c>.</summary>
    /// <param name="v">The vector.</param>
    /// <returns>The text.</returns>
    public static string Vector(double3Snap v) => $"{Scalar(v.X)} {Scalar(v.Y)} {Scalar(v.Z)}";

    /// <summary>Formats a quaternion as <c>x y z w</c>.</summary>
    /// <param name="q">The quaternion.</param>
    /// <returns>The text.</returns>
    public static string Quat(QuatSnap q) => $"{Scalar(q.X)} {Scalar(q.Y)} {Scalar(q.Z)} {Scalar(q.W)}";

    /// <summary>Formats an unsigned integer (e.g. a part <c>InstanceId</c>), invariant culture.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The text.</returns>
    public static string UInt(uint value) => value.ToString(CultureInfo.InvariantCulture);
}
