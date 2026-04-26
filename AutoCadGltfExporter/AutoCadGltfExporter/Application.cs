using Autodesk.AutoCAD.Runtime;

namespace AutoCadGltfExporter
{
    public class Application : IExtensionApplication
    {
        public void Initialize()
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            doc?.Editor.WriteMessage("\nAutoCadGltfExporter loaded. Use the EXPORTGLB command to export the current drawing.\n");
        }

        public void Terminate() { }
    }
}
