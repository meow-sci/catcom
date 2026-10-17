using Catcom.Core.Values;

namespace Catcom.Core.Snapshots;

/// <summary>
///     The script-facing projection of the snapshot records, used by the game bridge's
///     <c>Read(handle, path)</c>: scripts read <c>vessel.position.cci</c>, not the raw record property
///     names. Every number is finite (NaN/±Infinity → 0); keys are camelCase.
/// </summary>
public static class ScriptViews
{
    /// <summary>Projects a vector to the map <c>{x, y, z}</c>.</summary>
    /// <param name="v">The vector.</param>
    /// <returns>The map value.</returns>
    public static ScriptValue Vec3(double3Snap v)
        => Build(("x", Num(v.X)), ("y", Num(v.Y)), ("z", Num(v.Z)));

    /// <summary>Projects a quaternion to the map <c>{x, y, z, w}</c>.</summary>
    /// <param name="q">The quaternion.</param>
    /// <returns>The map value.</returns>
    public static ScriptValue Quat(QuatSnap q)
        => Build(("x", Num(q.X)), ("y", Num(q.Y)), ("z", Num(q.Z)), ("w", Num(q.W)));

    /// <summary>
    ///     Projects a vessel to a map with these keys, in this order: <c>id, name, situation, parent</c>
    ///     (parent body name or <c>""</c>)<c>, controlled, controllable, isKitten,
    ///     position {cci, ecl, lat, lon}, velocity {orbital, surface, inertial, cci},
    ///     attitude {quat, rates}, altitude {barometric, radar},
    ///     mass {total, dry, propellant, centerOfMass}, orbit, navball, environment</c> (the last three
    ///     are the snapshot records as maps, or Null when unavailable)<c>,
    ///     power {producedW, consumedW}, battery {fraction, capacityJ}</c> (each Null when there is no
    ///     battery)<c>, engines, tanks, rcs, solar, generators, lights, docking, decouplers, animations,
    ///     srb, encounters, parts</c> (lists of snapshot-record maps)<c>, throttleCmd, attitudeMode,
    ///     attitudeFrame, rcsMode, rcsOn, engineOn, scale, alwaysRender, translateCmd, rotateCmd</c>
    ///     (vectors are <see cref="Vec3"/> maps).
    /// </summary>
    /// <param name="v">The vessel snapshot.</param>
    /// <returns>The map value.</returns>
    public static ScriptValue Vessel(VesselSnapshot v)
    {
        ArgumentNullException.ThrowIfNull(v);
        return Build(
            ("id", ScriptValue.From(v.Id)),
            ("name", ScriptValue.From(v.Name)),
            ("situation", ScriptValue.From(v.Situation)),
            ("parent", ScriptValue.From(v.ParentBodyName ?? "")),
            ("controlled", ScriptValue.From(v.Controlled)),
            ("controllable", ScriptValue.From(v.Controllable)),
            ("isKitten", ScriptValue.From(v.IsKitten)),
            ("position", Build(
                ("cci", Vec3(v.PositionCci)),
                ("ecl", Vec3(v.PositionEcl)),
                ("lat", Num(v.LatitudeDeg)),
                ("lon", Num(v.LongitudeDeg)))),
            ("velocity", Build(
                ("orbital", Num(v.OrbitalSpeed)),
                ("surface", Num(v.SurfaceSpeed)),
                ("inertial", Num(v.InertialSpeed)),
                ("cci", Vec3(v.VelocityCci)))),
            ("attitude", Build(
                ("quat", Quat(v.AttitudeBody2Cci)),
                ("rates", Vec3(v.BodyRatesRadS)))),
            ("altitude", Build(
                ("barometric", Num(v.BarometricAltitude)),
                ("radar", Num(v.RadarAltitude)))),
            ("mass", Build(
                ("total", Num(v.MassTotal)),
                ("dry", Num(v.MassDry)),
                ("propellant", Num(v.MassPropellant)),
                ("centerOfMass", Vec3(v.CenterOfMass)))),
            ("orbit", ScriptValue.FromObject(v.Orbit)),
            ("navball", ScriptValue.FromObject(v.Navball)),
            ("environment", ScriptValue.FromObject(v.Environment)),
            ("power", Build(
                ("producedW", Num(v.PowerProducedW)),
                ("consumedW", Num(v.PowerConsumedW)))),
            ("battery", Build(
                ("fraction", v.BatteryChargeFraction is { } fraction ? Num(fraction) : ScriptValue.Null),
                ("capacityJ", v.BatteryCapacityJoules is { } capacity ? Num(capacity) : ScriptValue.Null))),
            ("engines", ScriptValue.FromObject(v.Engines)),
            ("tanks", ScriptValue.FromObject(v.Tanks)),
            ("rcs", ScriptValue.FromObject(v.Rcs)),
            ("solar", ScriptValue.FromObject(v.Solar)),
            ("generators", ScriptValue.FromObject(v.Generators)),
            ("lights", ScriptValue.FromObject(v.Lights)),
            ("docking", ScriptValue.FromObject(v.Docking)),
            ("decouplers", ScriptValue.FromObject(v.Decouplers)),
            ("animations", ScriptValue.FromObject(v.Animations)),
            ("srb", ScriptValue.FromObject(v.Srb)),
            ("encounters", ScriptValue.FromObject(v.Encounters)),
            ("parts", ScriptValue.FromObject(v.Parts)),
            ("throttleCmd", Num(v.ThrottleCmd)),
            ("attitudeMode", ScriptValue.From(v.AttitudeMode)),
            ("attitudeFrame", ScriptValue.From(v.AttitudeFrame)),
            ("rcsMode", ScriptValue.From(v.RcsMode)),
            ("rcsOn", ScriptValue.From(v.RcsOn)),
            ("engineOn", ScriptValue.From(v.EngineOn)),
            ("scale", Num(v.Scale)),
            ("alwaysRender", ScriptValue.From(v.AlwaysRender)),
            ("translateCmd", Vec3(v.TranslateCmd)),
            ("rotateCmd", Vec3(v.RotateCmd)));
    }

