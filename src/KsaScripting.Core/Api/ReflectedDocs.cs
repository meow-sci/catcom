using System.Text;

namespace KsaScripting.Core.Api;

/// <summary>
///     Documentation of the reflected data-class members: a curated sentence for the fields players use
///     most, and a generated one (<c>"Vac thrust, in newtons (from the game's EngineSnapshot)."</c>) for the rest.
/// </summary>
internal static class ReflectedDocs
{
    private static readonly Dictionary<string, string> Curated = new(StringComparer.Ordinal)
    {
        ["EngineSnapshot.Active"] = "Whether the engine is switched on.",
        ["EngineSnapshot.VacThrustN"] = "Vacuum thrust at full throttle, in newtons.",
        ["EngineSnapshot.IspS"] = "Specific impulse, in seconds.",
        ["EngineSnapshot.ThrottleCmd"] = "The commanded throttle, 0 to 1.",
        ["EngineSnapshot.PropellantAvailable"] = "Whether propellant currently reaches the engine.",
        ["EngineSnapshot.MinThrottle"] = "The configured minimum throttle (the deep-throttle floor), 0 to 1.",
        ["TankSnapshot.Resource"] = "The resource name (for example a propellant).",
        ["TankSnapshot.Amount"] = "The current amount of the resource.",
        ["TankSnapshot.Capacity"] = "The tank capacity.",
        ["TankSnapshot.Fraction"] = "The fill fraction, 0 to 1.",
        ["RcsSnapshot.Active"] = "Whether the thruster controller is switched on.",
        ["RcsSnapshot.PropellantAvailable"] = "Whether propellant is available to the thrusters.",
        ["RcsSnapshot.ControlMap"] = "The control axes this controller serves, as text such as `Pitch|Yaw`.",
        ["SolarSnapshot.ProducedW"] = "Instantaneous power produced, in watts.",
        ["SolarSnapshot.Occluded"] = "Whether the panel is shadowed.",
        ["SolarSnapshot.SunAoaDeg"] = "The sun's angle of attack on the panel, in degrees.",
        ["SolarSnapshot.Efficiency"] = "The sun-angle efficiency, 0 to 1.",
        ["SolarSnapshot.HasTracker"] = "Whether the panel has a sun tracker.",
        ["SolarSnapshot.TrackerAngleDeg"] = "The tracker's current angle, in degrees (0 without a tracker).",
        ["SolarSnapshot.AnimationIndex"] = "The ordinal of the panel's deploy animation, or -1 for a fixed panel.",
        ["GeneratorSnapshot.Active"] = "Whether the generator is running.",
        ["GeneratorSnapshot.ProducedW"] = "Instantaneous power produced, in watts.",
        ["LightSnapshot.On"] = "Whether the light is switched on.",
        ["LightSnapshot.Intensity"] = "The light intensity, in template units.",
        ["LightSnapshot.Color"] = "The light colour, each component 0 to 1.",
        ["LightSnapshot.AnimationIndex"] = "The ordinal of the light's actuate animation, or -1 when it has none.",
        ["LightSnapshot.OuterAngleDeg"] = "The spotlight cone outer half-angle (the hard edge of the beam), in degrees.",
        ["LightSnapshot.InnerAngleDeg"] = "The spotlight cone inner half-angle (full brightness inside it), in degrees.",
        ["DockingSnapshot.Docked"] = "Whether the port is docked.",
        ["DockingSnapshot.DockedToPart"] = "The id of the part it is docked to, or null.",
        ["DockingSnapshot.PushoffImpulseNs"] = "The separation impulse applied when the port undocks, in newton-seconds.",
        ["DecouplerSnapshot.Fired"] = "Whether the decoupler has fired (irreversible).",
        ["DecouplerSnapshot.Enabled"] = "Whether the decoupler module is enabled; a disabled decoupler cannot fire.",
        ["EncounterSnapshot.Body"] = "The id of the body being encountered.",
        ["EncounterSnapshot.Ut"] = "Universal time of closest approach, in seconds.",
        ["EncounterSnapshot.DistanceMeters"] = "Closest-approach distance, in metres.",
        ["OrbitSnapshot.ApoapsisAltitude"] = "Apoapsis altitude above the parent body's surface, in metres.",
        ["OrbitSnapshot.PeriapsisAltitude"] = "Periapsis altitude above the parent body's surface, in metres.",
        ["OrbitSnapshot.Eccentricity"] = "Orbital eccentricity.",
        ["OrbitSnapshot.InclinationDeg"] = "Inclination, in degrees.",
        ["OrbitSnapshot.SmaMeters"] = "Semi-major axis, in metres.",
        ["OrbitSnapshot.PeriodSeconds"] = "Orbital period, in seconds.",
        ["OrbitSnapshot.LanDeg"] = "Longitude of the ascending node, in degrees.",
        ["OrbitSnapshot.ArgPeDeg"] = "Argument of periapsis, in degrees.",
        ["OrbitSnapshot.TrueAnomalyDeg"] = "True anomaly, in degrees.",
        ["OrbitSnapshot.TimeToApoapsis"] = "Seconds until the next apoapsis (0 when unbound or unknown).",
        ["OrbitSnapshot.TimeToPeriapsis"] = "Seconds until the next periapsis (0 when unbound or unknown).",
        ["OrbitSnapshot.NextPatchEventUt"] = "Universal time of the next orbit patch transition (sphere-of-influence change or escape), 0 when none.",
        ["AnimationSnapshot.GoalFraction"] = "The commanded deploy fraction, 0 (retracted) to 1 (deployed).",
        ["AnimationSnapshot.CurrentFraction"] = "The actual deploy fraction, 0 (retracted) to 1 (deployed).",
        ["AnimationSnapshot.DeploymentState"] = "`Deployed`, `Retracted`, `Deploying`, `Retracting` or `Broken`.",
        ["AnimationSnapshot.IsSolar"] = "Whether this animation deploys a solar panel (the panel is also in `solar`).",
        ["NavballSnapshot.PitchDeg"] = "Pitch, in degrees.",
        ["NavballSnapshot.YawDeg"] = "Yaw (heading), in degrees.",
        ["NavballSnapshot.RollDeg"] = "Roll, in degrees.",
        ["NavballSnapshot.ThrustWeightRatio"] = "The current thrust-to-weight ratio.",
        ["NavballSnapshot.DeltaVVacuumMs"] = "Remaining vacuum delta-v, in m/s.",
        ["NavballSnapshot.Frame"] = "The navball reference frame (`EclBody`, `Lvlh`, ...).",
        ["NavballSnapshot.SpeedMs"] = "The navball speed readout, in m/s.",
        ["EnvironmentSnapshot.PressurePa"] = "Static atmospheric pressure, in pascals.",
        ["EnvironmentSnapshot.DensityKgM3"] = "Atmospheric density, in kg/m^3.",
        ["EnvironmentSnapshot.DynamicPressurePa"] = "Dynamic pressure (q), in pascals.",
        ["EnvironmentSnapshot.OceanDensityKgM3"] = "Ocean density, in kg/m^3 (0 outside an ocean).",
        ["EnvironmentSnapshot.TerrainRadiusM"] = "Terrain radius below the vessel, in metres.",
        ["EnvironmentSnapshot.AccelBody"] = "Linear acceleration in the body frame, in m/s^2.",
        ["EnvironmentSnapshot.AngularAccelBody"] = "Angular acceleration in the body frame, in rad/s^2.",
        ["EnvironmentSnapshot.GForce"] = "Acceleration magnitude in g.",
        ["BodySnapshot.Id"] = "The body id.",
        ["BodySnapshot.Class"] = "The body class: `Planet`, `Moon`, `Star`, ...",
        ["BodySnapshot.ParentId"] = "The parent body's id, or null for a root star or barycentre.",
        ["BodySnapshot.ChildIds"] = "The ids of the bodies orbiting this one.",
        ["BodySnapshot.Mass"] = "Mass, in kilograms.",
        ["BodySnapshot.MeanRadius"] = "Mean radius, in metres.",
        ["BodySnapshot.Mu"] = "Standard gravitational parameter, in m^3/s^2.",
        ["BodySnapshot.SoiMeters"] = "Sphere-of-influence radius, in metres.",
        ["BodySnapshot.RotationRateRadS"] = "Sidereal rotation rate, in rad/s.",
        ["BodySnapshot.PositionEcl"] = "Position in the system's ecliptic frame, in metres.",
        ["BodySnapshot.VelocityEcl"] = "Velocity in the system's ecliptic frame, in m/s.",
        ["BodySnapshot.Orbit"] = "The orbit about the parent, or null for a root.",
        ["BodySnapshot.Atmosphere"] = "The atmosphere, or null when airless.",
        ["BodySnapshot.Ocean"] = "The ocean, or null when dry.",
        ["AtmosphereSnapshot.HeightM"] = "Height of the atmosphere boundary above the surface, in metres.",
        ["AtmosphereSnapshot.ScaleHeightM"] = "Scale height, in metres.",
        ["AtmosphereSnapshot.SeaLevelPressurePa"] = "Sea-level pressure, in pascals.",
        ["AtmosphereSnapshot.SeaLevelDensityKgM3"] = "Sea-level density, in kg/m^3.",
        ["OceanSnapshot.DensityKgM3"] = "Ocean density, in kg/m^3.",
        ["PartSnapshot.InstanceId"] = "The runtime-unique part id; stable while the vessel exists.",
        ["PartSnapshot.Id"] = "The part id string (not unique across instances of one template).",
        ["PartSnapshot.DisplayName"] = "The human-readable part name.",
        ["PartSnapshot.Template"] = "The part template id.",
        ["PartSnapshot.IsRoot"] = "Whether this is the vessel's root part.",
        ["PartSnapshot.SubpartCount"] = "The number of subparts.",
        ["PartSnapshot.PositionVehicleAsmb"] = "Part position in the vehicle assembly frame, in metres.",
        ["PartSnapshot.Subparts"] = "The part's subparts.",
        ["SubpartSnapshot.InstanceId"] = "The runtime-unique subpart id.",
        ["SubpartSnapshot.Id"] = "The subpart id string.",
        ["SubpartSnapshot.DisplayName"] = "The human-readable subpart name.",
        ["SubpartSnapshot.Template"] = "The subpart template id.",
        ["SubpartSnapshot.PositionVehicleAsmb"] = "Subpart position in the vehicle assembly frame, in metres.",
        ["SrbSnapshot.Active"] = "Whether the motor is currently burning.",
        ["SrbSnapshot.MassKg"] = "Current total grain mass, in kilograms.",
        ["SrbSnapshot.MassInitialKg"] = "Full-stack grain mass when new, in kilograms.",
        ["SrbSnapshot.EngineIndex"] = "The index in `engines` of the engine that ignites this motor, or -1.",
        ["SrbSnapshot.Segments"] = "The grain segments feeding the motor.",
    };

