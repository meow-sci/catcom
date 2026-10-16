# Spike assets

- `sucrase.bundle.js` — sucrase 3.35.1 (MIT) bundled as a single IIFE exposing `globalThis.sucrase.transform`, built with `bun build entry.js --format=iife --target=browser --minify` where `entry.js` is `import { transform } from "sucrase"; globalThis.sucrase = { transform };`. Used by `V8TsSpike` and `JintTsSpike` as the lightweight TypeScript transpiler.
- `typescript.js` is not vendored here; the spikes default to `../../ksa-game-assemblies/node_modules/typescript/lib/typescript.js` (TypeScript 6.0.3) or take a path as the first argument.
