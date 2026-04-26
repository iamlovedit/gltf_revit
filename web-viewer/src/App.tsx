import { useCallback, useState } from "react";
import { Viewer } from "./viewer/Viewer";
import { PropertyPanel } from "./viewer/PropertyPanel";
import { LayerPanel } from "./viewer/LayerPanel";
import { PerformanceOverlay } from "./viewer/PerformanceOverlay";
import { BottomToolbar } from "./viewer/BottomToolbar";
import { SceneManager } from "./viewer/SceneManager";
import { useViewerStore } from "./store";

export default function App() {
  const [url, setUrl] = useState<string | null>(null);
  const [sceneManager, setSceneManager] = useState<SceneManager | null>(null);
  const loading = useViewerStore((s) => s.loading);
  const progress = useViewerStore((s) => s.progress);

  // Stable identity so Viewer's effect doesn't tear down the renderer on
  // every App render.
  const onReady = useCallback((mgr: SceneManager | null) => {
    setSceneManager(mgr);
  }, []);

  const onPick = (e: React.ChangeEvent<HTMLInputElement>) => {
    const f = e.target.files?.[0];
    if (!f) return;
    // URL.createObjectURL is synchronous; fetch() later will stream from it.
    if (url) URL.revokeObjectURL(url);
    setUrl(URL.createObjectURL(f));
  };

  return (
    <div style={{ position: "relative", width: "100vw", height: "100vh" }}>
      <Viewer url={url} onReady={onReady} />

      <div style={toolbar}>
        <label style={button}>
          Open .glb
          <input
            type="file"
            accept=".glb"
            onChange={onPick}
            style={{ display: "none" }}
          />
        </label>
        {loading && (
          <div style={{ marginLeft: 12 }}>
            Loading {(progress * 100).toFixed(0)}%
          </div>
        )}
      </div>

      <PerformanceOverlay />
      <PropertyPanel />
      <LayerPanel sceneManager={sceneManager} />
      <BottomToolbar sceneManager={sceneManager} />
    </div>
  );
}

const toolbar: React.CSSProperties = {
  position: "absolute",
  top: 12,
  left: 12,
  display: "flex",
  alignItems: "center",
  padding: "8px 12px",
  background: "rgba(30, 31, 34, 0.92)",
  border: "1px solid #333",
  borderRadius: 8,
};

const button: React.CSSProperties = {
  padding: "6px 12px",
  background: "#2d7",
  color: "#0a0a0a",
  fontWeight: 600,
  borderRadius: 4,
  cursor: "pointer",
};
