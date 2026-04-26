using System;
using System.Windows.Interop;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AutoCadGltfExporter.Export;
using AutoCadGltfExporter.UI;
using GltfExporter.Shared;
using Microsoft.Win32;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AutoCadGltfExporter.Commands
{
    public class ExportGlbCommand
    {
        [CommandMethod("EXPORTGLB", CommandFlags.Modal)]
        public void ExportGlb()
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                return;
            }

            var ed = doc.Editor;
            var db = doc.Database;

            var options = new ExportOptions
            {
                EnableDraco = false,
                DracoCompressionLevel = 7,
                IncludeProperties = true
            };

            var optionsWindow = new ExportOptionsWindow(options);
            new WindowInteropHelper(optionsWindow).Owner = AcadApp.MainWindow.Handle;
            if (optionsWindow.ShowDialog() != true)
            {
                ed.WriteMessage("\nExport cancelled.\n");
                return;
            }

            var defaultName = System.IO.Path.GetFileNameWithoutExtension(doc.Name ?? "model") + ".glb";
            var save = new SaveFileDialog
            {
                Filter = "glTF Binary (*.glb)|*.glb",
                FileName = defaultName
            };
            if (save.ShowDialog() != true)
            {
                ed.WriteMessage("\nExport cancelled.\n");
                return;
            }

            var outputPath = save.FileName;

            // FACETRES drives the chord tolerance used by entity.WorldDraw tessellation.
            // Bump it for the export, restore in finally.
            var prevFacetRes = db.Facetres;
            try
            {
                db.Facetres = Math.Max(prevFacetRes, 2.0);

                using (doc.LockDocument())
                {
                    var context = new DwgExportContext(db, options, ed);
                    context.Run();
                    context.WriteGlb(outputPath);
                }

                ed.WriteMessage("\nExported to {0}\n", outputPath);
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\nExport failed: {0}\n", ex);
            }
            finally
            {
                db.Facetres = prevFacetRes;
            }
        }
    }
}
