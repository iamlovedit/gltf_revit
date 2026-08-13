import * as THREE from "three";

type FaceKey = "pX" | "nX" | "pY" | "nY" | "pZ" | "nZ";

interface FaceDef {
  key: FaceKey;
  axisIdx: 0 | 1 | 2;
  side: 1 | -1;
  normal: THREE.Vector3;
}

const FACES: FaceDef[] = [
  { key: "pX", axisIdx: 0, side: 1, normal: new THREE.Vector3(-1, 0, 0) },
  { key: "nX", axisIdx: 0, side: -1, normal: new THREE.Vector3(1, 0, 0) },
  { key: "pY", axisIdx: 1, side: 1, normal: new THREE.Vector3(0, -1, 0) },
  { key: "nY", axisIdx: 1, side: -1, normal: new THREE.Vector3(0, 1, 0) },
  { key: "pZ", axisIdx: 2, side: 1, normal: new THREE.Vector3(0, 0, -1) },
  { key: "nZ", axisIdx: 2, side: -1, normal: new THREE.Vector3(0, 0, 1) },
];

const EDGE_INDICES: Array<[number, number]> = [
  [0, 1],
  [1, 2],
  [2, 3],
  [3, 0],
  [4, 5],
  [5, 6],
  [6, 7],
  [7, 4],
  [0, 4],
  [1, 5],
  [2, 6],
  [3, 7],
];

const INACTIVE_CONSTANT = 1e10;

interface ControllerDeps {
  scene: THREE.Scene;
  renderer: THREE.WebGLRenderer;
  getCamera: () => THREE.Camera;
  domElement: HTMLElement;
  setControlsEnabled: (enabled: boolean) => void;
  invalidate: () => void;
}

export class SectionBoxController {
  readonly planes: THREE.Plane[];
  private readonly gizmoGroup = new THREE.Group();
  private readonly boxLine: THREE.LineSegments;
  private readonly boxPositions: Float32Array;
  private readonly handles: Record<FaceKey, THREE.Mesh>;
  private readonly handleByMesh = new Map<THREE.Mesh, FaceKey>();
  private readonly handleMaterial: THREE.MeshBasicMaterial;
  private readonly min = new THREE.Vector3();
  private readonly max = new THREE.Vector3();
  private readonly raycaster = new THREE.Raycaster();
  private readonly pointer = new THREE.Vector2();
  private handleRadius = 0.5;
  private hasMaterialAssignment = false;
  private enabled = false;
  private dragging: FaceKey | null = null;
  private dragStartValue = 0;
  private dragStartAxisParam = 0;
  private deps: ControllerDeps | null = null;

  constructor() {
    this.planes = FACES.map(
      (f) => new THREE.Plane(f.normal.clone(), INACTIVE_CONSTANT),
    );

    const edgeGeom = new THREE.BufferGeometry();
    this.boxPositions = new Float32Array(EDGE_INDICES.length * 2 * 3);
    edgeGeom.setAttribute(
      "position",
      new THREE.BufferAttribute(this.boxPositions, 3),
    );
    const edgeMat = new THREE.LineBasicMaterial({
      color: 0xffa500,
      depthTest: false,
      transparent: true,
      opacity: 0.85,
      clippingPlanes: [],
    });
    this.boxLine = new THREE.LineSegments(edgeGeom, edgeMat);
    this.boxLine.renderOrder = 999;
    this.gizmoGroup.add(this.boxLine);

    this.handleMaterial = new THREE.MeshBasicMaterial({
      color: 0xffa500,
      depthTest: false,
      transparent: true,
      opacity: 0.9,
      clippingPlanes: [],
    });

    const sphereGeom = new THREE.SphereGeometry(1, 16, 12);
    const handles = {} as Record<FaceKey, THREE.Mesh>;
    for (const f of FACES) {
      const mesh = new THREE.Mesh(sphereGeom, this.handleMaterial);
      mesh.renderOrder = 1000;
      mesh.userData.faceKey = f.key;
      this.gizmoGroup.add(mesh);
      handles[f.key] = mesh;
      this.handleByMesh.set(mesh, f.key);
    }
    this.handles = handles;
    this.gizmoGroup.visible = false;
  }

  isEnabled() {
    return this.enabled;
  }

  attach(deps: ControllerDeps) {
    this.deps = deps;
    deps.scene.add(this.gizmoGroup);
  }

