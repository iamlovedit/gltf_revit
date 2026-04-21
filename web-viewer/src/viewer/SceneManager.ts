import * as THREE from "three";
import { OrbitControls } from "three/examples/jsm/controls/OrbitControls.js";
import { GLTF } from "three/examples/jsm/loaders/GLTFLoader.js";
import { SectionBoxController } from "./SectionBoxController";

export type PickCallback = (mesh: THREE.Mesh | null) => void;

export interface RenderStats {
  fps: number;
  frameTimeMs: number;
  triangles: number;
  drawCalls: number;
}

export type StatsCallback = (stats: RenderStats) => void;

export type StandardView =
  | "front"
  | "back"
  | "left"
  | "right"
  | "top"
  | "bottom"
  | "iso";

export type CameraMode = "perspective" | "orthographic";

const FRAME_SAMPLE_WINDOW = 30;
const STATS_EMIT_INTERVAL_MS = 250;

const INITIAL_CAM_POS = new THREE.Vector3(20, 20, 20);
const INITIAL_TARGET = new THREE.Vector3(0, 0, 0);

const STANDARD_VIEW_DIRS: Record<StandardView, THREE.Vector3> = {
  front: new THREE.Vector3(0, 0, 1),
  back: new THREE.Vector3(0, 0, -1),
  right: new THREE.Vector3(1, 0, 0),
  left: new THREE.Vector3(-1, 0, 0),
  top: new THREE.Vector3(0.001, 1, 0.001),
  bottom: new THREE.Vector3(0.001, -1, 0.001),
  iso: new THREE.Vector3(1, 0.9, 1).normalize(),
};

export class SceneManager {
  readonly scene = new THREE.Scene();
  readonly renderer: THREE.WebGLRenderer;
  readonly sectionBox: SectionBoxController;
  camera: THREE.PerspectiveCamera | THREE.OrthographicCamera;
  private perspCamera: THREE.PerspectiveCamera;
  private cameraMode: CameraMode = "perspective";
  private controls: OrbitControls;
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
  private readonly hiddenSet = new Set<THREE.Mesh>();
  private wireframeEnabled = false;
  private hiddenChangeCallback: ((n: number) => void) | null = null;

  constructor(private readonly container: HTMLElement) {
    this.scene.background = new THREE.Color(0x1e1f22);

    const { clientWidth: w, clientHeight: h } = container;
    this.perspCamera = new THREE.PerspectiveCamera(50, w / h, 0.1, 10_000);
    this.perspCamera.position.copy(INITIAL_CAM_POS);
    this.camera = this.perspCamera;

    this.renderer = new THREE.WebGLRenderer({
      antialias: true,
      powerPreference: "high-performance",
    });
    this.renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
    this.renderer.setSize(w, h, false);
    this.renderer.outputColorSpace = THREE.SRGBColorSpace;
    container.appendChild(this.renderer.domElement);

    this.controls = this.createControls(this.camera);
    this.controls.target.copy(INITIAL_TARGET);

    this.scene.add(new THREE.HemisphereLight(0xffffff, 0x222233, 0.9));
    const dir = new THREE.DirectionalLight(0xffffff, 0.8);
    dir.position.set(30, 50, 30);
    this.scene.add(dir);

    this.sectionBox = new SectionBoxController();
    this.sectionBox.attach({
      scene: this.scene,
      renderer: this.renderer,
      getCamera: () => this.camera,
      domElement: this.renderer.domElement,
      setControlsEnabled: (enabled) => {
        this.controls.enabled = enabled;
      },
      invalidate: this.invalidate,
    });

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

  onHiddenChange(cb: (n: number) => void) {
    this.hiddenChangeCallback = cb;
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
      // If the section box has ever been activated, propagate its clipping
      // planes to materials of newly-revealed meshes so the cut stays
      // consistent across streamed batches.
      this.sectionBox.applyToNewMeshes(gltf.scene);
      if (this.wireframeEnabled) this.applyWireframe(gltf.scene, true);
      this.invalidate();
      await new Promise<void>((r) => requestAnimationFrame(() => r()));
    }

    if (this.disposed) return;
    this.frameSelection();
    this.invalidate();
  }

  fitToScreen() {
    this.frameSelection();
    this.invalidate();
  }

  resetView() {
    if (this.cameraMode === "orthographic") this.setCameraMode("perspective");
    this.perspCamera.position.copy(INITIAL_CAM_POS);
    this.controls.target.copy(INITIAL_TARGET);
    this.perspCamera.updateProjectionMatrix();
    this.controls.update();
    this.invalidate();
  }

  setWireframe(on: boolean) {
    this.wireframeEnabled = on;
    this.applyWireframe(this.scene, on);
    this.invalidate();
  }

  private applyWireframe(root: THREE.Object3D, on: boolean) {
    root.traverse((obj) => {
      const mesh = obj as THREE.Mesh;
      if (!mesh.isMesh) return;
      const mat = mesh.material as THREE.Material | THREE.Material[];
      if (Array.isArray(mat)) {
        for (const m of mat) (m as THREE.MeshBasicMaterial).wireframe = on;
      } else if (mat) {
        (mat as THREE.MeshBasicMaterial).wireframe = on;
      }
    });
  }

  hideSelected() {
    if (!this.highlighted) return;
    const target = this.highlighted;
    // Clear the picked highlight first so the original material is restored
    // before we flip visibility; otherwise the disposed clone would hang on.
    this.setHighlighted(null);
    target.visible = false;
    this.hiddenSet.add(target);
    this.emitHidden();
    this.invalidate();
  }

