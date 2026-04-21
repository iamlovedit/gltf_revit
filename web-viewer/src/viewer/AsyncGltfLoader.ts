import { GLTFLoader, GLTF } from "three/examples/jsm/loaders/GLTFLoader.js";
import { DRACOLoader } from "three/examples/jsm/loaders/DRACOLoader.js";

let cachedLoader: GLTFLoader | null = null;

function getLoader(): GLTFLoader {
  if (cachedLoader) return cachedLoader;
  const draco = new DRACOLoader();
  draco.setDecoderPath("/draco/");
  draco.setDecoderConfig({ type: "wasm" });
  draco.setWorkerLimit(
    Math.max(1, Math.min(4, navigator.hardwareConcurrency ?? 2)),
  );
  cachedLoader = new GLTFLoader().setDRACOLoader(draco);
  return cachedLoader;
}

export interface LoadOptions {
  onProgress?: (ratio: number) => void;
  signal?: AbortSignal;
}

// Streams the response body so a progress bar updates smoothly while the
// network transfer is in flight, then hands the assembled ArrayBuffer to
// GLTFLoader.parseAsync. Parse + Draco decode happen on a worker pool.
export async function loadGlb(
  url: string,
  opts: LoadOptions = {},
): Promise<GLTF> {
  const res = await fetch(url, { signal: opts.signal });
  if (!res.ok) throw new Error(`Failed to fetch ${url}: ${res.status}`);
  if (!res.body) throw new Error("ReadableStream not supported by response");

  const total = Number(res.headers.get("Content-Length") ?? 0);
  const reader = res.body.getReader();
  const chunks: Uint8Array[] = [];
  let received = 0;

  while (true) {
    const { done, value } = await reader.read();
    if (done) break;
    if (value) {
      chunks.push(value);
      received += value.byteLength;
      if (total && opts.onProgress) opts.onProgress(received / total);
      else if (opts.onProgress)
        opts.onProgress(Math.min(0.95, received / (received + 1_000_000)));
    }
  }
  opts.onProgress?.(1);

  const buffer = new Uint8Array(received);
  let offset = 0;
  for (const c of chunks) {
    buffer.set(c, offset);
    offset += c.byteLength;
  }

  return await getLoader().parseAsync(buffer.buffer, "");
}
