# 开发文档：Revit GLB 导出插件 + Web 浏览器

## 1. 项目概览

本项目包含两个子系统：

| 子系统 | 目标 | 技术栈 |
| --- | --- | --- |
| `RevitGltfExporter` | Revit 2019 插件，将模型导出为含材质与构件属性的 `.glb`，支持 Draco 几何压缩 | C# / .NET Framework 4.7 / Revit 2019 API |
| `web-viewer` | 浏览器端 GLB 查看器，支持大模型异步、非阻塞加载 | Vite + React 18 + TypeScript + three.js + pnpm |

两端通过 GLB 文件解耦；插件产物是 Viewer 的唯一输入。

---

## 2. 仓库结构建议

```
repo/
├── business.md                # 原始需求
├── development.md             # 本文件
├── RevitGltfExporter/         # Revit 插件解决方案
│   ├── RevitGltfExporter.sln
│   └── RevitGltfExporter/
│       ├── RevitGltfExporter.csproj
│       ├── Application.cs            # IExternalApplication，注册 Ribbon
│       ├── Commands/
│       │   └── ExportGlbCommand.cs   # IExternalCommand 入口
│       ├── Export/
│       │   ├── GlbExportContext.cs   # IExportContext 实现
│       │   ├── MaterialCollector.cs
│       │   ├── PropertyCollector.cs
│       │   └── DracoCompressor.cs
│       ├── UI/ExportOptionsWindow.xaml
│       └── Resources/RevitGltfExporter.addin
└── web-viewer/                # 前端项目
    ├── package.json
    ├── vite.config.ts
    ├── public/
    │   └── draco/             # draco_decoder.wasm / .js
    └── src/
        ├── main.tsx
        ├── App.tsx
        ├── viewer/
        │   ├── Viewer.tsx
        │   ├── SceneManager.ts
        │   ├── AsyncGltfLoader.ts
        │   └── PropertyPanel.tsx
        └── workers/
            └── gltfWorker.ts
```

---

## 3. Revit 插件开发

### 3.1 环境与依赖

- **Revit 版本**：2019
- **.NET Framework**：`4.7`（Revit 2019 的运行时）
- **目标平台**：`x64`
- **关键引用**（Revit 安装目录，属性 `Copy Local=false`）：
  - `RevitAPI.dll`
  - `RevitAPIUI.dll`
- **NuGet**：
  - `SharpGLTF.Toolkit`（推荐）或 `glTF2Loader` —— 构建 glTF/GLB 结构
  - `Newtonsoft.Json` —— 属性序列化
  - Draco 压缩使用 Google `draco_encoder`（C++）通过 P/Invoke 或独立 CLI 调用；无成熟托管版时，优先 **CLI 方式**：导出未压缩 glTF → 调 `gltf-pipeline --draco` 生成最终 GLB。

### 3.2 注册与加载

