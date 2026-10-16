// Compile-only check: a StarMap mod skeleton + Harmony patches against confirmed KSA members.
using HarmonyLib;
using KSA;
using StarMap.API;

namespace KsaScripting.HookCompileCheck;

[StarMapMod]
public class Mod
{
    public static Mod? Instance;
    private static readonly Harmony _harmony = new("meowsci.ksa-scripting.compilecheck");
    private static readonly Queue<Action> _gameThreadQueue = new();

    [StarMapImmediateLoad] public void OnImmediateLoad(KSA.Mod definingMod) { }
    [StarMapAllModsLoaded] public void OnAllModsLoaded() { Instance = this; _harmony.PatchAll(typeof(Mod).Assembly); }
    [StarMapBeforeGui] public void OnBeforeGui(double dt) { lock (_gameThreadQueue) while (_gameThreadQueue.Count > 0) _gameThreadQueue.Dequeue()(); }
    [StarMapAfterGui] public void OnAfterGui(double dt) { }
    [StarMapAfterOnFrame] public void OnAfterFrame(double currentPlayerTime, double dtPlayer) { }
    [StarMapUnload] public void OnUnload() { _harmony.UnpatchAll(_harmony.Id); Instance = null; }

    public static void Post(Action a) { lock (_gameThreadQueue) _gameThreadQueue.Enqueue(a); }
}

[HarmonyPatch(typeof(EngineController), nameof(EngineController.SetIsActive))]
static class EngineIgnitePatch
{
    static void Postfix(EngineController __instance, Vehicle? vehicle, bool activationState)
    {
        // event: engine (de)activated
        _ = __instance; _ = vehicle; _ = activationState;
    }
}

[HarmonyPatch(typeof(Universe), nameof(Universe.ExecuteNextVehicleSolvers))]
static class SolverStepPatch
{
    [HarmonyPriority(Priority.First)]
    static void Prefix(double dtPlayer, SimStep simStep) { _ = dtPlayer; _ = simStep; }
}

[HarmonyPatch(typeof(Program), "OnFrame")] // internal: patched by name
static class FramePatch
{
    static void Postfix(double currentPlayerTime, double dtPlayer) { _ = currentPlayerTime; _ = dtPlayer; }
}

static class ReadSurface
{
    public static (double simSeconds, int vehicles, string? controlled) Snapshot()
    {
        int count = 0; if (Universe.CurrentSystem is { } sys) foreach (var _v in sys.All.OfType<Vehicle>()) count++;
        var controlled = Program.ControlledVehicle;
        return (Universe.GetElapsedSeconds(), count, controlled?.Id);
    }
    public static void ThrottleAllEngines(Vehicle v, bool on)
    {
        foreach (var ec in v.Parts.Modules.Get<EngineController>()) ec.SetIsActive(v, on);
        v.Parts.RecomputeAllDerivedData();
    }
}
