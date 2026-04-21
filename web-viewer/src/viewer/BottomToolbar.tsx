import { useState } from "react";
import { SceneManager, StandardView } from "./SceneManager";
import { useViewerStore } from "../store";

interface Props {
  sceneManager: SceneManager | null;
}

const STANDARD_VIEW_LABELS: Array<[StandardView, string]> = [
  ["front", "前"],
  ["back", "后"],
  ["left", "左"],
  ["right", "右"],
  ["top", "顶"],
  ["bottom", "底"],
  ["iso", "轴测"],
];

export function BottomToolbar({ sceneManager }: Props) {
  const sectionBoxEnabled = useViewerStore((s) => s.sectionBoxEnabled);
  const wireframeEnabled = useViewerStore((s) => s.wireframeEnabled);
  const cameraMode = useViewerStore((s) => s.cameraMode);
  const selected = useViewerStore((s) => s.selected);
  const hiddenCount = useViewerStore((s) => s.hiddenCount);
  const setSectionBoxEnabled = useViewerStore((s) => s.setSectionBoxEnabled);
  const setWireframeEnabled = useViewerStore((s) => s.setWireframeEnabled);
  const setCameraMode = useViewerStore((s) => s.setCameraMode);
  const [viewMenuOpen, setViewMenuOpen] = useState(false);

  const disabled = !sceneManager;
  const hasSelection = !!selected;

  const onFit = () => sceneManager?.fitToScreen();
  const onReset = () => {
    sceneManager?.resetView();
    setCameraMode("perspective");
  };
  const onToggleSectionBox = () => {
    if (!sceneManager) return;
    const next = !sectionBoxEnabled;
    if (next) sceneManager.enableSectionBox();
    else sceneManager.disableSectionBox();
    setSectionBoxEnabled(next);
  };
  const onToggleWireframe = () => {
    if (!sceneManager) return;
    const next = !wireframeEnabled;
    sceneManager.setWireframe(next);
    setWireframeEnabled(next);
  };
  const onToggleCameraMode = () => {
    if (!sceneManager) return;
    const next = cameraMode === "perspective" ? "orthographic" : "perspective";
    sceneManager.setCameraMode(next);
    setCameraMode(next);
  };
  const onStandardView = (v: StandardView) => {
    sceneManager?.setStandardView(v);
    setViewMenuOpen(false);
  };
  const onHide = () => sceneManager?.hideSelected();
  const onIsolate = () => sceneManager?.isolateSelected();
  const onShowAll = () => sceneManager?.showAll();

  return (
    <div style={toolbar}>
      <Group label="视图">
        <Button onClick={onFit} disabled={disabled} icon="🔳" label="适应屏幕" />
        <Button onClick={onReset} disabled={disabled} icon="↺" label="重置视角" />
        <div style={{ position: "relative" }}>
          <Button
            onClick={() => setViewMenuOpen((v) => !v)}
            disabled={disabled}
            icon="📐"
            label={`标准视图 ${viewMenuOpen ? "▾" : "▴"}`}
            active={viewMenuOpen}
          />
          {viewMenuOpen && (
            <div style={popupMenu}>
              {STANDARD_VIEW_LABELS.map(([key, label]) => (
                <button
                  key={key}
                  style={popupItem}
                  onClick={() => onStandardView(key)}
                >
                  {label}
                </button>
              ))}
            </div>
          )}
        </div>
        <Button
          onClick={onToggleCameraMode}
          disabled={disabled}
          icon={cameraMode === "perspective" ? "🎬" : "▦"}
          label={cameraMode === "perspective" ? "透视" : "正交"}
        />
      </Group>

      <Divider />

      <Group label="显示">
        <Button
          onClick={onToggleWireframe}
          disabled={disabled}
          icon="📏"
          label="线框"
          active={wireframeEnabled}
        />
        <Button
          onClick={onHide}
          disabled={disabled || !hasSelection}
          icon="👁"
          label="隐藏选中"
        />
        <Button
          onClick={onIsolate}
          disabled={disabled || !hasSelection}
          icon="◉"
          label="隔离"
        />
        <Button
          onClick={onShowAll}
          disabled={disabled || hiddenCount === 0}
          icon="✦"
          label={hiddenCount > 0 ? `显示全部 (${hiddenCount})` : "显示全部"}
        />
      </Group>

      <Divider />

      <Group label="剖切">
        <Button
          onClick={onToggleSectionBox}
          disabled={disabled}
          icon="✂"
          label="剖面框"
          active={sectionBoxEnabled}
        />
      </Group>
    </div>
  );
}

