import { useViewerStore } from "../store";
import type { SceneManager } from "./SceneManager";

interface Props {
  sceneManager: SceneManager | null;
}

export function LayerPanel({ sceneManager }: Props) {
  const layers = useViewerStore((s) => s.layers);
  const visibility = useViewerStore((s) => s.layerVisibility);
  const setVisibility = useViewerStore((s) => s.setLayerVisibility);

  if (layers.length === 0) return null;

  const allOn = layers.every((l) => visibility[l.name] !== false);
  const setAll = (on: boolean) => {
    for (const l of layers) {
      setVisibility(l.name, on);
      sceneManager?.setLayerVisibility(l.name, on);
    }
  };

  return (
    <div style={panelStyle}>
      <div
        style={{
          display: "flex",
          justifyContent: "space-between",
          alignItems: "center",
          marginBottom: 8,
        }}
      >
        <h3 style={{ margin: 0 }}>Layers ({layers.length})</h3>
        <button style={smallBtn} onClick={() => setAll(!allOn)}>
          {allOn ? "Hide all" : "Show all"}
        </button>
      </div>
      <div style={{ maxHeight: "40vh", overflowY: "auto" }}>
        {layers.map((l) => {
          const on = visibility[l.name] !== false;
          return (
            <label key={l.name} style={rowStyle}>
              <input
                type="checkbox"
                checked={on}
                onChange={(e) => {
                  setVisibility(l.name, e.target.checked);
                  sceneManager?.setLayerVisibility(l.name, e.target.checked);
                }}
              />
              {l.color && (
                <span
                  style={{
                    display: "inline-block",
                    width: 10,
                    height: 10,
                    background: `rgb(${l.color[0]},${l.color[1]},${l.color[2]})`,
                    border: "1px solid #333",
                  }}
                />
              )}
              <span style={{ wordBreak: "break-all" }}>{l.name}</span>
            </label>
          );
        })}
      </div>
    </div>
  );
}

const panelStyle: React.CSSProperties = {
  position: "absolute",
  bottom: 60,
  right: 12,
  width: 240,
  padding: 12,
  background: "rgba(30, 31, 34, 0.92)",
  border: "1px solid #333",
  borderRadius: 8,
  backdropFilter: "blur(6px)",
  fontSize: 13,
};

const rowStyle: React.CSSProperties = {
  display: "flex",
  alignItems: "center",
  gap: 6,
  padding: "3px 0",
  cursor: "pointer",
};

const smallBtn: React.CSSProperties = {
  padding: "2px 8px",
  background: "#2d3",
  color: "#0a0a0a",
  border: 0,
  borderRadius: 3,
  cursor: "pointer",
  fontSize: 11,
};
