# Revit 2019 导出规格

## 输入与入口

- 命令从 Revit 外部命令进入，目标平台为 x64、.NET Framework 4.8，引用 Revit 2019 API。
- 当前活动视图必须是 `View3D`；否则返回取消并提示用户切换三维视图。
- 导出前显示选项：`EnableDraco`、`DracoCompressionLevel`（0–10，默认 7）、`IncludeProperties`（默认开启），随后选择 `.glb` 保存路径。

## 几何、材质与属性

- 使用 `CustomExporter`/`IExportContext` 读取当前视图渲染几何；只导出视图中可见内容，不开启事务修改模型。
- 按 Revit 图元创建 node，按材质创建 primitive；支持位置、法线、可用时的 UV 和三角索引。
- 坐标转换为 `(X, Z, -Y) * 0.3048`，法线执行同样的轴交换但不缩放。
- 材质映射到 PBR base color、alpha、metallic、roughness；透明材质使用 `alphaMode: BLEND`。
- node `extras` 至少包含 `elementId`；可包含 `category`、`family`、`type`。开启属性时写入参数字典，双精度参数按 Revit 2019 显示单位转换。

## 验收条件

1. 非三维视图不会创建文件并返回 `Cancelled`。
2. 普通输出的每个三角 primitive 有 `POSITION`、`NORMAL`、索引和材质；有 UV 时包含 `TEXCOORD_0`。
3. 选中 Draco 后仅三角 primitive 使用 `KHR_draco_mesh_compression`，Viewer 可正常解码。
4. 链接模型/族实例的变换不丢失；异常图元不会使整个导出静默成功。
