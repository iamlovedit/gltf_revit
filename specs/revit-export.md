# Revit 多版本导出规格

## 版本支持与兼容策略

当前发布矩阵必须支持 Revit 2019–2027。每个 Revit 主版本生成独立的插件程序集和 `.addin` 清单，不使用一个 DLL 通过反射或动态绑定来兼容多个 Revit API 版本。

| Revit 版本 | 目标框架 | API 适配分组 | 状态 |
| --- | --- | --- | --- |
| 2019–2020 | `net48` | 传统单位/参数 API | 必须支持 |
| 2021–2024 | `net48` | ForgeTypeId 单位 API 及对应版本差异 | 必须支持 |
| 2025–2026 | `net8.0-windows` | 现代 .NET 宿主及 64 位 ElementId API | 必须支持 |
| 2027 | `net10.0-windows` | Revit 2027 API 包要求 .NET 10 | 必须支持 |
| 2028+ | 由对应 Revit API 包决定 | 沿用现代宿主分组，但新增年份前必须验证并登记 API 包版本与目标框架 | 后续扩展 |

版本适配必须遵守以下边界：

- `Core` 层不得引用 `RevitAPI.dll` 或 `RevitAPIUI.dll`。
- `Adapters` 通过 `RevitVersion` 对应的条件编译符号只引用并编译目标 Revit 主版本的 API 程序集；单位转换、参数读取和 `ElementId` 序列化等差异不得泄漏到 Core。
- 仓库只维护一个 SDK-style、参数化的 Revit `.csproj`；该项目按 `RevitVersion` 在 `net48`、`net8.0-windows` 与 `net10.0-windows` 之间选择目标框架，负责外部命令、WPF UI、宿主生命周期和版本清单，业务导出流程由 Core 复用。每次构建仍只生成一个目标版本的插件程序集。
- 适配器必须将 ElementId 转成不依赖位宽的十进制字符串，写入 GLB 的 `elementId`，以兼容包含 64 位 ID 的版本。
- 默认使用 NuGet 包 `Revit_All_Main_Versions_API_x64` 提供编译期 API 引用，包版本必须由 `RevitVersion` 严格推导为 `<RevitVersion>.0.0`，例如 Revit 2019 必须使用 `2019.0.0`，不得自动选择同一主版本的更新包。只有显式启用 `UseLocalRevitReferences=true` 时才使用 `RevitInstallPath`。本机 Revit 运行时仍由宿主加载，不把 API DLL 复制进插件输出目录。NuGet 包版本与 `RevitVersion` 不匹配时，或启用本机引用时路径缺失、API 程序集不存在、API 程序集主版本与目标年份不匹配时，构建必须失败并给出明确诊断；同一主版本的 Revit 更新版程序集允许用作本机引用。

## 输入与入口

- 命令从对应版本的 Revit 外部命令进入。Revit 2019–2024 目标平台为 x64、`net48`，Revit 2025–2026 为 x64、`net8.0-windows`，Revit 2027 为 x64、`net10.0-windows`；所有版本由同一个 SDK-style 项目分别引用目标版本的 Revit API。
- 当前活动视图必须是 `View3D`；否则返回取消并提示用户切换三维视图。
- 导出前显示选项：`EnableDraco`、`DracoCompressionLevel`（0–10，默认 7）、`IncludeProperties`（默认开启），随后选择 `.glb` 保存路径。

## 几何、材质与属性

- 使用 `CustomExporter`/`IExportContext` 读取当前视图渲染几何；只导出视图中可见内容，不开启事务修改模型。
- 按 Revit 图元创建 node，按材质创建 primitive；支持位置、法线、可用时的 UV 和三角索引。
- 坐标转换为 `(X, Z, -Y) * 0.3048`，法线执行同样的轴交换但不缩放。
- 材质映射到 PBR base color、alpha、metallic、roughness；透明材质使用 `alphaMode: BLEND`。
- node `extras` 至少包含 `elementId`；可包含 `category`、`family`、`type`。开启属性时写入参数字典，双精度参数由版本适配器转换为米、平方米等规范单位，不得把宿主内部单位直接写入 GLB。

## 构建、输出与安装

- 构建参数必须包含 `RevitVersion`；唯一的 SDK-style 项目文件根据它设置目标框架、条件编译符号、NuGet API 包版本、程序集名、输出目录和独立中间目录。Visual Studio 解决方案提供 `Revit2019-Debug` 至 `Revit2027-Release` 的版本专用配置，这些配置必须映射到对应的 `RevitVersion`，并将 `Debug`/`Release` 后缀映射到基础构建配置；命令行仍可直接传入 `/p:RevitVersion`。仅在 `UseLocalRevitReferences=true` 时要求对应的 `RevitInstallPath`，不得修改项目默认安装路径来适配本机环境。
- 每个版本的产物必须写入独立目录，例如 `output/Revit2019/`、`output/Revit2025/`、`output/Revit2027/`，目录内包含该版本插件程序集、`.addin` 清单、`GltfExporter.Shared.dll`、`Newtonsoft.Json.dll` 和运行所需的 `draco_encoder.dll`；2025+ 还必须部署对应的 `.deps.json`。
- `.addin` 清单必须安装到对应的 `%AppData%/Autodesk/Revit/Addins/<version>/` 目录，不得让不同版本共用清单或覆盖程序集。
- 安装脚本应支持显式版本列表，并在未找到目标版本安装目录时跳过或失败并报告原因；不得静默安装到其他 Revit 版本。
- Release 流水线默认必须构建 2019–2027 九个 Revit MSI，并与 AutoCAD MSI 一起上传；Revit MSI 文件名必须包含年份，SHA256 汇总必须覆盖全部 MSI。

推荐的单版本构建形式为：

```powershell
msbuild .\src\RevitGltfExporter\RevitGltfExporter\RevitGltfExporter.csproj /m /p:Configuration=Release /p:RevitVersion=2024 /p:Platform=x64

# Revit 2025+ 使用同一项目，项目会自动切换到对应的现代 .NET 目标框架
msbuild .\src\RevitGltfExporter\RevitGltfExporter\RevitGltfExporter.csproj /restore /m /p:Configuration=Release /p:RevitVersion=2025 /p:Platform=x64

# 可选：使用本机安装的 Revit API 做编译期校验
msbuild .\src\RevitGltfExporter\RevitGltfExporter\RevitGltfExporter.csproj /m /p:Configuration=Release /p:RevitVersion=2024 /p:Platform=x64 /p:UseLocalRevitReferences=true /p:RevitInstallPath="C:\Program Files\Autodesk\Revit 2024"
```

## 验收条件

1. 非三维视图不会创建文件并返回 `Cancelled`。
2. 普通输出的每个三角 primitive 有 `POSITION`、`NORMAL`、索引和材质；有 UV 时包含 `TEXCOORD_0`。
3. 选中 Draco 后仅三角 primitive 使用 `KHR_draco_mesh_compression`，Viewer 可正常解码。
4. 链接模型/族实例的变换不丢失；异常图元不会使整个导出静默成功。
5. 2019、2020、2021、2022、2023、2024、2025、2026、2027 均至少完成一次 Release 构建和宿主手工导出；同一模型在各版本生成的 GLB 必须满足 `glb-contract.md`，除版本诊断字段外，几何、材质和属性语义一致。
6. 每个版本的取消、失败和版本路径错误都必须返回可诊断结果，且不得留下未完成的 `.glb` 文件。
