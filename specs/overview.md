# 总体业务规格

## 目标

将 Revit 2019 或 AutoCAD 2020–2024 中的可见模型导出为可在浏览器打开的 `.glb`，保留几何、材质和可查询属性，并支持可选 Draco 三角网格压缩。Viewer 必须能在不依赖 Autodesk 宿主的情况下加载、浏览和检查文件。

## 模块边界

- `src/RevitGltfExporter`：只负责 Revit 视图数据采集与 Revit 属性/材质映射。
- `src/AutoCadGltfExporter`：只负责 DWG ModelSpace 实体、图层、块和 CAD 属性采集。
- `src/Shared`：负责与宿主无关的 glTF/GLB 结构、二进制 buffer、材质和 Draco 调用。
- `src/draco_encoder_wrapper`：构建 `draco_encoder.dll`，不改变导出业务语义。
- `src/web-viewer`：只消费符合 `glb-contract.md` 的 GLB，不读取 Revit/AutoCAD API。

## 共通验收

1. 生成文件是可被标准 glTF 2.0 loader 打开的 GLB 2.0。
2. 输出坐标为右手系、Y-up、米；导出失败不得留下未完成的目标文件。
3. 关闭属性或 Draco 选项时，不应写入对应的可选数据或扩展。
4. 失败、取消和宿主版本/路径缺失都必须给出可诊断信息，且恢复宿主临时状态。
