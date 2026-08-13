import { create } from "zustand";
import type { RenderStats } from "./viewer/SceneManager";

export type { RenderStats };

export type CameraMode = "perspective" | "orthographic";

export interface ElementProps {
  elementId?: number;
  category?: string;
  family?: string;
  type?: string;
  layer?: string;
  parameters?: Record<string, unknown>;
}

export interface LayerInfo {
  name: string;
  color?: [number, number, number];
}

interface ViewerState {
  progress: number;
  loading: boolean;
  selected: ElementProps | null;
  stats: RenderStats | null;
  sectionBoxEnabled: boolean;
  wireframeEnabled: boolean;
  cameraMode: CameraMode;
  hiddenCount: number;
  layers: LayerInfo[];
  layerVisibility: Record<string, boolean>;
  setProgress: (p: number) => void;
  setLoading: (b: boolean) => void;
  setSelected: (p: ElementProps | null) => void;
  setStats: (s: RenderStats) => void;
  setSectionBoxEnabled: (b: boolean) => void;
  setWireframeEnabled: (b: boolean) => void;
  setCameraMode: (m: CameraMode) => void;
  setHiddenCount: (n: number) => void;
  setLayers: (layers: LayerInfo[]) => void;
  setLayerVisibility: (name: string, visible: boolean) => void;
}

export const useViewerStore = create<ViewerState>((set) => ({
  progress: 0,
  loading: false,
  selected: null,
  stats: null,
  sectionBoxEnabled: false,
  wireframeEnabled: false,
  cameraMode: "perspective",
  hiddenCount: 0,
  layers: [],
  layerVisibility: {},
  setProgress: (progress) => set({ progress }),
  setLoading: (loading) => set({ loading }),
  setSelected: (selected) => set({ selected }),
  setStats: (stats) => set({ stats }),
  setSectionBoxEnabled: (sectionBoxEnabled) => set({ sectionBoxEnabled }),
  setWireframeEnabled: (wireframeEnabled) => set({ wireframeEnabled }),
  setCameraMode: (cameraMode) => set({ cameraMode }),
  setHiddenCount: (hiddenCount) => set({ hiddenCount }),
  setLayers: (layers) =>
    set({
      layers,
      layerVisibility: Object.fromEntries(layers.map((l) => [l.name, true])),
    }),
  setLayerVisibility: (name, visible) =>
    set((s) => ({
      layerVisibility: { ...s.layerVisibility, [name]: visible },
    })),
}));