    /// <summary>Projects a celestial body with <see cref="ScriptValue.FromObject"/> (camelCase record keys).</summary>
    /// <param name="b">The body snapshot.</param>
    /// <returns>The map value.</returns>
    public static ScriptValue Body(BodySnapshot b) => ScriptValue.FromObject(b);

    /// <summary>Projects the loaded system to <c>{name, home, sun}</c> (home/sun are body ids or Null).</summary>
    /// <param name="s">The system snapshot, or <c>null</c> when no system is loaded.</param>
    /// <returns>The map value, or <see cref="ScriptValue.Null"/> when <paramref name="s"/> is <c>null</c>.</returns>
    public static ScriptValue System(SystemSnapshot? s)
        => s is null
            ? ScriptValue.Null
            : Build(
                ("name", ScriptValue.From(s.Name)),
                ("home", ScriptValue.From(s.HomeBodyId)),
                ("sun", ScriptValue.From(s.SunId)));

    /// <summary>
    ///     Projects the game clock with <see cref="ScriptValue.FromObject"/>; keys are <c>ut, warp, simDt,
    ///     paused, warpSpeeds, autoWarp, autoWarpTargetUt, activeVesselId, frame, stepIndex</c>.
    /// </summary>
    /// <param name="t">The time view.</param>
    /// <returns>The map value.</returns>
    public static ScriptValue Time(TimeView t) => ScriptValue.FromObject(t);

    private static ScriptValue Num(double value) => ScriptValue.From(double.IsFinite(value) ? value : 0d);

    private static ScriptValue Build(params (string Key, ScriptValue Value)[] entries)
    {
        var map = new Dictionary<string, ScriptValue>(entries.Length, StringComparer.Ordinal);
        foreach (var (key, value) in entries)
            map[key] = value;
        return ScriptValue.Map(map);
    }
}
