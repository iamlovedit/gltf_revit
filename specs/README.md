# 功能规格索引

这些 spec 是本项目跨插件、共享导出器和 Web 查看器的行为与验收标准。实现或测试前，先阅读本表中与任务相关的全部文档。

| 功能 | Spec |
| --- | --- |
| 总体业务与模块边界 | [overview.md](overview.md) |
| Revit 2019 GLB 导出 | [revit-export.md](revit-export.md) |
| AutoCAD 2020–2024 GLB 导出 | [autocad-export.md](autocad-export.md) |
| GLB 数据契约与坐标单位 | [glb-contract.md](glb-contract.md) |
| Draco 原生压缩 | [draco.md](draco.md) |
| Web 查看器与异步加载 | [frontend.md](frontend.md) |

## 阅读与验收规则

- 导出端和 Viewer 改动必须同时遵守 `glb-contract.md`。
- 涉及多个模块时，必须阅读每个对应 spec；文档中的“必须”是验收条件。
- 性能目标用于回归验证；若宿主软件或浏览器无法自动化测试，必须记录手工验证环境与结果。
