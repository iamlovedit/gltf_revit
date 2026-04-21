using System;
using System.Reflection;
using Autodesk.Revit.UI;

namespace RevitGltfExporter
{
    public class Application : IExternalApplication
    {
        private const string TabName = "GLB Tools";
        private const string PanelName = "Export";

        public Result OnStartup(UIControlledApplication app)
        {
            app.CreateRibbonTab(TabName);

            var panel = app.CreateRibbonPanel(TabName, PanelName);
            var asmPath = Assembly.GetExecutingAssembly().Location;

            var data = new PushButtonData(
                "ExportGlb",
                "Export GLB",
                asmPath,
                "RevitGltfExporter.Commands.ExportGlbCommand")
            {
                ToolTip = "Export current view to glTF binary (.glb) with materials and properties.",
                LongDescription = "Optional Draco compression is applied via an external gltf-pipeline CLI."
            };
            panel.AddItem(data);
            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication app) => Result.Succeeded;
    }
}
