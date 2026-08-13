# Web 查看器规格

## 技术与输入

- 使用 Vite、React 18、TypeScript、three.js 和 pnpm 9；`pnpm typecheck` 必须通过后才可合并。
- 通过本地 `.glb` 文件选择器加载文件，使用 `GLTFLoader`；Draco 解码器从同源 `/draco/` WASM 资源加载。
- GLB 只按 `glb-contract.md` 解释，未知 `extras` 字段必须容错忽略。

## 加载与大模型体验

- 使用 `fetch` 的 `ReadableStream` 分块读取，按 `Content-Length` 更新进度；无长度时仍显示不超过 95% 的估算进度，解析完成后置为 100%。
- 使用 `GLTFLoader.parseAsync` 和 Draco worker 池（最多 4 个 worker），禁止在 React 渲染阶段同步解析模型。
- 将 drawable 对象按批次加入场景并预编译材质，避免一次性显示造成长时间主线程阻塞；取消/替换文件时释放旧 URL、几何、材质和 renderer 资源。

## 交互功能

- OrbitControls 支持左键旋转、滚轮缩放、中/右键平移；中键快速双击重置视角。
- 提供适应屏幕、重置、前后左右上下/轴测标准视图、透视/正交相机、线框、隐藏选中、隔离、显示全部和可拖动剖面框。
- 点击 mesh/line 显示 Revit element 或 AutoCAD layer/entity 属性，并对 mesh 高亮；AutoCAD 图层可逐层显示/隐藏。
- 性能浮层显示 FPS、帧耗时、三角形数和 Draw Call。

## 验收条件

1. 普通和 Draco GLB 均能打开，加载期间 UI 可操作且进度可见；网络/解析错误有可见错误信息。
2. Revit 属性面板显示 `elementId/category/family/type/parameters`；AutoCAD 图层和块属性可拾取。
3. 每个工具按钮在无场景、无选中对象和已有隐藏对象时保持正确禁用/恢复状态。
4. `pnpm build` 产出可部署的 `dist/`，并包含 Draco decoder 资源。