  // First-time enable: compute AABB from scene, assign clippingPlanes to all
  // materials, turn on localClippingEnabled. On subsequent enables we only
  // restore active constants — the array assignment and renderer flag stay
  // permanent so we never trigger shader recompilation on toggle.
  enable() {
    const deps = this.deps;
    if (!deps || this.enabled) return;

    if (!this.hasMaterialAssignment) {
      deps.scene.updateMatrixWorld(true);
      const box = new THREE.Box3().setFromObject(deps.scene);
      if (!isFinite(box.min.x) || box.isEmpty()) return;
      // Tiny inflation so the initial box doesn't appear to clip the model.
      const pad = box.getSize(new THREE.Vector3()).length() * 0.002;
      this.min.copy(box.min).subScalar(pad);
      this.max.copy(box.max).addScalar(pad);
      this.handleRadius = Math.max(
        0.2,
        box.getSize(new THREE.Vector3()).length() * 0.012,
      );
      for (const key of Object.keys(this.handles) as FaceKey[]) {
        this.handles[key].scale.setScalar(this.handleRadius);
      }
      deps.renderer.localClippingEnabled = true;
      this.assignPlanesTo(deps.scene);
      this.hasMaterialAssignment = true;
    }

    this.syncPlaneConstants();
    this.updateGizmo();
    this.gizmoGroup.visible = true;
    this.enabled = true;
    deps.invalidate();
  }

  disable() {
    if (!this.enabled) return;
    this.enabled = false;
    this.endDrag();
    // Keep clippingPlanes arrays attached (avoids shader recompile). Push the
    // plane constants out so nothing gets clipped.
    for (const p of this.planes) p.constant = INACTIVE_CONSTANT;
    this.gizmoGroup.visible = false;
    this.deps?.invalidate();
  }

  // Called by SceneManager after each streaming batch reveals new meshes. If
  // the section box has ever been activated, new materials must receive the
  // same clippingPlanes reference so they also get clipped.
  applyToNewMeshes(root: THREE.Object3D) {
    if (!this.hasMaterialAssignment) return;
    this.assignPlanesTo(root);
  }

  dispose() {
    this.endDrag();
    this.gizmoGroup.parent?.remove(this.gizmoGroup);
    this.boxLine.geometry.dispose();
    (this.boxLine.material as THREE.Material).dispose();
    this.handleMaterial.dispose();
    // Handles share one sphere geometry.
    const firstHandle = this.handles.pX;
    firstHandle.geometry.dispose();
    this.deps = null;
  }

  private assignPlanesTo(root: THREE.Object3D) {
    root.traverse((obj) => {
      const mesh = obj as THREE.Mesh & THREE.LineSegments;
      const drawable =
        mesh.isMesh || mesh.isLineSegments || (obj as THREE.Line).isLine;
      if (!drawable) return;
      // Skip our own gizmo: handle meshes by lookup, the boxLine by reference.
      if (this.handleByMesh.has(mesh as THREE.Mesh)) return;
      if (obj === this.boxLine) return;
      const mat = (mesh as THREE.Mesh).material as
        | THREE.Material
        | THREE.Material[];
      if (Array.isArray(mat)) {
        for (const m of mat) m.clippingPlanes = this.planes;
      } else if (mat) {
        mat.clippingPlanes = this.planes;
      }
    });
  }

  private syncPlaneConstants() {
    // For +X face: plane.normal = (-1,0,0), keeping x < max → constant = max.
    // For -X face: plane.normal = (1,0,0), keeping x > min → constant = -min.
    for (let i = 0; i < FACES.length; i++) {
      const f = FACES[i];
      const value =
        f.side > 0
          ? this.max.getComponent(f.axisIdx)
          : this.min.getComponent(f.axisIdx);
      this.planes[i].constant = f.side > 0 ? value : -value;
    }
  }

  private updateGizmo() {
    const { min, max } = this;
    const corners = [
      [min.x, min.y, min.z],
      [max.x, min.y, min.z],
      [max.x, max.y, min.z],
      [min.x, max.y, min.z],
      [min.x, min.y, max.z],
      [max.x, min.y, max.z],
      [max.x, max.y, max.z],
      [min.x, max.y, max.z],
    ];
    let p = 0;
    for (const [a, b] of EDGE_INDICES) {
      this.boxPositions[p++] = corners[a][0];
      this.boxPositions[p++] = corners[a][1];
      this.boxPositions[p++] = corners[a][2];
      this.boxPositions[p++] = corners[b][0];
      this.boxPositions[p++] = corners[b][1];
      this.boxPositions[p++] = corners[b][2];
    }
    (
      this.boxLine.geometry.attributes.position as THREE.BufferAttribute
    ).needsUpdate = true;
    this.boxLine.geometry.computeBoundingSphere();

    const cx = (min.x + max.x) * 0.5;
    const cy = (min.y + max.y) * 0.5;
    const cz = (min.z + max.z) * 0.5;
    this.handles.pX.position.set(max.x, cy, cz);
    this.handles.nX.position.set(min.x, cy, cz);
    this.handles.pY.position.set(cx, max.y, cz);
    this.handles.nY.position.set(cx, min.y, cz);
    this.handles.pZ.position.set(cx, cy, max.z);
    this.handles.nZ.position.set(cx, cy, min.z);
  }

