import { useEffect, useRef } from "react";
import * as THREE from "three";
import { SceneManager } from "./SceneManager";
import { loadGlb } from "./AsyncGltfLoader";
import { useViewerStore, ElementProps } from "../store";

interface Props {
  url: string | null;
  onReady?: (mgr: SceneManager | null) => void;
}

export function Viewer({ url, onReady }: Props) {
  const containerRef = useRef<HTMLDivElement | null>(null);
  const managerRef = useRef<SceneManager | null>(null);
  const setProgress = useViewerStore((s) => s.setProgress);
  const setLoading = useViewerStore((s) => s.setLoading);
  const setSelected = useViewerStore((s) => s.setSelected);
  const setHiddenCount = useViewerStore((s) => s.setHiddenCount);
  const setLayers = useViewerStore((s) => s.setLayers);

  useEffect(() => {
    if (!containerRef.current) return;
    const mgr = new SceneManager(containerRef.current);
    mgr.onPick((mesh) => setSelected(mesh ? resolveExtras(mesh) : null));
    mgr.onStats(useViewerStore.getState().setStats);
    mgr.onHiddenChange((n) => setHiddenCount(n));
    managerRef.current = mgr;
    onReady?.(mgr);
    return () => {
      onReady?.(null);
      mgr.dispose();
      managerRef.current = null;
    };
    // onReady intentionally excluded: a fresh callback identity each render
    // would tear down the WebGL context. App passes a stable ref setter.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [setSelected, setHiddenCount]);

  useEffect(() => {
    if (!url || !managerRef.current) return;
    const mgr = managerRef.current;
    const ctl = new AbortController();
    let cancelled = false;

    (async () => {
      setLoading(true);
      setProgress(0);
      try {
        const gltf = await loadGlb(url, {
          signal: ctl.signal,
          onProgress: (p) => setProgress(p),
        });
        if (cancelled) return;
        await mgr.addGltfProgressively(gltf);
        if (cancelled) return;
        // Populate the layer panel for DWG-sourced glbs. Returns [] for Revit
        // glbs (no extras.layer), in which case LayerPanel renders nothing.
        setLayers(mgr.getLayers());
      } catch (err) {
        if (!cancelled) console.error(err);
      } finally {
        if (!cancelled) setLoading(false);
      }
    })();

    return () => {
      cancelled = true;
      ctl.abort();
    };
  }, [url, setLoading, setProgress, setLayers]);

  return <div ref={containerRef} style={{ width: "100%", height: "100%" }} />;
}

// Walks up the scene graph looking for the first node with glTF extras
// (GLTFLoader assigns `extras` → `object.userData`). Recognizes both Revit
// (elementId / parameters) and DWG (handle / layer) extras shapes.
function resolveExtras(mesh: THREE.Object3D): ElementProps | null {
  let obj: THREE.Object3D | null = mesh;
  while (obj) {
    const data = obj.userData as Record<string, unknown> | undefined;
    if (
      data &&
      (data.elementId !== undefined ||
        data.parameters !== undefined ||
        data.handle !== undefined ||
        data.layer !== undefined)
    ) {
      return {
        elementId: data.elementId as number | undefined,
        category: data.category as string | undefined,
        family: data.family as string | undefined,
        type:
          (data.type as string | undefined) ??
          (data.entityType as string | undefined),
        layer: data.layer as string | undefined,
        parameters:
          (data.parameters as Record<string, unknown> | undefined) ??
          buildDwgParameters(data),
      };
    }
    obj = obj.parent;
  }
  return null;
}

function buildDwgParameters(
  data: Record<string, unknown>,
): Record<string, unknown> | undefined {
  // Pick the keys our DwgPropertyCollector emits, but only include them for
  // DWG-shaped extras (handle present) so we don't pollute Revit panels.
  if (data.handle === undefined) return undefined;
  const keys = [
    "handle",
    "linetype",
    "lineweight",
    "color",
    "colorIndex",
    "xdata",
    "extDict",
    "entities",
  ];
  const out: Record<string, unknown> = {};
  for (const k of keys) {
    if (data[k] !== undefined) out[k] = data[k];
  }
  return Object.keys(out).length > 0 ? out : undefined;
}
