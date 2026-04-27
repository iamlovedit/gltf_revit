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

## 2. glTF 和 GLB 数据结构

glTF 2.0 可以理解为“JSON 结构描述 + 二进制数据块”。本项目最终写出的是 Binary glTF，即 `.glb` 文件，它把 JSON 和二进制 BIN 合在同一个文件里。

GLB 文件由三部分组成：

```text
GLB header
  magic/version/totalLength
JSON chunk
  glTF scene、node、mesh、material、accessor、bufferView 等结构
BIN chunk
  顶点、法线、UV、索引，或 Draco 压缩后的 primitive 数据
```

当前导出器的核心引用链如下：

```text
GltfRoot
  scenes[0]
    nodes[] -> GltfNode
      mesh -> GltfMesh
        primitives[] -> GltfPrimitive
          attributes["POSITION"]   -> GltfAccessor
          attributes["NORMAL"]     -> GltfAccessor
          attributes["TEXCOORD_0"] -> GltfAccessor
          indices                  -> GltfAccessor
          material                 -> GltfMaterial
            accessor.bufferView    -> GltfBufferView
              buffer = 0           -> GltfBuffer / GLB BIN chunk
```

也就是说，`node` 不直接保存顶点数组，`mesh` 也不直接保存三角形数据。真正的几何字节都在 BIN chunk 中；JSON 只保存“哪一段字节应该按什么类型解释”的描述。

### 2.1 JSON 根对象

共享模块 `Shared/GltfSchema.cs` 中的 `GltfRoot` 对应 glTF JSON 根对象。当前实现主要写入这些字段：

- `asset`：文件元信息，当前固定为 `version = "2.0"`、`generator = "GltfExporter"`。
- `scene`：默认场景索引，当前为 `0`。
- `scenes`：场景列表。`WriteGlb(...)` 发现没有显式 scene 时，会创建一个默认 scene，并把所有顶层 node 放进去。
- `nodes`：场景节点列表。Revit 导出当前是一 Revit 图元一 `GltfNode`，node 通过 `mesh` 字段引用 mesh。
- `meshes`：网格列表。一个 `GltfMesh` 包含一个或多个 `GltfPrimitive`。
- `materials`：材质列表。primitive 通过 `material` 索引引用这里的材质。
- `accessors`：typed view 列表。accessor 描述二进制数据的组件类型、元素数量、向量类型和可选包围范围。
- `bufferViews`：二进制切片列表。bufferView 指向 `buffers[0]` 中的一段连续字节。
- `buffers`：二进制 buffer 列表。GLB 输出中只有一个 buffer，即 BIN chunk。
- `extensionsUsed`、`extensionsRequired`：启用 Draco 时写入 `KHR_draco_mesh_compression`。
- `extras`：非标准扩展信息。当前用于保存导出来源、单位和 Revit 图元属性。

### 2.2 scene、node、mesh、primitive

`scene` 是渲染入口，只保存顶层 `node` 的索引数组。当前 Revit 导出没有显式层级树，`WriteGlb(...)` 会把所有已添加的 node 都放入默认 scene：

```json
{
  "scene": 0,
  "scenes": [
    { "nodes": [0, 1, 2] }
  ]
}
```

`GltfNode` 是场景中的对象实例。当前 Revit node 主要包含：

- `name`：由 Revit category、element name 和 element id 组成。
- `mesh`：引用 `meshes` 数组中的一个 mesh。
- `extras`：保存 `elementId`、`category`、`family`、`type` 和可选参数。

`GltfMesh` 是几何容器。一个 mesh 可以有多个 `primitive`，本项目按材质拆 primitive，所以一个 Revit 图元如果包含多种材质，会导出为一个 mesh 下的多个 primitive。

`GltfPrimitive` 是真正的绘制单元。当前三角面 primitive 的关键字段是：

- `mode = 4`：表示按 `TRIANGLES` 绘制。
- `attributes`：顶点属性表，例如 `POSITION`、`NORMAL`、`TEXCOORD_0`。
- `indices`：索引 accessor，用于描述三角形顶点顺序。
- `material`：材质索引，一个 primitive 只能绑定一个 material。
- `extensions`：启用 Draco 时保存 `KHR_draco_mesh_compression` 数据。

### 2.3 accessor、bufferView、buffer

几何数组从 C# 的 `List<float>` 或 `List<int>` 写入 glTF 时，会被拆成三层：

