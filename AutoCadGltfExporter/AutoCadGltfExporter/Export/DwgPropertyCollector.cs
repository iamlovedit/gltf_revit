using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;

namespace AutoCadGltfExporter.Export
{
    // Collects per-entity metadata into the dictionary that ends up in
    // GltfNode.Extras. Kept intentionally narrow: handle, layer, color,
    // linetype, lineweight, plus XData and ExtensionDictionary if present.
    internal static class DwgPropertyCollector
    {
        public static Dictionary<string, object> Collect(Entity entity, Transaction tr)
        {
            var dict = new Dictionary<string, object>();
            if (entity == null) return dict;

            dict["handle"] = entity.Handle.Value.ToString("X");
            dict["layer"] = entity.Layer ?? "0";
            dict["linetype"] = entity.Linetype;
            dict["lineweight"] = (int)entity.LineWeight;
            dict["color"] = entity.Color?.ColorNameForDisplay ?? entity.Color?.ToString();
            dict["colorIndex"] = (int)entity.ColorIndex;

            var xdata = entity.XData;
            if (xdata != null)
            {
                var xlist = new List<object>();
                foreach (var v in xdata)
                {
                    xlist.Add(new Dictionary<string, object>
                    {
                        ["code"] = (int)v.TypeCode,
                        ["value"] = v.Value?.ToString()
                    });
                }
                if (xlist.Count > 0) dict["xdata"] = xlist;
            }

            var extDictId = entity.ExtensionDictionary;
            if (extDictId != ObjectId.Null && tr != null)
            {
                try
                {
                    var ext = tr.GetObject(extDictId, OpenMode.ForRead) as DBDictionary;
                    if (ext != null)
                    {
                        var kv = new Dictionary<string, object>();
                        foreach (DBDictionaryEntry entry in ext)
                        {
                            kv[entry.Key] = entry.Value.Handle.Value.ToString("X");
                        }
                        if (kv.Count > 0) dict["extDict"] = kv;
                    }
                }
                catch
                {
                    // Some DBDictionary entries are restricted; ignore.
                }
            }
            return dict;
        }
    }
}
