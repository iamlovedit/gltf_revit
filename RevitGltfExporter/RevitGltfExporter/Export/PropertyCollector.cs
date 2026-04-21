using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace RevitGltfExporter.Export
{
    internal static class PropertyCollector
    {
        // Converts an element's parameters to a dictionary suitable for glTF node `extras`.
        public static Dictionary<string, object> Collect(Element element)
        {
            var dict = new Dictionary<string, object>();
            if (element == null) return dict;

            foreach (Parameter p in element.Parameters)
            {
                if (p == null || p.Definition == null) continue;
                var name = p.Definition.Name;
                if (string.IsNullOrEmpty(name) || dict.ContainsKey(name)) continue;

                object value = null;
                switch (p.StorageType)
                {
                    case StorageType.String:
                        value = p.AsString();
                        break;
                    case StorageType.Integer:
                        value = p.AsInteger();
                        break;
                    case StorageType.Double:
                        // Internal units are feet for length, cubic feet for volume, etc.
                        // Convert to SI via ParameterType heuristic.
                        value = ConvertToSi(p);
                        break;
                    case StorageType.ElementId:
                        var id = p.AsElementId();
                        value = id?.IntegerValue;
                        break;
                }
                if (value != null) dict[name] = value;
            }
            return dict;
        }

        private static double ConvertToSi(Parameter p)
        {
            var raw = p.AsDouble();
#if REVIT2021_OR_LATER
            try
            {
                var unitTypeId = p.GetUnitTypeId();
                return UnitUtils.ConvertFromInternalUnits(raw, unitTypeId);
            }
            catch { return raw; }
#else
            // Revit 2019 API: DisplayUnitType on the parameter.
            try
            {
                var dut = p.DisplayUnitType;
                return UnitUtils.ConvertFromInternalUnits(raw, dut);
            }
            catch
            {
                return raw;
            }
#endif
        }
    }
}
