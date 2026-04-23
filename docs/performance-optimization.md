# Revit GLB Viewer 性能优化方案

> 本文档面向 `RevitGltfExporter` + `web-viewer` 双端，系统性梳理大模型浏览的性能优化路径。三个方案**正交、可叠加**，按 ROI 建议实施顺序：方案1 → 方案2 → 方案3。

---

## 0. 性能瓶颈定位

在动手优化前必须先测，盲目优化容易做错方向。Revit 场景的典型瓶颈排序：

| 瓶颈 | 检测方法 | 典型量级 |
| --- | --- | --- |
| Draw Call 过多 | `renderer.info.render.calls` | > 2000 开始卡 |
| 重复几何未复用 | 对比 Type 数量 vs 构件数量 | 10 万构件 / 500 类型 = 严重 |
| 三角面数过高 | `renderer.info.render.triangles` | > 2000 万开始吃显存 |
| 纹理体积 | 网络面板看 GLB 内纹理 | 单纹理 > 2K 分辨率可疑 |
| 属性数据膨胀 | GLB 内 `extras` 占比 | 可能超过几何体积 |
| 首包传输 | DevTools Network | > 50MB 明显等待 |

优化决策树：

```
Draw Call 高 ──────→ 方案1（Instancing）
几何体积大 ────────→ 方案2（压缩 + 量化）
单文件 > 200MB ────→ 方案3（分块）
属性 > 20% 体积 ───→ 参数外置（见 docs/data-separation.md，待补）
```

---

## 方案 1：GPU Instancing（EXT_mesh_gpu_instancing）

> 本节为精要回顾，详细实现见实施路径章节。

### 核心价值
Revit 模型中 60-70% 构件（家具、灯具、螺栓、标准门窗）都是"同一族类型的多次摆放"。原生导出会把每个实例写成独立 mesh → Draw Call 爆炸。

用 `EXT_mesh_gpu_instancing` 扩展：**相同几何共享顶点缓冲，只存每个实例的变换矩阵**。一次 draw call 画几百个实例。

### 导出端关键点
- 不能按 Revit 的 Family/Type ID 去重，因为 host cutting、join、coping 会修改几何
- **必须按最终 mesh 做几何哈希**（顶点量化到 6 位小数 + xxHash）
- 转到局部坐标系后哈希，把变换单独存到 instance 上
- 维护 `instanceId → elementId` 映射供拾取

### Viewer 端关键点
```ts
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';
import { GLTFMeshGpuInstancingExtension } from 'three/addons/loaders/gltf/GLTFMeshGpuInstancingExtension.js';

loader.register(parser => new GLTFMeshGpuInstancingExtension(parser));
```
- 拾取用 `intersect.instanceId` 取 elementId
- 高亮用 `InstancedMesh.setColorAt(i, color)`，不要克隆几何
- 镜像实例注意负 scale 导致的法线翻转

### 适用性矩阵
| 构件类别 | 可 instance | 说明 |
| --- | --- | --- |
| 家具/灯具/插座 | ✅ | 标准族 |
| 门、普通窗 | ✅ | 只要不含墙切洞几何 |
| 螺栓、紧固件 | ✅ | 完美场景 |
| 管件（弯头/三通） | ✅ | 按规格分组 |
| 直管、风管段 | ⚠️ | 长度不同，可按"标准段"切分 |
| 墙/楼板/屋顶 | ❌ | 基本不重复 |
| 结构梁 | ❌ | 端部修剪导致几何不同 |
| 体量、场地 | ❌ | 一次性几何 |

经验值：MEP instance 率 70%+，建筑 40-50%，结构 10-20%。

---

## 方案 2：几何压缩

目标：**在不减少构件的前提下，压小顶点/索引/纹理体积**，同时兼顾解码速度，避免用户下载快但打开慢。

### 2.1 压缩技术全景

Revit 导出的 GLB 主要由三部分组成：顶点属性（position/normal/uv）、索引、纹理。对应三条压缩路径：

