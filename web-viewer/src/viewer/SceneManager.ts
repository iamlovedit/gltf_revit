import * as THREE from "three";
import { OrbitControls } from "three/examples/jsm/controls/OrbitControls.js";
import { GLTF } from "three/examples/jsm/loaders/GLTFLoader.js";

export type PickCallback = (mesh: THREE.Mesh | null) => void;

export interface RenderStats {
  fps: number;
  frameTimeMs: number;
  triangles: number;
  drawCalls: number;
}

export type StatsCallback = (stats: RenderStats) => void;

const FRAME_SAMPLE_WINDOW = 30;
const STATS_EMIT_INTERVAL_MS = 250;

export class SceneManager {
  readonly scene = new THREE.Scene();
  readonly camera: THREE.PerspectiveCamera;
  readonly renderer: THREE.WebGLRenderer;
  private readonly controls: OrbitControls;
  private readonly raycaster = new THREE.Raycaster();
  private readonly pointer = new THREE.Vector2();
  private disposed = false;
  private resizeObserver: ResizeObserver | null = null;
  private pickCallback: PickCallback | null = null;
  private statsCallback: StatsCallback | null = null;
  private readonly frameSamples: number[] = [];
  private lastStatsEmitAt = 0;
  private needsRender = true;
  private highlighted: THREE.Mesh | null = null;
  private originalMaterial: THREE.Material | THREE.Material[] | null = null;
  private readonly highlightColor = new THREE.Color(0xffa500);

  constructor(private readonly container: HTMLElement) {
    this.scene.background = new THREE.Color(0x1e1f22);

    const { clientWidth: w, clientHeight: h } = container;
    this.camera = new THREE.PerspectiveCamera(50, w / h, 0.1, 10_000);
    this.camera.position.set(20, 20, 20);

    this.renderer = new THREE.WebGLRenderer({
      antialias: true,
      powerPreference: "high-performance",
    });
    this.renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
    this.renderer.setSize(w, h, false);
    this.renderer.outputColorSpace = THREE.SRGBColorSpace;
    container.appendChild(this.renderer.domElement);

    this.controls = new OrbitControls(this.camera, this.renderer.domElement);
    this.controls.enableDamping = true;
    // OrbitControls dispatches 'change' from inside update(), including the
    // update() it calls synchronously from its own wheel handler. Listening
    // here is what makes wheel-driven zoom visible under on-demand rendering.
    this.controls.addEventListener("change", this.invalidate);

    this.scene.add(new THREE.HemisphereLight(0xffffff, 0x222233, 0.9));
    const dir = new THREE.DirectionalLight(0xffffff, 0.8);
    dir.position.set(30, 50, 30);
    this.scene.add(dir);

    this.resizeObserver = new ResizeObserver(() => this.resize());
    this.resizeObserver.observe(container);

    this.renderer.domElement.addEventListener(
      "pointerdown",
      this.onPointerDown,
    );
    this.renderer.setAnimationLoop(this.tick);
  }

  onPick(cb: PickCallback) {
    this.pickCallback = cb;
  }

  onStats(cb: StatsCallback) {
    this.statsCallback = cb;
  }

  invalidate = () => {
    this.needsRender = true;
  };

  // Adds the gltf to the scene but keeps meshes hidden and reveals them in
  // batches. Between batches we await renderer.compileAsync so WebGL programs
  // are compiled ahead of the first render of each batch, spreading the
  // shader-compile cost across short tasks instead of one multi-second stall.
  async addGltfProgressively(gltf: GLTF) {
    const meshes: THREE.Mesh[] = [];
    gltf.scene.traverse((o) => {
      if ((o as THREE.Mesh).isMesh) meshes.push(o as THREE.Mesh);
    });

    const prevVisibility = meshes.map((m) => m.visible);
    for (const m of meshes) m.visible = false;
    this.scene.add(gltf.scene);

    const BATCH = 150;
    for (let i = 0; i < meshes.length; i += BATCH) {
      if (this.disposed) return;
      const end = Math.min(i + BATCH, meshes.length);
      for (let j = i; j < end; j++) meshes[j].visible = prevVisibility[j];
      // compileAsync traverses visible objects; hidden batches are skipped and
      // already-compiled programs are a no-op, so each call only pays for the
      // newly-revealed slice.
      await this.renderer.compileAsync(this.scene, this.camera, this.scene);
      this.invalidate();
      await new Promise<void>((r) => requestAnimationFrame(() => r()));
    }

    if (this.disposed) return;
    this.frameSelection();
    this.invalidate();
  }