  isolateSelected() {
    if (!this.highlighted) return;
    const keep = this.highlighted;
    this.setHighlighted(null);
    this.scene.traverse((obj) => {
      const mesh = obj as THREE.Mesh;
      if (!mesh.isMesh || mesh === keep) return;
      if (mesh.visible) {
        mesh.visible = false;
        this.hiddenSet.add(mesh);
      }
    });
    this.emitHidden();
    this.invalidate();
  }

  showAll() {
    for (const m of this.hiddenSet) m.visible = true;
    this.hiddenSet.clear();
    this.emitHidden();
    this.invalidate();
  }

  setStandardView(view: StandardView) {
    const box = new THREE.Box3().setFromObject(this.scene);
    if (!isFinite(box.min.x) || box.isEmpty()) return;
    const size = box.getSize(new THREE.Vector3());
    const center = box.getCenter(new THREE.Vector3());
    const maxDim = Math.max(size.x, size.y, size.z);
    const dist = maxDim * 1.8;
    const dir = STANDARD_VIEW_DIRS[view].clone().normalize();
    this.camera.position.copy(center).addScaledVector(dir, dist);
    if (this.camera instanceof THREE.PerspectiveCamera) {
      this.camera.near = Math.max(0.01, maxDim / 1000);
      this.camera.far = maxDim * 20;
    }
    this.camera.updateProjectionMatrix();
    this.controls.target.copy(center);
    this.controls.update();
    this.invalidate();
  }

  setCameraMode(mode: CameraMode) {
    if (mode === this.cameraMode) return;
    const { clientWidth: w, clientHeight: h } = this.container;
    const aspect = w / h;
    const target = this.controls.target.clone();
    const position = this.camera.position.clone();
    const quaternion = this.camera.quaternion.clone();

    let nextCamera: THREE.PerspectiveCamera | THREE.OrthographicCamera;
    if (mode === "orthographic") {
      const dist = position.distanceTo(target);
      const halfH =
        Math.tan(THREE.MathUtils.degToRad(this.perspCamera.fov) / 2) *
        Math.max(dist, 1e-3);
      const halfW = halfH * aspect;
      const box = new THREE.Box3().setFromObject(this.scene);
      const maxDim = box.isEmpty()
        ? 200
        : box.getSize(new THREE.Vector3()).length();
      const ortho = new THREE.OrthographicCamera(
        -halfW,
        halfW,
        halfH,
        -halfH,
        -maxDim * 20,
        maxDim * 20,
      );
      ortho.position.copy(position);
      ortho.quaternion.copy(quaternion);
      ortho.zoom = 1;
      ortho.updateProjectionMatrix();
      nextCamera = ortho;
    } else {
      this.perspCamera.position.copy(position);
      this.perspCamera.quaternion.copy(quaternion);
      this.perspCamera.aspect = aspect;
      this.perspCamera.updateProjectionMatrix();
      nextCamera = this.perspCamera;
    }

    // OrbitControls stores camera-specific spherical state, so swap by
    // disposing the old one and binding a fresh instance to the new camera.
    this.controls.removeEventListener("change", this.invalidate);
    this.controls.dispose();
    this.camera = nextCamera;
    this.controls = this.createControls(nextCamera);
    this.controls.target.copy(target);
    this.controls.update();
    this.cameraMode = mode;
    this.invalidate();
  }

  getCameraMode(): CameraMode {
    return this.cameraMode;
  }

  enableSectionBox() {
    this.sectionBox.enable();
  }

  disableSectionBox() {
    this.sectionBox.disable();
  }

  private createControls(
    camera: THREE.PerspectiveCamera | THREE.OrthographicCamera,
  ): OrbitControls {
    const controls = new OrbitControls(camera, this.renderer.domElement);
    controls.enableDamping = true;
    // OrbitControls dispatches 'change' from inside update(), including the
    // update() it calls synchronously from its own wheel handler. Listening
    // here is what makes wheel-driven zoom visible under on-demand rendering.
    controls.addEventListener("change", this.invalidate);
    return controls;
  }

  private emitHidden() {
    this.hiddenChangeCallback?.(this.hiddenSet.size);
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
    if (this.camera instanceof THREE.PerspectiveCamera) {
      this.camera.near = Math.max(0.01, maxDim / 1000);
      this.camera.far = maxDim * 20;
    }
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
    const aspect = w / h;
    if (this.camera instanceof THREE.PerspectiveCamera) {
      this.camera.aspect = aspect;
    } else {
      // Preserve vertical extent, recompute horizontal from new aspect.
      const halfH = (this.camera.top - this.camera.bottom) / 2;
      this.camera.left = -halfH * aspect;
      this.camera.right = halfH * aspect;
      this.camera.top = halfH;
      this.camera.bottom = -halfH;
    }
    this.camera.updateProjectionMatrix();
    this.invalidate();
  };

  private onPointerDown = (ev: PointerEvent) => {
    // Let the section box handle take precedence when a face handle is hit;
    // if so, skip normal picking for this event.
    if (this.sectionBox.tryStartDrag(ev)) return;
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
    this.sectionBox.dispose();
    this.renderer.setAnimationLoop(null);
    this.controls.removeEventListener("change", this.invalidate);
    this.controls.dispose();
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