- `.addin` 文件放到 `%ProgramData%\Autodesk\Revit\Addins\2019\`
- `Application : IExternalApplication` 在 `OnStartup` 中创建 Ribbon 面板与按钮，按钮 `PushButtonData` 指向 `ExportGlbCommand`
- `ExportGlbCommand : IExternalCommand` 是导出入口，读取 UI 选项后调用导出服务

### 3.3 几何导出（核心）

使用 **`CustomExporter` + `IExportContext`** 遍历当前视图的渲染数据，而非 `FilteredElementCollector` + `Geometry`，原因：

- `IExportContext` 提供展开后的三角网格与变换矩阵，自动处理链接模型、实例、组
- 材质回调 `OnMaterial` 与节点进入/退出回调天然对应 glTF 的 Node/Mesh/Material 结构

实现要点：

- `OnElementBegin` → 新建 glTF Node，记录当前 `ElementId`
- `OnInstanceBegin/End` → 维护变换栈
- `OnPolymesh` → 将 `PolymeshTopology` 的顶点/法线/UV/索引追加到当前 `Mesh.Primitive`
- `OnMaterial` → 切换当前 Primitive 使用的材质
- `OnElementEnd` → 将构件属性写入节点的 `extras`

### 3.4 材质映射

Revit `Material` → glTF `pbrMetallicRoughness`：

| Revit | glTF |
| --- | --- |
| `Color` + `Transparency` | `baseColorFactor`（含 alpha） |
| `Shininess` / `Smoothness` | `roughnessFactor = 1 - smoothness/100` |
| `Metalness`（如有外观资源） | `metallicFactor` |
| 漫反射贴图（`AppearanceAssetElement`） | `baseColorTexture` |

贴图需复制到输出目录并嵌入 GLB（使用 `image/bufferView`）。

### 3.5 构件属性（`extras`）

在每个 Node 写入：

```json
{
  "extras": {
    "elementId": 123456,
    "category": "Walls",
    "family": "Basic Wall",
    "type": "Generic - 200mm",
    "parameters": {
      "Comments": "...",
      "Volume": 1.23,
      "Area": 4.56
    }
  }
}
```

- `PropertyCollector` 遍历 `Element.Parameters`，按 `StorageType` 取值，单位统一转 SI（长度米、体积立方米）
- 大量重复属性可提升到 glTF 根节点 `extras.sharedSchemas`，节点只存 ID，减小文件体积

### 3.6 Draco 压缩

推荐流水线：

1. 内部先产出标准 GLB（`model.glb`）
2. 调 `gltf-pipeline -i model.glb -o model.draco.glb -d`（需随插件分发 Node 环境或预编译 CLI）
3. 或使用 `DracoEncoderModule`（C++）P/Invoke，针对 `bufferView` 逐个压缩并添加 `KHR_draco_mesh_compression` 扩展

导出 UI 提供：是否启用 Draco、压缩等级（0–10，默认 7）、量化位数（position 14 / normal 10 / uv 12）。

### 3.7 事务与性能

- 导出是只读过程，不开 `Transaction`
- 大模型导出使用 `CustomExporter.IncludeGeometricObjects = true` + `Use2DRepresentation = false`
- 顶点/索引缓冲预分配（`List<float>` 初始容量），最后一次性拷贝到 `byte[]`
- 在后台线程做序列化与 Draco 压缩，Revit 主线程仅做几何回调

### 3.8 测试与调试

- 使用 **RevitAddInManager** 或 **Revit SDK AddInManager** 热加载 DLL，避免重启 Revit
- `csproj` 的 `<StartAction>Program</StartAction>` + `<StartProgram>Revit.exe</StartProgram>` 配合 `Debug → Attach` 可断点调试
- 单元测试不易跑 Revit API，将 **几何转换** 与 **glTF 构造** 抽成纯函数类，单独做 xUnit 测试

---

## 4. Web Viewer 开发

### 4.1 初始化

```bash
pnpm create vite web-viewer --template react-ts
cd web-viewer
pnpm add three @types/three
pnpm add -D vite-plugin-static-copy
```

将 `node_modules/three/examples/jsm/libs/draco/` 拷贝到 `public/draco/`（构建时 `vite-plugin-static-copy` 自动完成）。

### 4.2 依赖选择

| 功能 | 库 |
| --- | --- |
| 渲染引擎 | `three` |
| GLB 解析 | `GLTFLoader`（three examples） |
| Draco 解码 | `DRACOLoader` + `draco_decoder.wasm` |
| 控件 | `OrbitControls` |
| 状态管理 | `zustand`（轻量，避免 Redux 样板） |
| 类型 | `@types/three` |

### 4.3 异步加载方案

**关键目标**：加载百兆甚至 GB 级 GLB 不阻塞主线程、不冻结 UI。

策略分层：

1. **流式下载**：`fetch(url)` 拿到 `ReadableStream`，边下边喂给 `GLTFLoader.parse`。结合 `Content-Length` 渲染进度条。
2. **Worker 解析**：`DRACOLoader.setWorkerLimit(navigator.hardwareConcurrency)` 把 Draco 解码放到 WebWorker 池。
3. **分帧构建场景**：遍历 `gltf.scene` 时用 `requestIdleCallback` 分批 `scene.add`，每帧不超过 8 ms。
4. **按需上传 GPU**：首帧只上传视锥内的 mesh，其余在空闲帧逐步 `renderer.initTexture/compile`。
5. **LOD / 视锥剔除**：`THREE.LOD` + `frustumCulled = true`（默认开），大模型再加 `three-mesh-bvh` 做快速射线拾取。
6. **Instancing**：相同 `mesh.geometry + material` 的节点用 `InstancedMesh` 合并（导出端可预聚合）。

关键代码骨架：

```ts
// AsyncGltfLoader.ts
const dracoLoader = new DRACOLoader()
  .setDecoderPath('/draco/')
  .setDecoderConfig({ type: 'wasm' })
  .setWorkerLimit(Math.min(4, navigator.hardwareConcurrency));

