# Revit GLB 导出插件与 Web 查看器

本仓库包含三个主要部分：

- `RevitGltfExporter`：Revit 2019 插件，用于导出 `.glb` 文件。
- `draco_encoder_wrapper`：基于 Draco 的原生压缩 DLL，供 Revit 插件通过 P/Invoke 调用。
- `web-viewer`：基于 Vite + React + TypeScript + three.js 的前端 GLB 查看器。

## 展示

![](docs/viewer.png)
![](docs/viewer1.png)

## 环境要求

建议在 Windows 64-bit 环境下编译和安装插件。

- Windows 64-bit
- Revit 2019
- Visual Studio 2022 或可用的 MSBuild
- CMake
- PowerShell
- Node.js
- pnpm 9

> Revit 插件项目默认查找 Revit API 的路径为 `C:\Program Files\Autodesk\Revit 2019`。如果 Revit 安装在其他目录，需要在构建时覆盖 `RevitInstallPath`。

## 编译 Draco 原生库

`draco_encoder_wrapper` 会编译出 `draco_encoder.dll`，该 DLL 必须和 `RevitGltfExporter.dll` 放在同一目录下。

在仓库根目录打开 PowerShell，执行：

```powershell
cd .\draco_encoder_wrapper
powershell -ExecutionPolicy Bypass -File .\build.ps1 -Config Release
```

编译完成后，脚本会将 DLL 复制到：

```text
output\draco_encoder.dll
```

如果需要 Debug 版本，可以执行：

```powershell
cd .\draco_encoder_wrapper
powershell -ExecutionPolicy Bypass -File .\build.ps1 -Config Debug
```

## 编译 Revit 插件

编译 Revit 插件前，请先完成 Draco 原生库编译，确保以下文件存在：

```text
output\draco_encoder.dll
```

### 使用 Visual Studio 编译

1. 打开解决方案：

   ```text
   RevitGltfExporter\RevitGltfExporter.sln
   ```

2. 选择构建配置：

   ```text
   Release | x64
   ```

   或：

   ```text
   Debug | x64
   ```

3. 执行 Build。

插件编译产物会输出到：

```text
output\RevitGltfExporter.dll
```

### 使用 MSBuild 编译

如果 Revit 安装在默认路径，可以在仓库根目录执行：

```powershell
msbuild .\RevitGltfExporter\RevitGltfExporter.sln /p:Configuration=Release /p:Platform=x64
```

如果 Revit 安装在其他路径，通过 `RevitInstallPath` 覆盖：

```powershell
msbuild .\RevitGltfExporter\RevitGltfExporter.sln /p:Configuration=Release /p:Platform=x64 /p:RevitInstallPath="D:\Autodesk\Revit 2019"
```

如果编译 Debug 插件并使用 Debug 版本的 Draco DLL，需要同时指定：

```powershell
msbuild .\RevitGltfExporter\RevitGltfExporter.sln /p:Configuration=Debug /p:Platform=x64 /p:DracoEncoderConfiguration=Debug
```

## 安装到 Revit 2019

Revit 通过 `.addin` 文件加载插件。插件安装目录为：

```text
%ProgramData%\Autodesk\Revit\Addins\2019\
```

如果目录不存在，请先创建。

### 创建 addin 文件

在下面目录中新建文件：

```text
%ProgramData%\Autodesk\Revit\Addins\2019\RevitGltfExporter.addin
```

文件内容示例：

```xml
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>RevitGltfExporter</Name>
    <Assembly>C:\path\to\gltf_revit\output\RevitGltfExporter.dll</Assembly>
    <AddInId>4f8e3a1b-0a6d-4d1a-9a5e-7f2a1c9d3e4f</AddInId>
    <FullClassName>RevitGltfExporter.Application</FullClassName>
    <VendorId>LOCAL</VendorId>
    <VendorDescription>Internal</VendorDescription>
  </AddIn>
</RevitAddIns>
```

请将 `<Assembly>` 修改为本机仓库中 `RevitGltfExporter.dll` 的绝对路径，例如：

```xml
<Assembly>C:\work\gltf_revit\output\RevitGltfExporter.dll</Assembly>
```

安装时请确认 `output` 目录中至少包含：

```text
output\RevitGltfExporter.dll
output\draco_encoder.dll
```

`draco_encoder.dll` 必须和 `RevitGltfExporter.dll` 位于同一目录，否则启用 Draco 压缩导出时会加载失败。

完成后启动 Revit 2019，在 Add-Ins/插件入口中使用 GLB 导出命令。

## 启动前端项目

前端项目位于 `web-viewer`，使用 pnpm 管理依赖。

在仓库根目录执行：

```powershell
cd .\web-viewer
pnpm install
pnpm dev
```

开发服务器默认监听：

```text
http://localhost:5173
```

如果需要生产构建：

```powershell
pnpm build
```

构建完成后，可以本地预览：

```powershell
pnpm preview
```

## 常见问题

### 编译 Revit 插件提示找不到 `draco_encoder.dll`

请先编译 Draco 原生库：

```powershell
cd .\draco_encoder_wrapper
powershell -ExecutionPolicy Bypass -File .\build.ps1 -Config Release
```

然后重新编译 Revit 插件。

### 编译 Revit 插件提示找不到 `RevitAPI.dll`

确认已安装 Revit 2019，并检查安装路径。项目默认路径为：

```text
C:\Program Files\Autodesk\Revit 2019
```

如果安装在其他目录，请在 MSBuild 中传入：

```powershell
/p:RevitInstallPath="你的 Revit 2019 安装目录"
```

### Revit 启动后没有看到插件

请检查：

- `.addin` 文件是否位于 `%ProgramData%\Autodesk\Revit\Addins\2019\`。
- `.addin` 文件中的 `<Assembly>` 是否为 `RevitGltfExporter.dll` 的绝对路径。
- `output\RevitGltfExporter.dll` 是否存在。
- `output\draco_encoder.dll` 是否和插件 DLL 在同一目录。
