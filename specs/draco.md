# Draco 原生压缩规格

## 构建与部署

- `draco_encoder_wrapper` 使用 CMake、C++14、静态链接仓库内固定的 `draco-1.5.7`，并启用 `DRACO_GLTF_BITSTREAM`。
- `powershell -ExecutionPolicy Bypass -File .\src\draco_encoder_wrapper\build.ps1 -Config Release` 必须生成 x64 `draco_encoder.dll` 并复制到 `output/`。
- C# 项目构建前必须检查对应配置的 DLL 存在；运行时 DLL 与插件程序集放在同一目录。

## 编码行为

- 压缩等级限定为 0–10；编码失败必须返回错误，不得写出宣称已压缩但无法解码的 GLB。
- 只编码三角网格的 position、normal 及可用 UV/索引；线几何保持未压缩。
- 输出必须符合 glTF `KHR_draco_mesh_compression`，由 Viewer 的 WASM `DRACOLoader` 解码。

## 验收条件

构建 Release/Debug 各至少验证一次；对同一模型比较普通与 Draco 输出均可在 Viewer 打开、材质/属性一致，且压缩路径失败时能显示明确错误。
