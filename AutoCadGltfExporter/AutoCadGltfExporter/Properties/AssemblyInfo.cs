using System.Reflection;
using System.Runtime.InteropServices;
using Autodesk.AutoCAD.Runtime;

[assembly: AssemblyTitle("AutoCadGltfExporter")]
[assembly: AssemblyDescription("Export AutoCAD DWG drawings (3D solids, 2D curves, text, hatches) to .glb for the web viewer.")]
[assembly: AssemblyProduct("AutoCadGltfExporter")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: ComVisible(false)]
[assembly: Guid("8b1d2c3a-4e5f-46a7-9b8c-1d2e3f4a5b6c")]

[assembly: ExtensionApplication(typeof(AutoCadGltfExporter.Application))]
[assembly: CommandClass(typeof(AutoCadGltfExporter.Commands.ExportGlbCommand))]