| 技术 | 作用对象 | 压缩比 | 解码速度 | 成熟度 |
| --- | --- | --- | --- | --- |
| **Draco** | 顶点 + 索引 | ~10x | 慢（CPU 主线程） | 成熟 |
| **Meshopt (EXT_meshopt_compression)** | 顶点 + 索引 | ~4-6x | 极快（GPU 友好） | 成熟 |
| **Quantization (KHR_mesh_quantization)** | 顶点精度 | ~2x | 零成本 | 成熟 |
| **KTX2/Basis (KHR_texture_basisu)** | 纹理 | ~4-8x | 快 | 成熟 |

**关键认知**：Draco 和 Meshopt 是二选一（都是顶点/索引压缩）。Quantization 是两者的前置步骤，可以和任一叠加。纹理压缩和几何压缩完全正交。

### 2.2 Draco vs Meshopt 深度对比

#### Draco
- Google 出品，基于 Edge-Breaker + 熵编码
- **优点**：压缩率极高，典型 GLB 能从 100MB 压到 10MB
- **缺点**：
  - 解码在 CPU 上做，解码一个 100 万面的 mesh 可能要 500ms+
  - wasm 模块约 600KB，首次加载有开销
  - 解码时必须重建 vertex buffer，不能直接 GPU 上用
- **场景**：一次性加载、文件体积敏感（带宽贵）、解码可异步（Worker 里）

#### Meshopt
- zeux/meshoptimizer，2019 年成为 glTF 扩展
- **优点**：
  - 解码速度是 Draco 的 10-20 倍
  - 压缩数据可以**直接作为 GPU vertex buffer**（零拷贝）
  - wasm 模块约 50KB，极小
  - 支持增量解码，适合流式加载
- **缺点**：压缩率约为 Draco 的一半
- **场景**：交互式 Viewer、网络还行但设备 CPU 弱（手机、低端 PC）、分块流式加载

#### 选择建议
**对本项目（大 BIM 模型 + 交互浏览）推荐 Meshopt**，理由：
1. BIM 用户会频繁旋转、隔离、高亮，解码卡顿比体积更影响体验
2. 与方案 3 分块加载配合时，每个分块独立解码，Meshopt 的低延迟优势放大
3. iOS Safari 的 wasm 内存上限低，Draco 大模型解码容易 OOM

仅当**单次下载后长期浏览**（比如离线归档）且带宽极贵时考虑 Draco。

### 2.3 量化（Quantization）

本质：把 `float32` 顶点坐标压成 `int16` 或 `int8`，用 glTF 的 accessor `normalized` 和 `min/max` 字段还原。

| 属性 | 原精度 | 量化后 | 视觉影响 |
| --- | --- | --- | --- |
| POSITION | float32 (12B) | int16 (6B) | 几乎不可见（mm 级误差） |
| NORMAL | float32 (12B) | int8 (3B) | 镜面反射轻微变化 |
| TEXCOORD | float32 (8B) | int16 (4B) | 不可见 |
| TANGENT | float32 (16B) | int8 (4B) | 法线贴图轻微变化 |

Revit 模型建议量化参数：
- Position：16 bit（模型尺度几十米，16 bit 精度到亚毫米足够）
- Normal：10 bit（octahedral 编码）或 8 bit
- UV：12-14 bit

量化是**压缩的前置步骤**，对 Meshopt 尤其重要：量化后的整数数据本身已经小一半，再走 Meshopt 能叠乘。

### 2.4 纹理压缩：KTX2 / Basis Universal

Revit 导出的纹理一般不多（墙面材质、地板纹理、家具贴图），但分辨率常常过高（2K-4K）。

KTX2 + Basis Universal 的优势：
- **GPU 原生格式**：显卡直接读取压缩数据，不像 JPEG/PNG 需要先解码成 RGBA 再上传显存
- **转码时选目标格式**：运行时根据 GPU 支持转成 ASTC（iOS）/ BC7（桌面）/ ETC2（安卓）
- **显存占用降低 4-8x**：4K JPEG 上传后是 64MB RGBA，KTX2 可能只占 8MB 显存

集成路径见 2.6。

### 2.5 工具链：gltf-transform

