# AutoCAD 导出 GLB 实现文档

本文面向维护导出器的工程师，说明 `AutoCadGltfExporter` 如何把 DWG 图元转换为 glTF 2.0 Binary 文件。核心代码位于 `AutoCadGltfExporter/AutoCadGltfExporter` 和 `Shared`。

## 1. 导出入口

入口命令是 `Commands/ExportGlbCommand.cs` 中的 `ExportGlbCommand.ExportGlb`，AutoCAD 命令名为 `EXPORTGLB`。

执行流程：

1. 获取当前 AutoCAD `Document`、`Editor`、`Database`。
2. 弹出 `ExportOptionsWindow`，目前选项包括 `EnableDraco`、`DracoCompressionLevel`、`IncludeProperties`。
3. 弹出 `SaveFileDialog` 获取 `.glb` 输出路径。
4. 暂时提高 `Database.Facetres`，让 AutoCAD 的 `WorldDraw` 产生更细的曲面离散结果。
5. `doc.LockDocument()` 后创建 `DwgExportContext`。
6. 调用 `context.Run()` 遍历 ModelSpace 并收集几何。
7. 调用 `context.WriteGlb(outputPath)` 写出 GLB。
8. 在 `finally` 中恢复原始 `Facetres`。

整体数据流：

```text
DWG ModelSpace
  -> DwgExportContext entity dispatch
  -> SolidTessellator / CurveSampler / TextExploder / BlockRefHandler
  -> layer buckets
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
  顶点、法线、线段索引、三角面索引，或 Draco 压缩后的 primitive 数据
```

AutoCAD 导出的核心引用链如下：

```text
GltfRoot
  scenes[0]
    nodes[] -> layer GltfNode
      mesh -> layer GltfMesh
        primitives[] -> GltfPrimitive
          TRIANGLES primitive
            attributes["POSITION"] -> GltfAccessor -> GltfBufferView
            attributes["NORMAL"]   -> GltfAccessor -> GltfBufferView
            indices                -> GltfAccessor -> GltfBufferView
          LINES primitive
            attributes["POSITION"] -> GltfAccessor -> GltfBufferView
            indices                -> GltfAccessor -> GltfBufferView
          material                 -> GltfMaterial
      bufferViews[] -> GltfBufferView
        buffer = 0 -> GltfBuffer / GLB BIN chunk
      children[] -> reusable block instance GltfNode
```

也就是说，`node` 和 `mesh` 不直接保存顶点数组。真正的顶点、法线和索引字节都在 BIN chunk 中；JSON 负责描述场景层级、材质、primitive 类型，以及每段二进制数据应按什么类型解释。

### 2.1 JSON 根对象

共享模块 `Shared/GltfSchema.cs` 中的 `GltfRoot` 对应 glTF JSON 根对象。当前 AutoCAD 导出主要写入这些字段：

- `asset`：文件元信息，当前固定为 `version = "2.0"`、`generator = "GltfExporter"`。
- `scene`：默认场景索引，当前为 `0`。
- `scenes`：场景列表。`DwgExportContext.EmitLayerNodes()` 会显式创建默认 scene，并把所有图层 node 放进去。
- `nodes`：场景节点列表。普通实体聚合到 `layer_*` node，可复用块参照作为图层 node 的 child node。
- `meshes`：网格列表。图层 node 绑定图层 mesh；可复用块实例 node 绑定块模板 mesh。
- `materials`：材质列表。primitive 通过 `material` 索引引用这里的材质，CAD 侧当前按 RGB 去重。
- `accessors`：typed view 列表。accessor 描述二进制数据的组件类型、元素数量、向量类型和可选包围范围。
- `bufferViews`：二进制切片列表。bufferView 指向 `buffers[0]` 中的一段连续字节。
- `buffers`：二进制 buffer 列表。GLB 输出中只有一个 buffer，即 BIN chunk。
- `extensionsUsed`、`extensionsRequired`：启用 Draco 时写入 `KHR_draco_mesh_compression`。
- `extras`：非标准扩展信息。根对象保存来源和单位；图层 node 和块实例 node 保存 CAD 属性。

### 2.2 AutoCAD 场景结构

