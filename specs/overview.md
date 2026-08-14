# 总体业务规格

## 目标

将 Revit 2019–2027 或 AutoCAD 2020–2024 中的可见模型导出为可在浏览器打开的 `.glb`，保留几何、材质和可查询属性，并支持可选 Draco 三角网格压缩。Viewer 必须能在不依赖 Autodesk 宿主的情况下加载、浏览和检查文件。Revit 2019–2024 使用 .NET Framework 4.8，Revit 2025–2026 使用 .NET 8（Windows），Revit 2027 使用 .NET 10（Windows）。

## 模块边界

- `src/RevitGltfExporter`：只负责 Revit 视图数据采集与 Revit 属性/材质映射；按 `Core`、`Adapters` 和参数化宿主项目分层。
- `src/RevitGltfExporter/Core`：不引用任何 Revit API，负责版本无关的导出流程、几何/材质/属性中间模型和错误处理。
- `src/RevitGltfExporter/Adapters`：由版本条件编译引用对应版本的 Revit API，将宿主 API 转换为 Core 接口；单位、ElementId 和参数 API 差异必须收敛在此层。
- `src/RevitGltfExporter/RevitGltfExporter/RevitGltfExporter.csproj`：唯一的 SDK-style 参数化宿主项目，默认按 `RevitVersion` 选择目标框架并引入 `Revit_All_Main_Versions_API_x64` 的对应 NuGet API 包，负责外部命令、UI、宿主生命周期，并生成独立程序集和 `.addin` 清单。
- `src/AutoCadGltfExporter`：只负责 DWG ModelSpace 实体、图层、块和 CAD 属性采集。
- `src/Shared`：负责与宿主无关的 glTF/GLB 结构、二进制 buffer、材质和 Draco 调用。
- `src/draco_encoder_wrapper`：构建 `draco_encoder.dll`，不改变导出业务语义。
- `src/web-viewer`：只消费符合 `glb-contract.md` 的 GLB，不读取 Revit/AutoCAD API。

## 共通验收

1. 生成文件是可被标准 glTF 2.0 loader 打开的 GLB 2.0。
2. 输出坐标为右手系、Y-up、米；导出失败不得留下未完成的目标文件。
3. 关闭属性或 Draco 选项时，不应写入对应的可选数据或扩展。
4. 失败、取消和宿主版本/路径缺失都必须给出可诊断信息，且恢复宿主临时状态。
5. 首期支持矩阵中的每个 Revit 版本必须独立编译、安装和手工验证；一个版本的 Revit API 程序集不得由另一个版本的插件运行时加载。
6. Tag Release 流水线必须为支持矩阵中的每个 Revit 版本生成独立可安装资产，不得只发布默认版本。
