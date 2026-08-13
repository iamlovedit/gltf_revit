# Draco 原生压缩规格

## 构建与部署

- `draco_encoder_wrapper` 使用 CMake、C++14，默认通过 `FetchContent` 下载并静态链接 Draco `1.5.7`；归档 URL 和 SHA256 必须固定，源码不得直接提交到本仓库，并启用 `DRACO_GLTF_BITSTREAM`。
- 首次默认构建需要访问固定的官方归档；后续构建复用 `build/_deps`。离线构建必须支持通过 `build.ps1 -DracoSourceDir <path>` 指向预先准备的 Draco `1.5.7` 源码树。
- `powershell -ExecutionPolicy Bypass -File .\src\draco_encoder_wrapper\build.ps1 -Config Release` 必须生成 x64 `draco_encoder.dll` 并复制到 `output/`。
- C# 项目构建前必须检查对应配置的 DLL 存在；运行时 DLL 与每个 Revit 版本的插件程序集放在同一版本输出目录。该 native DLL 不包含 Revit API 依赖，可由所有受支持的 .NET Framework 4.8 宿主共用；.NET 8 宿主须单独验证加载路径。

## 编码行为

- 压缩等级限定为 0–10；编码失败必须返回错误，不得写出宣称已压缩但无法解码的 GLB。
- 只编码三角网格的 position、normal 及可用 UV/索引；线几何保持未压缩。
- 输出必须符合 glTF `KHR_draco_mesh_compression`，由 Viewer 的 WASM `DRACOLoader` 解码。

## 验收条件

构建 Release/Debug 各至少验证一次，并至少验证一次带 SHA256 校验的默认下载或本地源码覆盖配置；对同一模型比较普通与 Draco 输出均可在 Viewer 打开、材质/属性一致，且压缩路径失败时能显示明确错误。
