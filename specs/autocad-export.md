# AutoCAD 2020–2024 导出规格

## 输入与入口

- 命令名为 `EXPORTGLB`，目标平台为 x64、.NET Framework 4.8，自动或显式解析 `acdbmgd.dll` 等 API 引用。
- 导出范围仅为当前 DWG 的可见 `ModelSpace` 实体；输出前显示 Draco、压缩级别（0–10，默认 7）和属性选项，并选择 `.glb` 路径。
- 导出期间临时提高 `Database.Facetres`，结束时（成功或异常）必须恢复原值并释放文档锁/事务。

## 几何组织与属性

- 按有效图层聚合为 layer node；三角面使用 primitive `mode: 4`，曲线/线段使用 `mode: 1`。
- 支持实体、曲面、网格、Region、Hatch、文字/标注、Curve 和 BlockReference；复杂注记可递归 explode，块递归深度最多 32 层。
- 块内容不依赖插入颜色/图层时复用模板 mesh 并保留实例矩阵，否则展开并继承上下文。
- 根据 `INSUNITS` 转米并把 Z-up 转为 Y-up；颜色按 ByLayer/ByBlock/实体颜色解析为 PBR 材质。
- layer `extras` 包含 `layer` 与可用的 `layerColor`；开启属性时包含实体数组。块实例至少保留 `handle`、`layer`，并可包含 linetype、lineweight、颜色、XData 和扩展字典引用。

## 验收条件

1. 不导出 PaperSpace；不可见或处理失败的实体计入跳过统计并输出诊断消息。
2. Draco 只压缩三角面，`LINES` 始终保持普通 bufferView。
3. 图层节点在 Viewer 中可枚举并独立显示/隐藏；块实例的变换和属性可被拾取。