AutoCAD 导出不是一实体一 node，而是优先按图层聚合：

- `scenes[0].nodes` 保存所有图层 node 的索引。
- 每个图层 node 名称形如 `layer_0`、`layer_Walls`，通常绑定一个图层 mesh。
- 图层 mesh 内可以同时包含三角面 primitive 和线 primitive。
- 普通实体的属性不会各自生成 node，而是集中写入图层 node 的 `extras.entities`。
- 可复用块参照会单独生成 child node，并挂到当前图层 node 的 `children`。
- 块实例 node 通过 `mesh` 引用缓存的块模板 mesh，并通过 `matrix` 保存插入变换。

`LayerBucket` 是写入 glTF 前的中间聚合结构：

```text
LayerBucket
  Triangles:     List<MeshPrimitiveBucket>
  Lines:         List<MeshPrimitiveBucket>
  InstanceNodes: List<GltfNode>
  PerEntityExtras
```

`Triangles` 和 `Lines` 分开保存，是因为 glTF 一个 primitive 只有一个 `mode`：三角面使用 `mode = 4`，线段使用 `mode = 1`。

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

- `componentType = 5126`：`FLOAT`，用于 position 和 normal。
- `componentType = 5125`：`UNSIGNED_INT`，用于 triangle index 和 line index。
- `type = "VEC3"`：每个元素 3 个分量，用于 `POSITION` 和 `NORMAL`。
- `type = "SCALAR"`：每个元素 1 个分量，用于 `indices`。
- `count`：元素数量，不是字节数。例如 position 有 300 个 float 时，`count = 100`。
- `min/max`：当前给 `POSITION` 写入，用于记录 primitive 的包围范围。

CAD 当前不写 `TEXCOORD_0`，因为导出逻辑没有从 DWG 实体构造逐顶点 UV。`GltfBuilder` 为每个属性单独写一个 bufferView，所以 accessor 的 `byteOffset` 保持默认 `0`；真正的字节偏移在 bufferView 的 `byteOffset` 上。

### 2.4 一个图层的 glTF 结构示例

一个图层同时包含三角面、线段和一个可复用块实例时，导出的 JSON 关系大致如下：

```json
{
  "scene": 0,
  "scenes": [
    { "nodes": [0] }
  ],
  "nodes": [
    {
      "name": "layer_Walls",
      "mesh": 0,
      "children": [1],
      "extras": {
        "layer": "Walls",
        "layerColor": [204, 204, 204]
      }
    },
    {
      "name": "block_Door",
      "mesh": 1,
      "matrix": [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 5, 0, 2, 1],
      "extras": {
        "handle": "2A1",
        "layer": "Walls",
        "entityType": "BlockReference"
      }
    }
  ],
  "meshes": [
    {
      "name": "layer_Walls",
      "primitives": [
        {
          "attributes": { "POSITION": 0, "NORMAL": 1 },
          "indices": 2,
          "material": 0,
          "mode": 4
        },
        {
          "attributes": { "POSITION": 3 },
          "indices": 4,
          "material": 0,
          "mode": 1
        }
      ]
    },
    {
      "name": "block_Door_template",
      "primitives": [
        {
          "attributes": { "POSITION": 5, "NORMAL": 6 },
          "indices": 7,
          "material": 0,
          "mode": 4
        }
      ]
    }
  ],
  "accessors": [
    { "bufferView": 0, "byteOffset": 0, "componentType": 5126, "count": 100, "type": "VEC3", "min": [0, 0, 0], "max": [10, 3, 4] },
    { "bufferView": 1, "byteOffset": 0, "componentType": 5126, "count": 100, "type": "VEC3" },
    { "bufferView": 2, "byteOffset": 0, "componentType": 5125, "count": 300, "type": "SCALAR" },
    { "bufferView": 3, "byteOffset": 0, "componentType": 5126, "count": 20, "type": "VEC3", "min": [0, 0, 0], "max": [10, 3, 4] },
    { "bufferView": 4, "byteOffset": 0, "componentType": 5125, "count": 38, "type": "SCALAR" },
    { "bufferView": 5, "byteOffset": 0, "componentType": 5126, "count": 24, "type": "VEC3", "min": [0, 0, 0], "max": [1, 2, 2] },
    { "bufferView": 6, "byteOffset": 0, "componentType": 5126, "count": 24, "type": "VEC3" },
    { "bufferView": 7, "byteOffset": 0, "componentType": 5125, "count": 36, "type": "SCALAR" }
  ],
  "bufferViews": [
    { "buffer": 0, "byteOffset": 0, "byteLength": 1200, "target": 34962 },
    { "buffer": 0, "byteOffset": 1200, "byteLength": 1200, "target": 34962 },
    { "buffer": 0, "byteOffset": 2400, "byteLength": 1200, "target": 34963 },
    { "buffer": 0, "byteOffset": 3600, "byteLength": 240, "target": 34962 },
    { "buffer": 0, "byteOffset": 3840, "byteLength": 152, "target": 34963 },
    { "buffer": 0, "byteOffset": 3996, "byteLength": 288, "target": 34962 },
    { "buffer": 0, "byteOffset": 4284, "byteLength": 288, "target": 34962 },
    { "buffer": 0, "byteOffset": 4572, "byteLength": 144, "target": 34963 }
  ],
  "buffers": [
    { "byteLength": 4716 }
  ]
}
```

