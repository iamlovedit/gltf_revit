using System;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.Win32;
using GltfExporter.Shared;
using RevitGltfExporter.Export;
using RevitGltfExporter.UI;

namespace RevitGltfExporter.Commands
{
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class ExportGlbCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var uiDoc = data.Application.ActiveUIDocument;
            if (uiDoc?.Document == null)
            {
                message = "No active document.";
                return Result.Failed;
            }

            var doc = uiDoc.Document;
            var view = doc.ActiveView as View3D;
            if (view == null)
            {
                TaskDialog.Show("Export GLB", "Please switch to a 3D view before exporting.");
                return Result.Cancelled;
            }

            var options = new ExportOptions
            {
                EnableDraco = false,
                DracoCompressionLevel = 7,
                IncludeProperties = true
            };

            var optionsWindow = new ExportOptionsWindow(options);
            new WindowInteropHelper(optionsWindow).Owner = data.Application.MainWindowHandle;
            if (optionsWindow.ShowDialog() != true)
            {
                return Result.Cancelled;
            }

            var save = new SaveFileDialog
            {
                Filter = "glTF Binary (*.glb)|*.glb",
                FileName = (doc.Title ?? "model") + ".glb"
            };
            if (save.ShowDialog() != true)
            {
                return Result.Cancelled;
            }

            var outputPath = save.FileName;

            try
            {
                var context = new GlbExportContext(doc, options);
                var exporter = new CustomExporter(doc, context)
                {
                    IncludeGeometricObjects = true,
                    ShouldStopOnError = false
                };
                exporter.Export(view);
                context.WriteGlb(outputPath);

                TaskDialog.Show("Export GLB", "Exported to " + outputPath);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Export GLB", "Failed: " + ex);
                return Result.Failed;
            }
        }
    }
}