  // Driven by SceneManager's single pointerdown listener: returns true if a
  // handle was hit and a drag started. Callers should then skip their own
  // pick logic for this event.
  tryStartDrag(ev: PointerEvent): boolean {
    if (!this.enabled || !this.deps) return false;
    this.updatePointer(ev);
    this.raycaster.setFromCamera(this.pointer, this.deps.getCamera());
    const hits = this.raycaster.intersectObjects(
      Object.values(this.handles),
      false,
    );
    if (hits.length === 0) return false;
    const key = (hits[0].object as THREE.Mesh).userData.faceKey as FaceKey;
    this.dragging = key;
    this.deps.setControlsEnabled(false);

    const face = FACES.find((f) => f.key === key)!;
    const currentValue =
      face.side > 0
        ? this.max.getComponent(face.axisIdx)
        : this.min.getComponent(face.axisIdx);
    this.dragStartValue = currentValue;
    this.dragStartAxisParam = this.closestAxisParam(face);

    window.addEventListener("pointermove", this.onPointerMove);
    window.addEventListener("pointerup", this.onPointerUp);
    return true;
  }

  private endDrag() {
    if (this.dragging) {
      this.deps?.setControlsEnabled(true);
    }
    this.dragging = null;
    window.removeEventListener("pointermove", this.onPointerMove);
    window.removeEventListener("pointerup", this.onPointerUp);
  }

  private onPointerMove = (ev: PointerEvent) => {
    if (!this.dragging || !this.deps) return;
    this.updatePointer(ev);
    this.raycaster.setFromCamera(this.pointer, this.deps.getCamera());
    const face = FACES.find((f) => f.key === this.dragging)!;
    const now = this.closestAxisParam(face);
    const delta = now - this.dragStartAxisParam;
    let newValue = this.dragStartValue + delta;

    // Clamp: positive face must stay above negative face and vice versa, with
    // a small gap so the box doesn't collapse to zero thickness.
    const gap = this.handleRadius * 2;
    if (face.side > 0) {
      const lo = this.min.getComponent(face.axisIdx) + gap;
      if (newValue < lo) newValue = lo;
      this.max.setComponent(face.axisIdx, newValue);
    } else {
      const hi = this.max.getComponent(face.axisIdx) - gap;
      if (newValue > hi) newValue = hi;
      this.min.setComponent(face.axisIdx, newValue);
    }
    this.syncPlaneConstants();
    this.updateGizmo();
    this.deps.invalidate();
  };

  private onPointerUp = () => {
    if (!this.dragging) return;
    this.endDrag();
  };

  private updatePointer(ev: PointerEvent) {
    if (!this.deps) return;
    const rect = this.deps.domElement.getBoundingClientRect();
    this.pointer.x = ((ev.clientX - rect.left) / rect.width) * 2 - 1;
    this.pointer.y = -((ev.clientY - rect.top) / rect.height) * 2 + 1;
  }

  // Closest-point-on-line-to-ray, projected onto the axis. The axis passes
  // through the handle's current position along world axis `axisIdx`; this is
  // more stable at grazing view angles than intersecting a view-aligned plane.
  private closestAxisParam(face: FaceDef): number {
    const handle = this.handles[face.key];
    const axisDir = new THREE.Vector3();
    axisDir.setComponent(face.axisIdx, 1);
    const origin = this.raycaster.ray.origin;
    const dir = this.raycaster.ray.direction;
    const w0 = new THREE.Vector3().subVectors(handle.position, origin);
    const a = 1; // axisDir · axisDir
    const b = axisDir.dot(dir);
    const c = dir.dot(dir);
    const d = axisDir.dot(w0);
    const e = dir.dot(w0);
    const denom = a * c - b * b;
    if (Math.abs(denom) < 1e-8)
      return handle.position.getComponent(face.axisIdx);
    const t = (b * e - c * d) / denom;
    return handle.position.getComponent(face.axisIdx) + t;
  }
}