示例里的数字只用于说明引用关系。实际 `byteOffset` 会受 `GltfBuilder.WriteBufferView(...)` 的对齐填充影响，最终 BIN chunk 也会在 `WriteGlb(...)` 中补齐到 4 字节。

## 3. ModelSpace 遍历和实体分派

`DwgExportContext.Run()` 开启事务后：

1. 调用 `IndexLayers(tr)` 缓存图层颜色。
2. 打开 `BlockTableRecord.ModelSpace`。
3. 创建根 `EntityContext`，包含当前有效图层、继承颜色、变换矩阵和块递归栈。
4. 遍历 ModelSpace 中可见的 `Entity`。
5. 调用 `ProcessEntityToScene(entity, tr, rootContext)`。
6. 结束后调用 `EmitLayerNodes()` 把图层 bucket 写成 glTF scene、node、mesh 和 primitive。

实体分派规则：

- `BlockReference` -> `ProcessBlockReferenceToScene`。
- `Solid3d`、`Surface`、`SubDMesh`、`PolyFaceMesh`、`PolygonMesh`、`Region`、`Body` -> `ProcessSolidToScene`。
- `Hatch` -> 实心填充按 solid 处理，非实心填充 explode 后递归。
- `DBText`、`MText`、`Dimension`、`Leader`、`MLeader` -> `ProcessAnnotationToScene`。
- `Curve` -> `ProcessCurveToScene`。
- 其他未知实体 -> 尝试按 annotation explode 后递归。

## 4. 图元如何变成 glTF

AutoCAD 导出不是一实体一 node。当前实现按图层聚合，最终通常是一个图层 node 绑定一个图层 mesh，图层 mesh 内按材质拆成多个 primitive。块参照如果可复用，会作为图层 node 的 child node 写入。

`LayerBucket` 到 glTF 的映射如下：

| `LayerBucket` 数据 | glTF 写入位置 | 说明 |
| --- | --- | --- |
| `Triangles` | `layer mesh.primitives[]` | 每个 `MeshPrimitiveBucket` 写成 `mode = 4` 的三角面 primitive |
| `Lines` | `layer mesh.primitives[]` | 每个 `MeshPrimitiveBucket` 写成 `mode = 1` 的线 primitive |
| `InstanceNodes` | `layer node.children` | 可复用块参照写成 child `GltfNode` |
| `PerEntityExtras` | `layer node.extras.entities` | 仅 `IncludeProperties = true` 时写入 |

`MeshPrimitiveBucket` 到 glTF primitive 的字段映射如下：

| `MeshPrimitiveBucket` 数据 | 三角面 primitive | 线 primitive |
| --- | --- | --- |
| `Positions` | `attributes["POSITION"]`，`VEC3 / FLOAT` | `attributes["POSITION"]`，`VEC3 / FLOAT` |
| `Normals` | `attributes["NORMAL"]`，`VEC3 / FLOAT` | 不写入 |
| `Indices` | `indices`，`SCALAR / UNSIGNED_INT`，每 3 个索引组成一个三角形 | `indices`，`SCALAR / UNSIGNED_INT`，每 2 个索引组成一条线段 |
| `Material` | `primitive.material` | `primitive.material` |

