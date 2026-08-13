# Repository Guidelines

## Project Structure & Module Organization

- `src/RevitGltfExporter/` contains the Revit 2019 x64 plug-in; export callbacks and collectors are under `Export/`.
- `src/AutoCadGltfExporter/` contains the AutoCAD 2020–2024 plug-in, with bundle metadata under `Bundle/`.
- `src/Shared/` holds the common .NET Framework 4.8 GLB schema, builders, options, and Draco interop used by both plug-ins.
- `src/draco_encoder_wrapper/` builds the native `draco_encoder.dll`; CMake fetches the pinned Draco source archive, while `-DracoSourceDir` supports offline builds.
- `src/web-viewer/` is the Vite + React + TypeScript + three.js viewer (`src/viewer/` contains viewer features). `docs/` contains implementation and performance notes; `scripts/` contains installation automation.

## Build, Test, and Development Commands

Run from the repository root in PowerShell (Windows, Visual Studio/MSBuild, CMake, and pnpm 9 are expected):

```powershell
powershell -ExecutionPolicy Bypass -File .\src\draco_encoder_wrapper\build.ps1 -Config Release
msbuild .\src\RevitGltfExporter\RevitGltfExporter.slnx /m /p:Configuration=Release /p:Platform=x64
msbuild .\src\AutoCadGltfExporter\AutoCadGltfExporter.slnx /m /p:Configuration=Release /p:Platform=x64
cd .\src\web-viewer; pnpm install; pnpm typecheck; pnpm build
```

Build Draco before either plug-in; outputs are staged in `output/`. Use `pnpm dev` for the viewer at `http://localhost:5173`. `scripts\install-plugins.ps1 -Target Revit|AutoCAD|Both` builds and installs plug-ins (administrator PowerShell may be required).

## Coding Style & Naming Conventions

Use four-space indentation in C# and TypeScript; retain existing braces and nullable-safe TypeScript. C# types/public members use `PascalCase`, locals/parameters `camelCase`, and interfaces the `I...` prefix. Name React components `PascalCase`, hooks `use...`, and keep feature files near their component. Keep generated files (`output/`, `dist/`, `bin/`, `obj/`, `node_modules/`) out of commits.

## Testing Guidelines

There is no repository-level C# test project. Validate by building both solutions and running `pnpm typecheck`/`pnpm build`; exercise exports and GLB loading in the matching Autodesk host and browser. Draco’s upstream tests are disabled by the wrapper by default. Name new tests `ThingTest.cs` or `thing_test.cc`.

## Commit & Pull Request Guidelines

Use short, imperative subjects with an optional prefix matching history (`feat:`, `refactor:`, `Add`, `Enhance`), e.g. `feat: preserve DWG layer metadata`. Keep commits focused. Pull requests should explain affected surfaces, list build/manual validation, link issues, and include screenshots or a recording for viewer/UI changes. Call out required Autodesk versions or path overrides.

## Configuration & Safety

Do not commit Autodesk SDK binaries, generated plug-in output, model files, or secrets. Pass non-default `RevitInstallPath`/`AutoCadInstallPath` to MSBuild or the installer script instead of changing project defaults; keep `draco_encoder.dll` alongside plug-in assemblies when testing Draco export.

## Mandatory spec workflow

在分析需求、制定计划、修改代码或编写测试之前，必须：

1. 首先阅读 `specs/README.md`。
2. 根据 `specs/README.md` 中的索引，阅读与当前任务相关的所有 spec。
3. 如果任务涉及多个模块，必须阅读这些模块对应的全部 spec。
4. `specs/` 下的文档是实现行为和验收标准的事实来源。
5. 如果用户要求与 spec 冲突，不要自行猜测或静默忽略 spec，必须指出冲突并请求确认。
6. 实现完成后，根据相关 spec 的验收标准检查代码和测试。
