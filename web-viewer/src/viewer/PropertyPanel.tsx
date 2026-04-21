import { useViewerStore } from "../store";

export function PropertyPanel() {
  const selected = useViewerStore((s) => s.selected);

  if (!selected) {
    return (
      <div style={panelStyle}>
        <div style={{ color: "#888" }}>
          Click on a component to see its properties.
        </div>
      </div>
    );
  }

  const { elementId, category, family, type, parameters } = selected;
  return (
    <div style={panelStyle}>
      <h3 style={{ margin: "0 0 12px" }}>{category ?? "Element"}</h3>
      <Row label="Element ID" value={elementId} />
      <Row label="Family" value={family} />
      <Row label="Type" value={type} />
      {parameters && Object.keys(parameters).length > 0 && (
        <>
          <h4 style={{ margin: "16px 0 8px", color: "#9cf" }}>Parameters</h4>
          <div style={{ maxHeight: "50vh", overflowY: "auto" }}>
            {Object.entries(parameters).map(([k, v]) => (
              <Row key={k} label={k} value={v} />
            ))}
          </div>
        </>
      )}
    </div>
  );
}

function Row({ label, value }: { label: string; value: unknown }) {
  if (value === undefined || value === null || value === "") return null;
  return (
    <div
      style={{
        display: "flex",
        fontSize: 13,
        padding: "3px 0",
        borderBottom: "1px solid #333",
      }}
    >
      <div style={{ flex: "0 0 42%", color: "#aaa" }}>{label}</div>
      <div style={{ flex: 1, wordBreak: "break-all" }}>{String(value)}</div>
    </div>
  );
}

const panelStyle: React.CSSProperties = {
  position: "absolute",
  top: 12,
  right: 12,
  width: 320,
  maxHeight: "calc(100vh - 24px)",
  padding: 14,
  background: "rgba(30, 31, 34, 0.92)",
  border: "1px solid #333",
  borderRadius: 8,
  backdropFilter: "blur(6px)",
};