### 4.1 3D 实体三角化

`ProcessSolidToScene` 调用 `SolidTessellator.Tessellate(entity)`。

`SolidTessellator` 的核心策略是实现最小化的 `WorldDraw` / `WorldGeometry`，然后调用：

```text
entity.WorldDraw(drawer)
```

AutoCAD 在绘制 `Solid3d`、`Surface`、`Region`、`Body`、实心 `Hatch` 等对象时，会把离散后的面片推给 `WorldGeometry`：

- `Shell(...)`：接收多边形面数据。
- `Mesh(...)`：接收规则网格面数据。

`Shell(...)` 处理方式：

1. 把 `Point3dCollection points` 追加到 `Vertices`。
2. 读取 `faces` 中的每个面。
3. 正数面按顶点扇形三角化：

```text
(v0, v1, v2), (v0, v2, v3), ...
```

4. 负数 loop 当前作为 hole 跳过。

`Mesh(...)` 处理方式：

1. 把网格点追加到 `Vertices`。
2. 对每个 quad cell 输出两个三角面：

```text
(a, b, e), (a, e, d)
```

三角化完成后，`DwgExportContext.AppendMesh(...)` 会：

1. 对每个原始 `Point3d` 应用当前块嵌套变换。
2. 调用 `DwgUnitConverter.ToGltfPoint(...)` 转成 glTF 坐标。
3. 追加到材质 bucket 的 `Positions`。
4. 根据三角索引计算面积加权顶点法线并写入 `Normals`。
5. 把局部索引加上 `baseIdx` 后追加到 `Indices`。

这些数据先保存在图层的 `Triangles` bucket 中。最终 `EmitTriangleBuckets(...)` 会把每个材质 bucket 写成一个三角面 `GltfPrimitive`：普通路径写 `POSITION`、`NORMAL` 和 `indices` 三个 accessor；Draco 路径调用 `GltfBuilder.AddDracoPrimitive(...)`。

### 4.2 曲线采样为 LINES

`ProcessCurveToScene` 调用 `CurveSampler.Sample(curve)`，把 DWG 曲线离散成一串 `Point3d`。

支持路径：

- `Line`：起点和终点。
- `Arc`、`Circle`：按 chord height 计算角步长采样。
- `Ellipse`：固定 64 段采样。
- `Polyline`：逐段处理直线和 bulge arc。
- `Polyline2d`、`Polyline3d`：读取顶点。
- `Spline` 和其他 curve：按参数范围采样，默认 64 段。

采样结果是连续折线：

```text
pts[0] - pts[1] - pts[2] - ...
```

`AppendPolyline(...)` 转成 glTF `LINES` primitive 需要的索引对：

```text
(0,1), (1,2), (2,3), ...
```

最终 `LineBuilder.AddLinePrimitive(...)` 写出：

- `POSITION` accessor。
- `indices` accessor。
- `mode = 1`，即 glTF `LINES`。

线几何没有 `NORMAL`，也不走 Draco 压缩。即使 `EnableDraco = true`，线 primitive 仍然写成普通 bufferView + accessor。

### 4.3 文字、标注和非实心填充

`ProcessAnnotationToScene` 使用 `TextExploder.ExplodeRecursive(entity, maxDepth: 4)`。

处理方式：

1. 对 `DBText`、`MText`、`Dimension`、`Leader`、`MLeader`、非实心 `Hatch` 等复杂注记对象调用 AutoCAD `Entity.Explode(...)`。
2. 递归分解成更基础的 `Curve`、`Solid`、`Region` 或 `BlockReference`。
3. `Curve` 继续走曲线采样路径。
4. `Region` 继续走 `WorldDraw` 三角化路径。
5. 二维 `Solid` 走 `AppendFilledSolid(...)`。

二维 `Solid` 转面逻辑：

1. 读取 4 个点 `p0/p1/p2/p3`。
2. 用 `(p1 - p0) x (p2 - p0)` 算面法线。
3. 写 4 个顶点。
4. 输出两个三角面：

```text
(0,1,2), (1,3,2)
```