```text
List<float>/List<int>
  -> byte[]
  -> GltfBufferView: byteOffset + byteLength + target
  -> GltfAccessor: componentType + count + type + min/max
  -> GltfPrimitive.attributes 或 GltfPrimitive.indices
```

`GltfBufferView` 描述 BIN chunk 中的一段字节：

- `buffer = 0`：当前始终引用唯一的 GLB BIN chunk。
- `byteOffset`：这段数据在 BIN chunk 中的起始字节位置。
- `byteLength`：这段数据的字节长度。
- `target = 34962`：顶点属性数据，即 `ARRAY_BUFFER`。
- `target = 34963`：索引数据，即 `ELEMENT_ARRAY_BUFFER`。

`GltfAccessor` 描述 bufferView 中的数据应该怎样解释：

- `componentType = 5126`：`FLOAT`，用于 position、normal、uv。
- `componentType = 5125`：`UNSIGNED_INT`，用于 index。
- `type = "VEC3"`：每个元素 3 个分量，用于 `POSITION` 和 `NORMAL`。
- `type = "VEC2"`：每个元素 2 个分量，用于 `TEXCOORD_0`。
- `type = "SCALAR"`：每个元素 1 个分量，用于 `indices`。
- `count`：元素数量，不是字节数。例如 position 有 300 个 float 时，`count = 100`。
- `min/max`：当前只给 `POSITION` 写入，用于记录 mesh 的包围范围。

当前 `GltfBuilder` 为每个属性单独写一个 bufferView，所以 accessor 的 `byteOffset` 保持默认 `0`。真正的字节偏移在 bufferView 的 `byteOffset` 上。

### 2.4 一个 Revit 图元的 glTF 结构示例

一个只含单一材质的 Revit 图元，未启用 Draco 时，导出的 JSON 关系大致如下：

```json
{
  "nodes": [
    {
      "name": "Walls_Basic Wall_12345",
      "mesh": 0,
      "extras": {
        "elementId": 12345,
        "category": "Walls",
        "type": "Basic Wall"
      }
    }
  ],
  "meshes": [
    {
      "name": "Walls_Basic Wall",
      "primitives": [
        {
          "attributes": {
            "POSITION": 0,
            "NORMAL": 1,
            "TEXCOORD_0": 3
          },
          "indices": 2,
          "material": 0,
          "mode": 4
        }
      ]
    }
  ],
  "accessors": [
    { "bufferView": 0, "byteOffset": 0, "componentType": 5126, "count": 100, "type": "VEC3", "min": [0, 0, 0], "max": [1, 2, 3] },
    { "bufferView": 1, "byteOffset": 0, "componentType": 5126, "count": 100, "type": "VEC3" },
    { "bufferView": 2, "byteOffset": 0, "componentType": 5125, "count": 300, "type": "SCALAR" },
    { "bufferView": 3, "byteOffset": 0, "componentType": 5126, "count": 100, "type": "VEC2" }
  ],
  "bufferViews": [
    { "buffer": 0, "byteOffset": 0, "byteLength": 1200, "target": 34962 },
    { "buffer": 0, "byteOffset": 1200, "byteLength": 1200, "target": 34962 },
    { "buffer": 0, "byteOffset": 2400, "byteLength": 1200, "target": 34963 },
    { "buffer": 0, "byteOffset": 3600, "byteLength": 800, "target": 34962 }
  ],
  "buffers": [
    { "byteLength": 4400 }
  ]
}
```

示例里的数字只是说明引用关系。实际 `byteOffset` 会受 `GltfBuilder.WriteBufferView(...)` 的对齐填充影响，最终 BIN chunk 也会在 `WriteGlb(...)` 中补齐到 4 字节。

## 3. 图元如何变成 glTF Mesh

Revit 导出不直接读取 `Element.Geometry`，而是通过 `CustomExporter` 接收 Revit 渲染管线已经三角化后的几何。`GlbExportContext` 实现 `IExportContext`，主要处理以下回调：

- `OnElementBegin`：开始一个 Revit 图元。
- `OnMaterial`：记录当前渲染材质。
- `OnPolymesh`：接收当前材质下的三角网格。
- `OnElementEnd`：把收集到的三角网格写成 glTF accessor、bufferView、primitive、mesh 和 node。
- `OnInstanceBegin/OnInstanceEnd`、`OnLinkBegin/OnLinkEnd`：维护嵌套实例和链接模型的变换矩阵栈。

