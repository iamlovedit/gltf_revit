using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace RevitGltfExporter.Adapters
{
    internal static class RevitPropertyCollector
    {
        public static Dictionary<string, object> Collect(Element element)
        {
            var values = new Dictionary<string, object>();
            if (element == null) return values;
            foreach (Parameter parameter in element.Parameters)
            {
                if (parameter?.Definition == null) continue;
                var name = parameter.Definition.Name;
                if (string.IsNullOrEmpty(name) || values.ContainsKey(name)) continue;
                object value = null;
                switch (parameter.StorageType)
                {
                    case StorageType.String: value = parameter.AsString(); break;
                    case StorageType.Integer: value = parameter.AsInteger(); break;
                    case StorageType.Double: value = ConvertDouble(parameter); break;
                    case StorageType.ElementId:
                        value = RevitApiVersion.ElementIdToString(parameter.AsElementId());
                        break;
                }
                if (value != null) values[name] = value;
            }
            return values;
        }

        private static double ConvertDouble(Parameter parameter)
        {
            var raw = parameter.AsDouble();
            try
            {
#if REVIT2021_OR_LATER
                return UnitUtils.ConvertFromInternalUnits(raw, parameter.GetUnitTypeId());
#else
                return UnitUtils.ConvertFromInternalUnits(raw, parameter.DisplayUnitType);
#endif
            }
            catch
            {
                return raw;
            }
        }
    }
}