### 4.4 块参照

`ProcessBlockReferenceToScene` 会先取得块定义 `BlockTableRecord`，再判断是否可以做实例复用。

判断依据：

- `CanInstanceBlock(btr, tr)` 会分析块内容是否依赖插入时的颜色或图层。
- 如果块内容不依赖插入颜色/图层，则可以复用同一个 glTF mesh。
- 如果依赖插入颜色/图层，则必须展开块内容，让子实体继承当前块参照的上下文。

可复用块路径：

1. `GetOrBuildTemplateMesh(btr, tr)` 构建或读取缓存的块模板 mesh。
2. 模板 mesh 只构建一次，由 `BlockRefHandler` 按块定义 handle 缓存 mesh index。
3. 当前块参照写成一个 `GltfNode`：
   - `Mesh = templateMeshIndex`
   - `Matrix = DwgUnitConverter.ToGltfMatrix(context.Transform * br.BlockTransform, unitScale)`
   - `Extras = DwgPropertyCollector.Collect(br, tr)`
4. 该 node 放入当前图层 bucket 的 `InstanceNodes`。

不可复用块路径：

1. 创建新的 `EntityContext`。
2. 把父变换乘上 `br.BlockTransform`。
3. 把块参照的有效图层和颜色作为子实体继承上下文。
4. 递归处理块定义内的每个可见实体。

为避免死循环，块处理限制最大深度为 32，并通过 `BlockStack` 检测递归块引用。

## 5. 坐标和单位转换

`DwgUnitConverter` 负责单位和坐标系转换。

单位来源是 `Database.Insunits`。`UnitFactorToMeters(...)` 把 DWG 插入单位转换为米，例如：

- millimeters -> `0.001`
- meters -> `1.0`
- inches -> `0.0254`
- feet -> `0.3048`
- undefined -> `1.0`

坐标转换规则：

```text
AutoCAD 坐标：drawing unit，Z-up
glTF 输出坐标：meter，Y-up

point:  (X, Y, Z) -> (X, Z, -Y) * unitScale
normal: (X, Y, Z) -> (X, Z, -Y)
```

块参照矩阵使用 `ToGltfMatrix(Matrix3d m, double scale)` 写成 glTF node matrix。矩阵按 glTF 要求使用 column-major `float[16]`，并对 translation 应用单位缩放。

## 6. 图层、颜色和材质

CAD 导出按图层聚合几何。`LayerBucket` 包含：

```text
Triangles:     List<MeshPrimitiveBucket>
Lines:         List<MeshPrimitiveBucket>
InstanceNodes: List<GltfNode>
PerEntityExtras
```

颜色解析由 `ResolveEntityState(...)` 完成：

1. 图层名为空或 `0` 时继承父上下文图层。
2. `ByLayer` 使用有效图层颜色。
3. `ByBlock` 使用父上下文继承颜色。
4. `ByColor` 使用实体 true color。
5. ACI color 使用 `EntityColor.LookUpRgb(...)`。
6. 无法解析时使用默认灰色 `204,204,204`。

材质由 RGB 去重：

```text
key = rgb:R,G,B
name = color_R_G_B
baseColorFactor = [R/255, G/255, B/255, 1]
metallicFactor = 0
roughnessFactor = 0.8
```

## 7. 属性写入 extras

根对象 `GltfRoot.Extras`：

```json
{
  "schemaVersion": "1.0.0",
  "source": "AutoCAD",
  "unit": "meter",
  "originalUnit": "..."
}
```

图层 node 的 `extras`：

- `layer`：图层名。
- `layerColor`：图层 RGB。
- `entities`：当 `IncludeProperties = true` 时写入该图层下的实体属性数组。

块实例 node 的 `extras` 由 `DwgPropertyCollector.Collect(...)` 创建，并在写入图层 child 时补充 `layer`。

实体属性包括：

- `handle`
- `layer`
- `linetype`
- `lineweight`
- `color`
- `colorIndex`
- `xdata`
- `extDict`
- `entityType`

## 8. GLB 写入机制

共享模块 `Shared/GltfBuilder.cs` 负责组装 glTF 结构和二进制数据。

三角面 primitive 的写入方式：