    private static readonly (string Suffix, string Unit)[] UnitSuffixes =
    [
        ("KgM3", "kg/m^3"), ("RadS", "rad/s"), ("Meters", "metres"), ("Seconds", "seconds"), ("Joules", "joules"),
        ("Deg", "degrees"), ("KgS", "kg/s"), ("Ms", "m/s"), ("Pa", "pascals"), ("Ns", "newton-seconds"), ("Kg", "kilograms"),
        ("M2", "m^2"), ("M3", "m^3"),
        ("W", "watts"), ("N", "newtons"), ("S", "seconds"), ("M", "metres"), ("K", "kelvin"),
    ];

    /// <summary>The documentation of <paramref name="property"/> of the snapshot record <paramref name="record"/>.</summary>
    internal static string For(string record, string property)
    {
        if (Curated.TryGetValue($"{record}.{property}", out var curated))
            return curated;
        if (property == "Index")
            return "The module's index on the vessel (its ordinal for actions).";

        var (stem, unit) = SplitUnit(property);
        var words = Humanize(stem);
        return unit is null
            ? $"{words} (from the game's {record})."
            : $"{words}, in {unit} (from the game's {record}).";
    }

    private static (string Stem, string? Unit) SplitUnit(string property)
    {
        foreach (var (suffix, unit) in UnitSuffixes)
        {
            var cut = property.Length - suffix.Length;
            if (cut > 0 && property.EndsWith(suffix, StringComparison.Ordinal) && (char.IsLower(property[cut - 1]) || char.IsDigit(property[cut - 1])))
                return (property[..cut], unit);
        }

        return (property, null);
    }

    private static string Humanize(string pascal)
    {
        var builder = new StringBuilder(pascal.Length + 4);
        for (var i = 0; i < pascal.Length; i++)
        {
            var c = pascal[i];
            if (i == 0)
                builder.Append(char.ToUpperInvariant(c));
            else if (char.IsUpper(c) && (char.IsLower(pascal[i - 1]) || char.IsDigit(pascal[i - 1])))
                builder.Append(' ').Append(char.ToLowerInvariant(c));
            else
                builder.Append(c);
        }

        return builder.ToString();
    }
}
