import { vessel } from "./ksa.ts";
interface Reading { alt: number; vel: number }
export async function onIgnite(ev: { engine: string; vesselId: string }): Promise<void> {
  const r: Reading = { alt: vessel.altitude, vel: vessel.speed };
  let total = 0;
  for (let i = 0; i < 10; i++) total += r.alt * i;
  console.log(`ignite ${ev.engine} on ${ev.vesselId}: ${total}`);
}
enum Mode { A = 1, B = 2 }
export const mode: Mode = Mode.B;