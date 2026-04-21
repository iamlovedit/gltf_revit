import { create } from "zustand";
import type { RenderStats } from "./viewer/SceneManager";

export type { RenderStats };

export interface ElementProps {
  elementId?: number;
  category?: string;
  family?: string;
  type?: string;
  parameters?: Record<string, unknown>;
}

interface ViewerState {
  progress: number;
  loading: boolean;
  selected: ElementProps | null;
  stats: RenderStats | null;
  setProgress: (p: number) => void;
  setLoading: (b: boolean) => void;
  setSelected: (p: ElementProps | null) => void;
  setStats: (s: RenderStats) => void;
}

export const useViewerStore = create<ViewerState>((set) => ({
  progress: 0,
  loading: false,
  selected: null,
  stats: null,
  setProgress: (progress) => set({ progress }),
  setLoading: (loading) => set({ loading }),
  setSelected: (selected) => set({ selected }),
  setStats: (stats) => set({ stats }),
}));
