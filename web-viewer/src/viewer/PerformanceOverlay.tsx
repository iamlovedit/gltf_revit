import { useViewerStore } from "../store";

export function PerformanceOverlay() {
  const stats = useViewerStore((s) => s.stats);

  const fps = stats ? Math.round(stats.fps) : null;
  const frameTime = stats ? stats.frameTimeMs.toFixed(1) : null;
  const triangles = stats ? stats.triangles.toLocaleString() : null;
  const drawCalls = stats ? stats.drawCalls.toLocaleString() : null;

  const fpsColor =
    fps === null ? "#888" : fps >= 50 ? "#4ade80" : fps >= 30 ? "#fbbf24" : "#f87171";

  return (
    <div style={overlayStyle}>
      <span style={{ color: fpsColor, fontWeight: 600 }}>
        {fps ?? "—"} FPS
      </span>
      <span style={sep}>·</span>
      <span>{frameTime ?? "—"} ms</span>
      <span style={sep}>·</span>
      <span>{triangles ?? "—"} tris</span>
      <span style={sep}>·</span>
      <span>{drawCalls ?? "—"} calls</span>
    </div>
  );
}

const overlayStyle: React.CSSProperties = {
  position: "absolute",
  top: 12,
  right: 12,
  display: "flex",
  alignItems: "center",
  gap: 6,
  padding: "6px 12px",
  fontFamily:
    "ui-monospace, SFMono-Regular, Menlo, Consolas, 'Liberation Mono', monospace",
  fontSize: 12,
  color: "#ddd",
  background: "rgba(30, 31, 34, 0.92)",
  border: "1px solid #333",
  borderRadius: 8,
  backdropFilter: "blur(6px)",
  pointerEvents: "none",
};

const sep: React.CSSProperties = { color: "#555" };