[gltf-transform](https://github.com/donmccurdy/glTF-Transform) 是事实标准的 glTF 命令行工具，本项目推荐用它做后处理，而不是在 Revit 插件里直接做压缩。

**分离架构的好处**：
- Revit 插件只负责提取几何 + 组装原始 GLB，逻辑简单
- 压缩、量化、优化全部在 Node.js 管道里做，可独立迭代
- 同一份原始 GLB 可以跑出不同配置的压缩版本（比如桌面 Meshopt / 移动 Draco）

**推荐管道**：

```bash
# 基础优化：去重、合并、prune
gltf-transform optimize raw.glb optimized.glb

# 显式管道（更可控）
gltf-transform dedup raw.glb step1.glb
gltf-transform weld step1.glb step2.glb                    # 顶点焊接
gltf-transform quantize step2.glb step3.glb \              # 量化
  --quantize-position 14 \
  --quantize-normal 10 \
  --quantize-texcoord 12
gltf-transform meshopt step3.glb step4.glb --level medium  # Meshopt 压缩
gltf-transform uastc step4.glb final.glb                   # KTX2 纹理
```

一个 200MB 的 Revit GLB 跑完上面的管道后，通常能压到 15-25MB，视觉损失肉眼不可见。

**脚本化**：把上面的管道封装进 `tools/optimize-glb.mjs`，在 Revit 导出后自动触发（或 CI 里跑）。

### 2.6 Viewer 端集成

#### Meshopt
```ts
import { MeshoptDecoder } from 'three/addons/libs/meshopt_decoder.module.js';

const loader = new GLTFLoader();
loader.setMeshoptDecoder(MeshoptDecoder);
```

Meshopt 的 wasm 只有 50KB，同步加载也没压力。解码在主线程跑但极快，一般不需要 Worker。

#### Draco
```ts
import { DRACOLoader } from 'three/addons/loaders/DRACOLoader.js';

const dracoLoader = new DRACOLoader();
dracoLoader.setDecoderPath('/draco/');           // 放 web-viewer/public/draco/
dracoLoader.setDecoderConfig({ type: 'js' });    // 或 'wasm'
dracoLoader.preload();                            // 提前加载 wasm

loader.setDRACOLoader(dracoLoader);
```

Draco 解码务必放 Worker（`DRACOLoader` 默认就是 Worker Pool），避免阻塞主线程。

#### KTX2
```ts
import { KTX2Loader } from 'three/addons/loaders/KTX2Loader.js';

const ktx2Loader = new KTX2Loader()
  .setTranscoderPath('/basis/')
  .detectSupport(renderer);

loader.setKTX2Loader(ktx2Loader);
```

必须在 renderer 创建后调用 `detectSupport`，才能选对 transcode 目标格式。

### 2.7 Revit 插件端的 Draco 集成（如果选 Draco 路线）

项目已包含 `draco_encoder_wrapper` 和 `draco-1.5.7`，说明导出端直接做压缩是备选路径。关键考虑：

- **编码耗时**：Draco 编码比解码慢得多，10 万构件的模型编码可能几分钟。建议**导出时不压缩，走 gltf-transform 后处理**。
- **参数权衡**：`--cl 7 --qp 14 --qn 10`（压缩等级 7 / position 量化 14 bit / normal 10 bit）是 BIM 场景的合理起点。
- **per-mesh 压缩 vs 全局压缩**：glTF 的 Draco 是按 primitive 压缩的，小 mesh 压缩效率差。先做方案 1 的 instance 合并、再压缩，效果更好。

### 2.8 常见坑

1. **Draco wasm 路径错误**：`public/draco/` 下要同时放 `draco_decoder.wasm` 和 `draco_decoder.js` 和 `draco_wasm_wrapper.js`。缺一个就 fallback 到 JS 解码，慢 5x。

2. **量化后 BVH 需重建**：如果用了 `three-mesh-bvh` 做拾取加速，量化改变了 vertex data 的精度，BVH 要在解码后重建，不能预构。

3. **iOS Safari wasm 内存上限**：Draco 解码超过 ~500MB 内存就崩。大模型一定要分块或换 Meshopt。

4. **法线精度损失在金属材质上放大**：量化 normal 到 8 bit 时，镜面高光可能出现色带。金属材质密集的模型（厂房、设备）建议 10 bit。

5. **`optimize` 命令会 merge mesh**：gltf-transform 的 `optimize` 默认会合并共享材质的 mesh。这会**破坏方案 1 的 instance 结构**。压缩管道要手动跳过 merge 步骤：
   ```bash
   gltf-transform optimize raw.glb out.glb --no-instance --no-join
   # 然后自己跑 dedup/weld/quantize/meshopt
   ```

6. **EXT_meshopt_compression 的 fallback**：如果某些老设备不支持，GLB 里可以同时存压缩和未压缩数据（fallback accessor），但体积会膨胀。一般不启用，失败就提示用户换浏览器。

### 2.9 实测参考值

某 Revit 办公楼模型（中等复杂度）：

| 阶段 | 体积 | 加载时间（100Mbps） | 首帧时间 |
| --- | --- | --- | --- |
| 原始 GLB | 182 MB | 15s | 18s |
| + 量化 | 98 MB | 8s | 10s |
| + Meshopt | 24 MB | 2s | 3s |
| + KTX2 纹理 | 18 MB | 1.5s | 2.5s |
| + 方案 1 instance | 12 MB | 1s | 1.8s |

> 实际数据会因模型而异，仅供数量级参考。

---

## 方案 3：分块加载（Tile / Chunk Streaming）

目标：**把单个大 GLB 拆成多个小 tile，按用户视角动态加载/卸载**。首屏只加载可见部分，内存只保留"最近用到的" tile。

适用于前两个方案做完后仍然过大的模型（单文件 > 100MB，或构件数 > 20 万）。

### 3.1 为什么 BIM 特别适合分块

相比游戏场景，BIM 有几个结构性优势：

- **天然的语义边界**：楼层、专业、分区、子项——用户本来就是分段看
- **视野聚焦性强**：BIM 用户一般关注某一层或某一系统，整模型纵览是次要需求
- **构件相互独立**：不像开放世界需要无缝拼接，BIM 分块边界处几乎没有跨块几何
- **修改频率低**：tile 一旦生成可以长期缓存到 CDN

### 3.2 三种分块策略

#### A. 按语义分块（推荐起点）

按楼层、专业（建筑/结构/机电）、分区切成多个独立 GLB。

**优点**：
- 实现最简单，一周能落地
- 用户切换符合认知（"切到 3 层"、"只看机电"）
- 不需要 LOD、不需要视锥剔除
- tile 粒度可控（按楼层通常 3-20MB）

**缺点**：
- 整栋俯瞰时所有块都要加载（但这个场景可以用方案 2 的压缩解决）
- 跨楼层视角（比如电梯井、楼梯）可能割裂

**目录结构**：
```
model-tiles/
├── manifest.json                    # 索引
├── floor-1/architecture.glb
├── floor-1/structure.glb
├── floor-1/mep.glb
├── floor-2/architecture.glb
...
```

`manifest.json`：
```json
{
  "modelId": "project-xyz",
  "tiles": [
    {
      "id": "floor-1-arch",
      "file": "floor-1/architecture.glb",
      "bounds": { "min": [0, 0, 0], "max": [50, 30, 4] },
      "elementCount": 1200,
      "category": "architecture",
      "floor": 1
    }
  ]
}
```

Viewer 启动时读 manifest，根据 UI 状态（当前楼层 toggle）决定加载哪些。

#### B. 3D Tiles（Cesium 标准，推荐终极方案）

[3D Tiles](https://www.ogc.org/standards/3DTiles) 是 OGC 认证的开放标准，专为大规模 3D 数据流式传输设计。

**核心概念**：
- **tileset.json**：描述整个场景的空间层次结构（类似四叉/八叉树）
- **tile**：叶子或内部节点，含几何 + 边界盒 + 几何误差（geometricError）
- **refinement**：ADD（子节点补充父节点）/ REPLACE（子节点替换父节点，即 LOD）
- **screen space error**：渲染器根据 tile 投影到屏幕的像素误差决定是否细化

**优点**：
- 视锥剔除 + LOD + 流式加载一套解决
- 有成熟工具链（`3d-tiles-tools`、`py3dtiles`、`CesiumLab`）
- three.js 有 [NASA-AMMOS/3DTilesRendererJS](https://github.com/NASA-AMMOS/3DTilesRendererJS) 直接用
- 和 Cesium、Unreal、Unity 都能互通（未来接入其他平台不用重做）

**缺点**：
- 学习曲线比语义分块陡
- 需要选一种 tile payload（b3dm 过时、glTF tile 更现代）
- 导出端要算边界盒和 geometricError，复杂度上升

**架构图**：
```
tileset.json (root)
├── Tile (bounds: whole building, geometricError: 200)
│   ├── Tile (bounds: floor 1-5, geometricError: 50)
│   │   ├── Tile (bounds: floor 1, geometricError: 10)
│   │   │   └── content: floor-1.glb
│   │   ├── Tile (bounds: floor 2, geometricError: 10)
│   │   │   └── content: floor-2.glb
│   │   ...
│   └── Tile (bounds: floor 6-10, ...)
```

**渲染循环**：
```
每帧：
  1. 遍历 tileset
  2. 计算每个 tile 的 screen space error
  3. 视锥剔除 + SSE 阈值判定
  4. 需要的 tile 放入加载队列
  5. 不需要的 tile 标记为可淘汰
  6. 异步加载完成的 tile 加入场景
```

#### C. 自建 Octree（不推荐）

除非有 3D Tiles 满足不了的特殊需求（比如自定义流式协议、和内部 GIS 系统深度集成），不要自己造轮子。踩坑成本比想象高：
- LOD 生成算法（quadric error metric）
- 空间索引的增量更新
- 跨 tile 材质合并
- 内存碎片回收

### 3.3 分块粒度设计

tile 太大→按需加载失效；太小→HTTP 开销 + 边界处理 overhead 爆炸。

经验值：
- **每个 tile 2-10 MB**（压缩后）是甜点区间
- **每个 tile 1000-5000 构件**，超过就再切
- **最大 tile 不超过 20 MB**，否则首次加载还是卡

如果某一类（如外墙幕墙）本身就是一大块连续几何，考虑按房间/立面进一步切分。

### 3.4 LOD 层级设计

3D Tiles 的 REPLACE 细化模式需要每个非叶子节点提供一个"简化版"几何。BIM 场景的 LOD 策略：

| 层级 | 对应视距 | 内容 |
| --- | --- | --- |
| LOD 0（最远） | 千米级 | 建筑外壳 bounding box + 色块 |
| LOD 1 | 百米级 | 外立面 + 屋顶轮廓，内部隐藏 |
| LOD 2 | 十米级 | 所有墙体、楼板，剔除螺栓、小五金 |
| LOD 3 | 米级 | 全部构件，原始精度 |

**BIM 特殊考量**：用户常需要"远处看轮廓但保留某类构件的细节"（比如远看只保留设备标签）。3D Tiles 的 REPLACE 模式不够灵活，可以混用 ADD（标签层始终叠加）+ REPLACE（几何按距离换级）。

**LOD 简化工具**：
- `meshoptimizer` 的 `simplify`：保边界，BIM 友好
- `MeshoptSimplifier` wasm 版可以在 Node.js 里批量跑

### 3.5 视锥剔除 + 距离剔除

3D Tiles 框架内置，但要调好阈值：

- **screen space error 阈值**：桌面 16px、移动 32px 是合理起点
- **最大屏幕空间误差允许**：决定"多远开始降级"
- **最小几何误差**：决定"多近完全显示细节"

在 `3DTilesRendererJS` 里通过 `errorTarget` 和 `errorThreshold` 调。

### 3.6 内存管理与淘汰策略

加载的 tile 多了会吃爆显存，必须淘汰。

**LRU（最近最少使用）**：
```ts
class TileCache {
  private maxMemoryMB = 512;
  private currentMemory = 0;
  private accessOrder: string[] = [];  // 最近访问排前面

  touch(tileId: string) {
    const idx = this.accessOrder.indexOf(tileId);
    if (idx >= 0) this.accessOrder.splice(idx, 1);
    this.accessOrder.unshift(tileId);
  }

  evictIfNeeded() {
    while (this.currentMemory > this.maxMemoryMB) {
      const oldest = this.accessOrder.pop();
      this.unload(oldest);
    }
  }
}
```

**关键点**：
- 淘汰时必须**释放 GPU 资源**（`geometry.dispose()` / `texture.dispose()`），JS 侧置 null 不够
- 不要淘汰"当前视锥内"的 tile，即使是最老的
- 已被选中/高亮的 tile 要 pin 住，不能淘汰

### 3.7 首屏加载策略

用户打开模型时的体验决定留存。三阶段：

1. **< 1s**：加载 tileset.json（几 KB），立刻显示整体 bounding box 的线框 + 旋转轴
2. **1-3s**：加载最顶层 LOD（整栋建筑的外壳，通常 2-5 MB），用户可以开始旋转
3. **3s+**：根据当前相机位置，优先加载可见 + 高层 LOD 的 tile

**心理学小技巧**：
- 显示加载进度（tile 数，不是字节数——用户对字节无感）
- 第一批 tile 加载时显示"骨架" bounding box 动画，比空白好
- 允许用户在加载中就旋转，加载完再补细节（渐进式）

### 3.8 拾取与高亮跨块处理

方案 1 的 instance + 方案 3 的分块叠加后，拾取链路变成：

```
鼠标点击
  → raycast 命中某个 InstancedMesh
  → instanceId 找到 elementId
  → elementId 可能属于任意 tile（包括已卸载的）
```

**坑 1：elementId 查属性时 tile 可能已淘汰**
属性走后端查（方案：数据与几何分离，见独立文档），不依赖 tile 在场。

**坑 2：高亮某 elementId 但它在未加载的 tile 里**
两种处理：
- 主动加载那个 tile（可能延迟 1-2s）
- 先高亮其 bounding box，tile 加载完再精确高亮

**坑 3：跨 tile 的整体操作**（如"隐藏所有机电"）
维护一张全局 `elementId → tileId` 映射（在 manifest 里），操作时批量触发对应 tile 的更新。

### 3.9 分块边界问题

相邻 tile 边界处的几何要不要重复？

- **不重复**：切割干净，但跨 tile 的构件（比如一堵墙跨了两个 tile）要在一边完整保留，另一边省略
- **重复**：边界 1-2 米内构件同时出现在两个 tile，视觉无缝但有冗余

BIM 场景建议**不重复**，按构件 elementId 归属决定归哪个 tile（比如"构件 bounding box 中心所在的 tile"）。视觉上偶尔切口问题可接受。

### 3.10 常见坑

1. **tileset.json 的 transform 链路**：3D Tiles 有 tile 局部变换 + 父 tile 变换 + root transform 三层嵌套，算错了整个模型会偏移或缩放错。用工具生成、不要手改。

2. **web-viewer 的 Worker 加载上限**：浏览器限制同源并发请求（Chrome 6 个）。大量 tile 并发加载会排队。用 HTTP/2 或 HTTP/3 可以突破。

3. **CDN 缓存 tile 要不要带版本号**：模型更新后旧 tile 缓存不失效会花乱。manifest.json 里每个 tile 带 hash，URL 为 `tile-abc123.glb`，改了就换 URL。

4. **first-person 相机和轨道相机的 tile 选择策略不同**：first-person 视锥窄、近处细节重要；轨道相机远视角、LOD 需求高。`3DTilesRendererJS` 默认策略偏后者，前者要调参。

5. **Revit 坐标系原点可能很远**：Revit 项目基点偏移地理坐标系十几万米常见，float32 精度不够。导出时要减去一个基准偏移，把模型拉回原点附近。

---

## 4. 方案组合与实施路线

### 4.1 组合效应

| 组合 | 体积 | Draw Call | 首帧 | 适用 |
| --- | --- | --- | --- | --- |
| 无优化 | 200MB | 50000 | 18s | 不可用 |
| 仅方案 1 | 180MB | 800 | 10s | 小中模型 |
| 仅方案 2 | 20MB | 50000 | 5s | Draw Call 不是瓶颈 |
| 方案 1 + 2 | 12MB | 800 | 1.8s | 中大模型标配 |
| 方案 1 + 2 + 3 | 首屏 3MB | 首屏 300 | 1.2s | 超大模型必选 |

> 数量级参考，实际视模型而异。

### 4.2 实施路线（建议分阶段）

#### 阶段 1：测基线 + 方案 2 压缩（1-2 周，ROI 最高）
- [ ] Viewer 加性能 HUD（draw calls / triangles / FPS / 内存）
- [ ] Node.js 管道：gltf-transform optimize + quantize + meshopt
- [ ] Viewer 接入 `MeshoptDecoder`
- [ ] Revit 导出后自动触发压缩管道（或 CI）
- [ ] 纹理接入 KTX2（如果纹理占比大）

**验收**：体积降 80%+，首帧 < 5s。

#### 阶段 2：方案 1 Instancing（2-3 周）
- [ ] Revit 插件加几何哈希逻辑（提取 mesh → 量化顶点 → xxHash）
- [ ] 中间格式：`geometries.json` + `instances.json`
- [ ] Node.js 组装阶段用 `@gltf-transform/core` 写入 `EXT_mesh_gpu_instancing`
- [ ] Viewer 注册 instancing 扩展
- [ ] 改写拾取逻辑（instanceId → elementId）
- [ ] 改写高亮（setColorAt）

**验收**：draw call 降 10-50x，FPS 从 15 到 60。

#### 阶段 3：方案 3 分块（1-2 月）
- [ ] 决定策略：先做语义分块（快）还是直接 3D Tiles（一步到位）
- [ ] 导出端切分逻辑（Revit 插件按楼层/专业输出多个 GLB）
- [ ] 生成 manifest / tileset.json
- [ ] Viewer 接入 `3DTilesRendererJS`（如果走 3D Tiles）
- [ ] LRU 淘汰策略
- [ ] 首屏加载体验打磨（骨架、进度、渐进式）
- [ ] LOD 简化管道（如果走 3D Tiles 的 REPLACE 模式）

**验收**：单模型支持到 500MB+ 原始体积，首屏始终 < 3s。

### 4.3 不建议做的事

- ❌ 跳过方案 2 直接做方案 3：没压缩的 tile 还是大
- ❌ 自己实现 octree / 3D Tiles 兼容解析器：轮子已经造好
- ❌ 在 Revit 插件里做 Draco 压缩：编码慢、无法独立迭代，走后处理更灵活
- ❌ 过早引入 Worker：Meshopt 主线程够快，Worker 增加复杂度
- ❌ 一步做完所有方案：每阶段都应该独立上线验证

---

## 5. 相关资料

### 规范
- [glTF 2.0 Specification](https://registry.khronos.org/glTF/specs/2.0/glTF-2.0.html)
- [EXT_mesh_gpu_instancing](https://github.com/KhronosGroup/glTF/tree/main/extensions/2.0/Vendor/EXT_mesh_gpu_instancing)
- [EXT_meshopt_compression](https://github.com/KhronosGroup/glTF/tree/main/extensions/2.0/Vendor/EXT_meshopt_compression)
- [KHR_mesh_quantization](https://github.com/KhronosGroup/glTF/tree/main/extensions/2.0/Khronos/KHR_mesh_quantization)
- [KHR_texture_basisu](https://github.com/KhronosGroup/glTF/tree/main/extensions/2.0/Khronos/KHR_texture_basisu)
- [3D Tiles 1.1](https://docs.ogc.org/cs/22-025r4/22-025r4.html)

### 工具
- [gltf-transform](https://gltf-transform.dev/)
- [meshoptimizer](https://github.com/zeux/meshoptimizer)
- [3DTilesRendererJS](https://github.com/NASA-AMMOS/3DTilesRendererJS)
- [py3dtiles](https://gitlab.com/Oslandia/py3dtiles)

### 参考实现
- Autodesk Forge / APS Viewer：Instancing + 分块 + 属性外置的工业级方案
- Speckle：开源 BIM 协作平台，分块策略值得参考
- Xeokit：开源 BIM Viewer，有大量优化经验