  private frameSelection() {
    const box = new THREE.Box3().setFromObject(this.scene);
    if (!isFinite(box.min.x)) return;
    const size = box.getSize(new THREE.Vector3());
    const center = box.getCenter(new THREE.Vector3());
    const maxDim = Math.max(size.x, size.y, size.z);
    const dist = maxDim * 1.8;
    this.camera.position
      .copy(center)
      .add(new THREE.Vector3(dist, dist * 0.8, dist));
    this.camera.near = Math.max(0.01, maxDim / 1000);
    this.camera.far = maxDim * 20;
    this.camera.updateProjectionMatrix();
    this.controls.target.copy(center);
    this.controls.update();
  }

  private tick = () => {
    if (this.disposed) return;
    // update() drives damping and fires 'change' when the camera actually
    // moves; the listener flips needsRender for us, so we only need to check
    // the flag here.
    this.controls.update();
    if (!this.needsRender) return;
    const t0 = performance.now();
    this.renderer.render(this.scene, this.camera);
    const dt = performance.now() - t0;
    this.needsRender = false;

    this.frameSamples.push(dt);
    if (this.frameSamples.length > FRAME_SAMPLE_WINDOW) this.frameSamples.shift();
    if (this.statsCallback && t0 - this.lastStatsEmitAt > STATS_EMIT_INTERVAL_MS) {
      const sum = this.frameSamples.reduce((a, b) => a + b, 0);
      const avg = sum / this.frameSamples.length;
      this.statsCallback({
        fps: avg > 0 ? 1000 / avg : 0,
        frameTimeMs: avg,
        triangles: this.renderer.info.render.triangles,
        drawCalls: this.renderer.info.render.calls,
      });
      this.lastStatsEmitAt = t0;
    }
  };

  private resize = () => {
    const { clientWidth: w, clientHeight: h } = this.container;
    if (w === 0 || h === 0) return;
    this.renderer.setSize(w, h, false);
    this.camera.aspect = w / h;
    this.camera.updateProjectionMatrix();
    this.invalidate();
  };

  private onPointerDown = (ev: PointerEvent) => {
    if (!this.pickCallback) return;
    const rect = this.renderer.domElement.getBoundingClientRect();
    this.pointer.x = ((ev.clientX - rect.left) / rect.width) * 2 - 1;
    this.pointer.y = -((ev.clientY - rect.top) / rect.height) * 2 + 1;
    this.raycaster.setFromCamera(this.pointer, this.camera);
    const hits = this.raycaster.intersectObjects(this.scene.children, true);
    const mesh = hits.find((h) => (h.object as THREE.Mesh).isMesh)?.object as
      | THREE.Mesh
      | undefined;
    this.setHighlighted(mesh ?? null);
    this.pickCallback(mesh ?? null);
  };

  // Swap the picked mesh's material for a tinted clone so the selection is
  // visible. The clone is scoped to this one mesh, so shared materials on
  // other meshes are unaffected. Restoring pops the original back.
  private setHighlighted(mesh: THREE.Mesh | null) {
    if (this.highlighted === mesh) return;
    if (this.highlighted && this.originalMaterial) {
      const current = this.highlighted.material as
        | THREE.Material
        | THREE.Material[];
      if (Array.isArray(current)) current.forEach((m) => m.dispose());
      else current.dispose();
      this.highlighted.material = this.originalMaterial;
    }
    this.highlighted = null;
    this.originalMaterial = null;

    if (mesh) {
      this.originalMaterial = mesh.material as
        | THREE.Material
        | THREE.Material[];
      mesh.material = Array.isArray(this.originalMaterial)
        ? this.originalMaterial.map((m) => this.tintClone(m))
        : this.tintClone(this.originalMaterial);
      this.highlighted = mesh;
    }
    this.invalidate();
  }

  private tintClone(material: THREE.Material): THREE.Material {
    const clone = material.clone();
    const withColor = clone as THREE.Material & {
      color?: THREE.Color;
      emissive?: THREE.Color;
      emissiveIntensity?: number;
    };
    if (withColor.color) withColor.color.copy(this.highlightColor);
    if (withColor.emissive) {
      withColor.emissive.copy(this.highlightColor);
      if (withColor.emissiveIntensity !== undefined) {
        withColor.emissiveIntensity = 0.4;
      }
    }
    return clone;
  }

  dispose() {
    this.disposed = true;
    this.statsCallback = null;
    this.setHighlighted(null);
    this.renderer.setAnimationLoop(null);
    this.controls.removeEventListener("change", this.invalidate);
    this.renderer.domElement.removeEventListener(
      "pointerdown",
      this.onPointerDown,
    );
    this.resizeObserver?.disconnect();
    this.scene.traverse((obj) => {
      const mesh = obj as THREE.Mesh;
      mesh.geometry?.dispose?.();
      const mat = mesh.material as
        | THREE.Material
        | THREE.Material[]
        | undefined;
      if (Array.isArray(mat)) mat.forEach((m) => m.dispose());
      else mat?.dispose?.();
    });
    this.renderer.dispose();
    this.renderer.domElement.remove();
  }
}
