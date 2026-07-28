import { pathToFileURL } from "node:url";

const workingDirectory = process.cwd();
const target = pathToFileURL(`${workingDirectory}/main.mjs`).href;

for (const name of [
  "process",
  "global",
  "GLOBAL",
  "root",
  "Buffer",
  "fetch",
  "WebSocket",
  "EventSource",
  "BroadcastChannel",
  "MessageChannel",
  "MessagePort",
  "Worker",
  "SharedArrayBuffer",
  "WebAssembly"
]) {
  try {
    Object.defineProperty(globalThis, name, {
      value: undefined,
      writable: false,
      enumerable: false,
      configurable: false
    });
  } catch {
    // Node flags and the syscall sandbox remain the mandatory boundary.
  }
}

await import(target);