const loader = new GLTFLoader().setDRACOLoader(dracoLoader);

export async function loadGlb(url: string, onProgress: (p: number) => void) {
  const res = await fetch(url);
  const total = Number(res.headers.get('Content-Length') ?? 0);
  const reader = res.body!.getReader();
  const chunks: Uint8Array[] = [];
  let received = 0;
  while (true) {
    const { done, value } = await reader.read();
    if (done) break;
    chunks.push(value);
    received += value.byteLength;
    if (total) onProgress(received / total);
  }
  const buf = await new Blob(chunks).arrayBuffer();
  return await loader.parseAsync(buf, '');
}
```

### 4.4 属性面板

- 点击拾取：`Raycaster` 命中 `Mesh` → 读 `mesh.userData`（GLTFLoader 自动把 `extras` 写入 `userData`）
- 展示插件写入的 `elementId / category / parameters`
- 搜索与过滤基于根节点 `userData.sharedSchemas` 做索引

### 4.5 性能预算

| 指标 | 目标 |
| --- | --- |
| 首帧 TTI（200 MB GLB，带宽 50 Mbps） | < 8 s |
| 交互帧率 | 稳定 ≥ 30 FPS（中端笔记本集显） |
| 单帧主线程阻塞 | < 16 ms |
| 内存峰值 | < 模型字节数 × 3 |

在 Chrome DevTools `Performance` 标签验证；不要依赖肉眼观察。

### 4.6 构建与部署

- `vite build` 输出 `dist/`，纯静态
- Draco 解码器、GLB 样例放到 `public/` 下，保证同源，避免 CORS
- 生产环境开启 HTTP/2 + `gzip`/`br`（GLB 内部已压缩，收益主要在 JS 代码）

---

## 5. 联调与数据契约

- 文件格式：**GLB 2.0**，可选 `KHR_draco_mesh_compression`
- 坐标系：导出端统一 **Y-up，右手系，单位米**（Revit 原生英尺 → 转 SI）
- 节点命名：`{Category}_{ElementId}`，便于 Viewer 端调试
- `extras.schemaVersion`：`"1.0.0"`，Viewer 据此做向前兼容

---

## 6. 里程碑

1. **M1（1 周）**：Revit 端跑通几何导出 → 无材质、无压缩的 GLB，Viewer 能加载
2. **M2（1 周）**：材质 + 构件属性写入 `extras`，Viewer 拾取并显示
3. **M3（3–5 天）**：接入 Draco，测大模型导出与加载性能
4. **M4（1 周）**：Viewer 异步加载优化（Worker 池、分帧、LOD），达到性能预算

---

## 7. 风险与备选

| 风险 | 备选 |
| --- | --- |
| 自研 Draco 集成复杂 | 先用 `gltf-pipeline` CLI 外挂，后续再内嵌 |
| `SharpGLTF` 对超大 buffer 有内存压力 | 手写 glTF JSON + 直接拼 `.glb` 二进制 |
| Revit 链接模型、组的变换易错 | 严格依赖 `IExportContext` 的进入/退出回调，不自己遍历几何 |
| 浏览器加载 GB 级模型 OOM | 导出端按楼层/专业分包，Viewer 按需加载包 |