interface ButtonProps {
  onClick: () => void;
  disabled?: boolean;
  icon: string;
  label: string;
  active?: boolean;
}

function Button({ onClick, disabled, icon, label, active }: ButtonProps) {
  const style: React.CSSProperties = {
    ...buttonBase,
    ...(active ? buttonActive : null),
    ...(disabled ? buttonDisabled : null),
  };
  return (
    <button onClick={onClick} disabled={disabled} style={style}>
      <span style={{ fontSize: 14, lineHeight: 1 }}>{icon}</span>
      <span>{label}</span>
    </button>
  );
}

function Group({
  label,
  children,
}: {
  label: string;
  children: React.ReactNode;
}) {
  return (
    <div style={group}>
      <div style={groupLabel}>{label}</div>
      <div style={{ display: "flex", gap: 4 }}>{children}</div>
    </div>
  );
}

function Divider() {
  return <div style={divider} />;
}

const toolbar: React.CSSProperties = {
  position: "absolute",
  bottom: 16,
  left: "50%",
  transform: "translateX(-50%)",
  display: "flex",
  alignItems: "stretch",
  gap: 10,
  padding: "8px 12px",
  background: "rgba(30, 31, 34, 0.92)",
  border: "1px solid #333",
  borderRadius: 10,
  backdropFilter: "blur(6px)",
  color: "#e8e8e8",
  fontFamily: "system-ui, sans-serif",
  fontSize: 12,
  userSelect: "none",
  boxShadow: "0 4px 20px rgba(0,0,0,0.4)",
};

const group: React.CSSProperties = {
  display: "flex",
  flexDirection: "column",
  alignItems: "center",
  gap: 4,
};

const groupLabel: React.CSSProperties = {
  fontSize: 10,
  color: "#888",
  letterSpacing: 1,
};

const divider: React.CSSProperties = {
  width: 1,
  background: "#333",
  margin: "0 2px",
};

const buttonBase: React.CSSProperties = {
  display: "flex",
  alignItems: "center",
  gap: 4,
  padding: "6px 10px",
  background: "#2a2b2e",
  border: "1px solid #3a3b3e",
  borderRadius: 6,
  color: "#e8e8e8",
  cursor: "pointer",
  fontSize: 12,
  fontFamily: "inherit",
};

const buttonActive: React.CSSProperties = {
  background: "#2d7",
  color: "#0a0a0a",
  borderColor: "#2d7",
  fontWeight: 600,
};

const buttonDisabled: React.CSSProperties = {
  opacity: 0.4,
  cursor: "not-allowed",
};

const popupMenu: React.CSSProperties = {
  position: "absolute",
  bottom: "calc(100% + 6px)",
  left: "50%",
  transform: "translateX(-50%)",
  display: "grid",
  gridTemplateColumns: "repeat(3, auto)",
  gap: 4,
  padding: 6,
  background: "rgba(30, 31, 34, 0.96)",
  border: "1px solid #333",
  borderRadius: 6,
  boxShadow: "0 4px 12px rgba(0,0,0,0.5)",
  zIndex: 10,
};

const popupItem: React.CSSProperties = {
  padding: "6px 10px",
  background: "#2a2b2e",
  border: "1px solid #3a3b3e",
  borderRadius: 4,
  color: "#e8e8e8",
  cursor: "pointer",
  fontSize: 12,
  fontFamily: "inherit",
  minWidth: 44,
};
