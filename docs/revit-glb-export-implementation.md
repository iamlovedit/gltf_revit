# Revit 导出 GLB 实现文档

本文面向维护导出器的工程师，说明 `RevitGltfExporter` 如何把 Revit 图元转换为 glTF 2.0 Binary 文件。核心代码位于 `RevitGltfExporter/RevitGltfExporter` 和 `Shared`。

## 1. 导出入口

入口命令是 `Commands/ExportGlbCommand.cs` 中的 `ExportGlbCommand.Execute`。

执行流程：

1. 获取当前 `UIDocument` 和 `Document`。
2. 要求当前视图必须是 `View3D`，否则取消导出。
3. 弹出 `ExportOptionsWindow`，目前选项包括 `EnableDraco`、`DracoCompressionLevel`、`IncludeProperties`。
4. 弹出 `SaveFileDialog` 获取 `.glb` 输出路径。
5. 创建 `GlbExportContext`。
6. 创建 Revit `CustomExporter`，设置 `IncludeGeometricObjects = true`、`ShouldStopOnError = false`。
7. 调用 `exporter.Export(view)` 遍历当前 3D 视图的渲染几何。
8. 调用 `context.WriteGlb(outputPath)` 写出 GLB。

整体数据流：

```text
Revit 3D View
  -> CustomExporter
  -> GlbExportContext callbacks
  -> GltfBuilder logical glTF + binary buffer
  -> .glb
```

## 2. 图元如何变成 glTF Mesh

Revit 导出不直接读取 `Element.Geometry`，而是通过 `CustomExporter` 接收 Revit 渲染管线已经三角化后的几何。`GlbExportContext` 实现 `IExportContext`，主要处理以下回调：

- `OnElementBegin`：开始一个 Revit 图元。
- `OnMaterial`：记录当前渲染材质。
- `OnPolymesh`：接收当前材质下的三角网格。
- `OnElementEnd`：把收集到的三角网格写成 glTF mesh 和 node。
- `OnInstanceBegin/OnInstanceEnd`、`OnLinkBegin/OnLinkEnd`：维护嵌套实例和链接模型的变换矩阵栈。

### 2.1 ElementState

`OnElementBegin(ElementId elementId)` 会创建当前图元状态：

```text
ElementState
  Element
  ElementId
  Primitives: Dictionary<materialIndex, PrimitiveBucket>
```

`PrimitiveBucket` 是真正的几何缓存：

```text
Positions: float[] packed as x,y,z
Normals:   float[] packed as x,y,z
Uvs:       float[] packed as u,v
Indices:   int[] triangle indices
```

按材质拆 bucket 的原因是 glTF 的一个 `primitive` 只能绑定一个 material。一个 Revit 图元如果有多个材质，最终会生成一个 glTF mesh，mesh 内含多个 primitive。

### 2.2 材质切换

`OnMaterial(MaterialNode node)` 调用 `MaterialCollector.GetOrCreate(...)`，把 Revit 材质转换为 glTF material index，并赋值给 `_currentMaterialIndex`。

后续的 `OnPolymesh` 会把几何写入当前材质对应的 `PrimitiveBucket`。

### 2.3 PolymeshTopology 转顶点和索引

`OnPolymesh(PolymeshTopology polymesh)` 是“图元变成 GLB”的核心。

处理逻辑：

1. 如果当前没有图元或没有材质，直接跳过。
2. 根据 `_currentMaterialIndex` 取得当前材质 bucket。
3. 从变换栈取当前实例/链接累积变换 `xf`。
4. 读取 `polymesh.GetPoints()`、`GetNormals()`、`GetUVs()`、`GetFacets()`。
5. 把每个 Revit 顶点变换到世界坐标，再转换为 glTF 坐标。
6. 把每个 facet 的 `V1/V2/V3` 写入索引数组。

坐标转换规则：

```text
Revit 内部坐标：英尺，Z-up
glTF 输出坐标：米，Y-up

position: (X, Y, Z) -> (X, Z, -Y) * 0.3048
normal:   (X, Y, Z) -> (X, Z, -Y)
```

代码行为：

- 点坐标先执行 `xf.OfPoint(pts[i])`，再做英尺到米和轴转换。
- 法线先执行 `xf.OfVector(normals[ni]).Normalize()`，再做轴转换，不做长度缩放。
- 如果 Revit 返回的 UV 数量和点数量一致，则写入 `TEXCOORD_0`。
- facet 索引使用当前 bucket 的 `baseIndex` 做偏移，保证多个 polymesh 可以追加到同一个 primitive bucket。

法线数量不一定和点数量一致。当前实现如果 `normals.Count == pts.Count` 就按顶点取法线，否则使用第一个 normal 作为回退。

### 2.4 Element 结束时生成 glTF node

`OnElementEnd(ElementId elementId)` 会把当前 `ElementState` 写入 glTF：