### 3.1 ElementState

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

### 3.2 材质切换

`OnMaterial(MaterialNode node)` 调用 `MaterialCollector.GetOrCreate(...)`，把 Revit 材质转换为 glTF material index，并赋值给 `_currentMaterialIndex`。

后续的 `OnPolymesh` 会把几何写入当前材质对应的 `PrimitiveBucket`。

### 3.3 PolymeshTopology 转顶点和索引

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

### 3.4 Element 结束时生成 glTF node

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

`PrimitiveBucket` 到 glTF primitive 的字段映射如下：

| `PrimitiveBucket` 数据 | glTF 写入位置 | accessor 形态 |
| --- | --- | --- |
| `Positions` | `primitive.attributes["POSITION"]` | `VEC3 / FLOAT`，写入 `min/max` |
| `Normals` | `primitive.attributes["NORMAL"]` | `VEC3 / FLOAT` |
| `Uvs` | `primitive.attributes["TEXCOORD_0"]` | `VEC2 / FLOAT` |
| `Indices` | `primitive.indices` | `SCALAR / UNSIGNED_INT` |
| dictionary key `materialIndex` | `primitive.material` | 引用 `materials[materialIndex]` |

未压缩时，每个 accessor 都引用一个真实 bufferView；bufferView 再指向 GLB BIN chunk 中的实际字节。启用 Draco 时，primitive 的顶点、法线、UV 和索引会先进入 Draco encoder，压缩结果写成一个无 `target` 的 bufferView；`POSITION`、`NORMAL`、`TEXCOORD_0` 和 `indices` 仍会保留 accessor，但这些 accessor 不包含 `bufferView`，它们只描述解码后的数据数量和类型。

Revit 当前实现是一图元一 node，不做 FamilyInstance 级别的 mesh 复用或 GPU instancing。

## 4. 材质转换

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

## 5. 属性写入 extras

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

## 6. GLB 写入机制

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

普通三角面 primitive 的构造过程：

1. `AddFloat3Accessor(bucket.Positions, min, max, GltfTarget.ArrayBuffer)` 把 position 写入 BIN chunk，创建 `POSITION` accessor。
2. `AddFloat3Accessor(bucket.Normals, null, null, GltfTarget.ArrayBuffer)` 创建 `NORMAL` accessor。
3. `AddIndexAccessor(bucket.Indices)` 创建 index accessor，bufferView 的 `target = ELEMENT_ARRAY_BUFFER`。
4. 如果存在 UV，`AddFloat2Accessor(bucket.Uvs, GltfTarget.ArrayBuffer)` 创建 `TEXCOORD_0` accessor。
5. `GltfPrimitive.Attributes` 保存属性名到 accessor index 的映射，`GltfPrimitive.Indices` 保存索引 accessor index。

Draco 三角面 primitive 的构造过程：

1. `AddDracoPrimitive(...)` 调用 `DracoNative.Encode(...)`，把 position、normal、uv、indices 编码为 Draco 字节。
2. 压缩字节写入一个 bufferView，`target` 为空，因为 `KHR_draco_mesh_compression` 要求压缩 bufferView 不声明 `ARRAY_BUFFER` 或 `ELEMENT_ARRAY_BUFFER`。
3. primitive 的 `extensions.KHR_draco_mesh_compression.bufferView` 指向压缩 bufferView。
4. `extensions.KHR_draco_mesh_compression.attributes` 保存 glTF 属性名到 Draco attribute id 的映射。
5. `AddShadowAccessor(...)` 仍为 `POSITION`、`NORMAL`、`TEXCOORD_0` 和 `indices` 创建 accessor，用来描述解码后的 `count/type/componentType/min/max`，但这些 accessor 没有 `bufferView`。
6. `extensionsUsed` 和 `extensionsRequired` 都声明 `KHR_draco_mesh_compression`。

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

## 7. 限制与注意事项

- 导出依赖当前 Revit 3D 视图，视图可见性会影响 `CustomExporter` 提供的几何。
- 当前实现按图元输出 node，不对重复族实例做几何去重。
- UV 只有在 Revit 提供逐顶点 UV 时才写出。
- Draco 只作用于三角面 primitive；启用时运行环境必须能加载同目录下的 `draco_encoder.dll`。
- 属性写入 `extras` 会增大 GLB JSON chunk，复杂模型可能需要后续做属性外置。
