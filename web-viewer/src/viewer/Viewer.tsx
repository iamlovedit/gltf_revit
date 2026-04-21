import { useEffect, useRef } from 'react';
import * as THREE from 'three';
import { SceneManager } from './SceneManager';
import { loadGlb } from './AsyncGltfLoader';
import { useViewerStore, ElementProps } from '../store';

interface Props {
  url: string | null;
}

export function Viewer({ url }: Props) {
  const containerRef = useRef<HTMLDivElement | null>(null);
  const managerRef = useRef<SceneManager | null>(null);
  const setProgress = useViewerStore((s) => s.setProgress);
  const setLoading = useViewerStore((s) => s.setLoading);
  const setSelected = useViewerStore((s) => s.setSelected);

  useEffect(() => {
    if (!containerRef.current) return;
    const mgr = new SceneManager(containerRef.current);
    mgr.onPick((mesh) => setSelected(mesh ? resolveExtras(mesh) : null));
    managerRef.current = mgr;
    return () => {
      mgr.dispose();
      managerRef.current = null;
    };
  }, [setSelected]);

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
          onProgress: (p) => setProgress(p)
        });
        if (cancelled) return;
        await mgr.addGltfProgressively(gltf);
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
  }, [url, setLoading, setProgress]);

  return <div ref={containerRef} style={{ width: '100%', height: '100%' }} />;
}

// Walks up the scene graph looking for the first node with glTF extras
// (GLTFLoader assigns `extras` → `object.userData`).
function resolveExtras(mesh: THREE.Object3D): ElementProps | null {
  let obj: THREE.Object3D | null = mesh;
  while (obj) {
    const data = obj.userData as Partial<ElementProps> | undefined;
    if (data && (data.elementId !== undefined || data.parameters)) {
      return {
        elementId: data.elementId,
        category: data.category,
        family: data.family,
        type: data.type,
        parameters: data.parameters
      };
    }
    obj = obj.parent;
  }
  return null;
}
