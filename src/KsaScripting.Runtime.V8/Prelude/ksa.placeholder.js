// PLACEHOLDER ksa prelude - used ONLY while src/KsaScripting.Core/Api/Preludes/ksa.js does not exist.
//
// The csproj embeds the real prelude from Core when that file is present; this file is the fallback so the V8
// runtime still builds and its tests still run before the prelude team has delivered. It implements a tiny subset of
// the API surface over the same `__host` contract (docs/IMPLEMENTATION_GUIDE.md section 3.7), and nothing else.

const host = globalThis.__host;

function vessel(id) {
  const v = String(id);
  const act = (action, value, values, token, aux) => host.act(action, v, -1, value, values ?? null, token ?? null, aux ?? null);
  return {
    id: v,
    ignite: () => act("vessel.ignite", 0),
    shutdown: () => act("vessel.shutdown", 0),
    stage: () => act("vessel.stage", 0),
    setThrottle: (f) => act("vessel.throttle", Number(f)),
    throttle: (f) => act("vessel.throttle", Number(f)),
    // Solver-phase action: the host returns a Promise of the result map.
    attitudeMode: (m) => act("vessel.attitude_mode", 0, null, String(m)),
    setAttitudeMode: (m) => act("vessel.attitude_mode", 0, null, String(m)),
  };
}

function log(level) {
  return (...args) => host.log(level, args.map(String).join(" "));
}

const consoleApi = {
  log: log(1),
  info: log(1),
  debug: log(0),
  warn: log(2),
  error: log(3),
  exec: (command) => host.consoleExec(String(command)),
};

export const ksa = {
  vessel,
  sleep: (seconds, clock) => host.wait("sleep", { seconds: Number(seconds), clock: clock || "ut" }),
  nextStep: () => host.wait("nextStep", null),
  nextFrame: () => host.wait("nextFrame", null),
  on(event, fn, opts) {
    const id = host.subscribe("event", String(event), fn, opts ?? {});
    return { id, unsubscribe() { host.unsubscribe(id); } };
  },
  console: consoleApi,
  state: {
    get: (key) => host.stateGet(String(key)),
    set: (key, value) => host.stateSet(String(key), value === undefined ? null : value),
  },
  makeContext(map) {
    const ctx = {};
    for (const key of Object.keys(map)) {
      if (key !== "$ctx") ctx[key] = map[key];
    }
    ctx.ksa = ksa;
    if (map.vesselId !== undefined && map.vesselId !== null) ctx.vessel = vessel(map.vesselId);
    return ctx;
  },
};

globalThis.ksa = ksa;
globalThis.console = ksa.console;

export default ksa;
