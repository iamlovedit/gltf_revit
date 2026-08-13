# GLB 数据契约

## 文件与根对象

- 文件必须为 glTF 2.0 Binary（`.glb`），一个 JSON chunk 加一个 BIN chunk，chunk 与 `buffers[0].byteLength` 按 4 字节对齐。
- 根对象包含 `asset.version: "2.0"`、`scene: 0`、`scenes[0]`、`nodes`、`meshes`、`materials`、`accessors`、`bufferViews` 和 `buffers[0]`。
- 根 `extras` 必须包含 `schemaVersion: "1.0.0"`、`source: "Revit"` 或 `"AutoCAD"`、`unit: "meter"`；AutoCAD 另写 `originalUnit`。Revit 导出器应写入 `sourceVersion`（例如 `"2024"`）用于诊断，Viewer 不得依赖该字段决定加载逻辑。
- 坐标为右手系、Y-up、米；三角 primitive `mode: 4`，线 primitive `mode: 1`。

## 属性约定

- Revit node：`elementId`、可选 `category`/`family`/`type`/`parameters`。
- AutoCAD layer node：`layer`、可选 `layerColor`（`[r,g,b]`，0–255）和 `entities`；块 node：`handle`、`layer` 及 CAD 属性。
- `parameters`、`entities` 等可选字段关闭时不得以空对象替代；未知字段由 Viewer 忽略。

## Draco 约定

启用压缩时根对象同时声明 `extensionsUsed` 和 `extensionsRequired` 中的 `KHR_draco_mesh_compression`。每个压缩三角 primitive 的扩展必须提供无 `target` 的 `bufferView` 及属性映射；保留 shadow accessors 描述解码后的 count/type/min/max。线 primitive 不得声明 Draco 扩展。