```text
Positions + Normals + Indices
  -> accessors
  -> bufferViews
  -> primitive mode TRIANGLES
```

线 primitive 的写入方式：

```text
Positions + index pairs
  -> accessors
  -> bufferViews
  -> primitive mode LINES
```

如果 `EnableDraco = false`：

- 顶点属性写入普通 BIN bufferView。
- 三角面 primitive 的 `POSITION`、`NORMAL`、`indices` 都有实际 `bufferView`。
- 线 primitive 的 `POSITION` 和 `indices` 也都有实际 `bufferView`，`mode = 1`。

如果 `EnableDraco = true`：

- 三角面调用 `GltfBuilder.AddDracoPrimitive(...)`。
- Draco 数据写入一个无 `target` 的压缩 bufferView。
- primitive 写入 `KHR_draco_mesh_compression`，其中 `bufferView` 指向压缩字节，`attributes` 保存 glTF 属性名到 Draco attribute id 的映射。
- accessor 作为 shadow accessor 保留 `count/type/componentType/min/max` 等元数据，但不包含 `bufferView`。
- 线 primitive 仍保持未压缩。

普通三角面 primitive 的构造过程：

1. `EmitTriangleBuckets(...)` 对每个 `MeshPrimitiveBucket` 计算 `Positions` 的 `min/max`。
2. `AddFloat3Accessor(pb.Positions, min, max, GltfTarget.ArrayBuffer)` 创建 `POSITION` accessor。
3. `AddFloat3Accessor(pb.Normals, null, null, GltfTarget.ArrayBuffer)` 创建 `NORMAL` accessor。
4. `AddIndexAccessor(pb.Indices)` 创建 index accessor，bufferView 的 `target = ELEMENT_ARRAY_BUFFER`。
5. `GltfPrimitive.Attributes` 保存属性名到 accessor index 的映射，`GltfPrimitive.Indices` 保存索引 accessor index。

线 primitive 的构造过程：

1. `EmitLineBuckets(...)` 调用 `LineBuilder.AddLinePrimitive(pb.Positions, pb.Indices, pb.Material)`。
2. `LineBuilder` 为 `Positions` 创建 `POSITION` accessor，并写入 `min/max`。
3. `LineBuilder` 为线段索引创建 index accessor。
4. primitive 设置 `Mode = GltfPrimitiveMode.Lines`，即 JSON 中的 `mode = 1`。

Draco 三角面 primitive 的构造过程：

1. `EmitTriangleBuckets(...)` 调用 `GltfBuilder.AddDracoPrimitive(...)`。
2. `DracoNative.Encode(...)` 把 position、normal 和 triangle indices 编码为 Draco 字节；CAD 当前传入的 UV 为 `null`。
3. 压缩字节写入一个无 `target` 的 bufferView。
4. `extensions.KHR_draco_mesh_compression.bufferView` 指向压缩 bufferView。
5. `extensions.KHR_draco_mesh_compression.attributes` 保存 `POSITION`、`NORMAL` 到 Draco attribute id 的映射。
6. `AddShadowAccessor(...)` 保留解码后 accessor 的 `count/type/componentType/min/max`，用于符合 glTF 扩展结构。
7. `extensionsUsed` 和 `extensionsRequired` 都声明 `KHR_draco_mesh_compression`。

`WriteGlb(path)` 最终写出：

1. GLB header。
2. JSON chunk。
3. BIN chunk。

JSON 描述 scene、nodes、meshes、materials、accessors、bufferViews、buffers；BIN chunk 保存所有顶点、索引或 Draco 压缩数据。

## 9. 限制与注意事项

- CAD 导出只遍历 ModelSpace，不导出 paper space。
- 当前主结构按图层聚合，普通实体不会一实体一 node；实体级属性集中写入图层 node 的 `extras.entities`。
- `WorldDraw` 捕获依赖 AutoCAD 的显示离散结果，`FACETRES` 会影响曲面三角化质量。
- `Shell` 中的 hole loop 当前跳过，复杂带洞面可能需要后续增强。
- 块复用只在块内容不依赖插入颜色/图层时启用，否则展开以保证显示颜色正确。
- Draco 只压缩三角面，不压缩线几何。