1. 遍历每个材质 bucket。
2. 对 `Positions` 计算 `min/max`，用于 glTF accessor 的包围信息。
3. 如果 `EnableDraco = false`：
   - `Positions` 写成 `POSITION` accessor，类型 `VEC3/FLOAT`。
   - `Normals` 写成 `NORMAL` accessor，类型 `VEC3/FLOAT`。
   - `Indices` 写成 index accessor，类型 `SCALAR/UNSIGNED_INT`。
   - 如果有 UV，写成 `TEXCOORD_0` accessor，类型 `VEC2/FLOAT`。
4. 如果 `EnableDraco = true`：
   - 调用 `GltfBuilder.AddDracoPrimitive(...)`。
   - 顶点、法线、UV、索引由 `draco_encoder.dll` 编码成一个压缩 bufferView。
   - primitive 写入 `KHR_draco_mesh_compression` 扩展。
5. 创建 `GltfMesh`，每个材质 bucket 生成一个 `GltfPrimitive`。
6. 创建 `GltfNode`，设置 `Mesh = meshIdx` 和 `Extras`。
7. 调用 `_builder.AddNode(node)`。

Revit 当前实现是一图元一 node，不做 FamilyInstance 级别的 mesh 复用或 GPU instancing。

## 3. 材质转换

`Export/MaterialCollector.cs` 负责把 Revit 材质转换为共享的 `MaterialBuilder` 输入。

有真实 `MaterialId` 时：

- key: `rvt:{materialId}`
- name: `Material.Name`
- base color: `Material.Color`
- alpha: `1 - Transparency / 100`
- metallic: `Shininess / 128`
- roughness: `1 - Smoothness / 100`

没有有效材质时使用 Revit 渲染节点提供的 fallback color 和 transparency，创建默认材质。

最终 `MaterialBuilder` 写出的 glTF material 使用 `pbrMetallicRoughness.baseColorFactor`、`metallicFactor`、`roughnessFactor`。当 alpha 小于 1 时设置 `alphaMode = "BLEND"`，材质默认 `doubleSided = true`。

## 4. 属性写入 extras

根对象 `GltfRoot.Extras`：

```json
{
  "schemaVersion": "1.0.0",
  "source": "Revit",
  "unit": "meter"
}
```

每个 Revit 图元 node 的 `extras` 由 `BuildNodeExtras` 创建：

- `elementId`：Revit `ElementId.IntegerValue`。
- `category`：`Element.Category.Name`。
- `family`：`FamilyInstance.Symbol.FamilyName`，仅族实例有。
- `type`：族类型名或元素名。
- `parameters`：当 `IncludeProperties = true` 时写入。

`PropertyCollector.Collect(element)` 遍历 `element.Parameters`：

- `String` -> `AsString()`。
- `Integer` -> `AsInteger()`。
- `Double` -> 使用 `UnitUtils.ConvertFromInternalUnits(...)` 尝试转成显示单位或 SI 语义值。
- `ElementId` -> `IntegerValue`。

## 5. GLB 写入机制

共享模块 `Shared/GltfBuilder.cs` 负责组装 glTF 结构和二进制数据。

未压缩 primitive 的写入方式：

```text
float/int arrays
  -> byte[]
  -> bufferView
  -> accessor
  -> primitive.attributes / primitive.indices
```

关键对象：

- `GltfRoot`：glTF 根 JSON。
- `GltfNode`：场景节点，绑定 mesh 和 extras。
- `GltfMesh`：mesh 容器。
- `GltfPrimitive`：真正的绘制单元，默认 `mode = 4`，即 `TRIANGLES`。
- `GltfAccessor`：描述 typed view，例如 `VEC3/FLOAT`。
- `GltfBufferView`：指向全局 BIN chunk 中的一段字节。
- `GltfBuffer`：GLB 内唯一二进制 buffer。

`WriteGlb(path)` 写文件时：

1. BIN chunk 补齐到 4 字节。
2. 写入 `buffers[0].byteLength`。
3. 如果没有显式 scene，则默认把所有 nodes 放入一个 scene。
4. 序列化 JSON，忽略 null 字段，并用空格补齐到 4 字节。
5. 写 GLB header：
   - magic: `0x46546C67`，即 `glTF`
   - version: `2`
   - totalLength
6. 写 JSON chunk。
7. 写 BIN chunk。

## 6. 限制与注意事项

- 导出依赖当前 Revit 3D 视图，视图可见性会影响 `CustomExporter` 提供的几何。
- 当前实现按图元输出 node，不对重复族实例做几何去重。
- UV 只有在 Revit 提供逐顶点 UV 时才写出。
- Draco 只作用于三角面 primitive；启用时运行环境必须能加载同目录下的 `draco_encoder.dll`。
- 属性写入 `extras` 会增大 GLB JSON chunk，复杂模型可能需要后续做属性外置。
