using Autodesk.Revit.DB;

namespace RevitGltfExporter.Adapters
{
    internal static class RevitApiVersion
    {
        public static string Current
        {
            get
            {
#if REVIT2027
                return "2027";
#elif REVIT2026
                return "2026";
#elif REVIT2025
                return "2025";
#elif REVIT2024
                return "2024";
#elif REVIT2023
                return "2023";
#elif REVIT2022
                return "2022";
#elif REVIT2021
                return "2021";
#elif REVIT2020
                return "2020";
#elif REVIT2021_OR_LATER
                return "2021";
#else
                return "2019";
#endif
            }
        }

        public static string ElementIdToString(ElementId id)
        {
            if (id == null) return string.Empty;
#if REVIT2024_OR_GREATER
            return id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
#else
            return id.IntegerValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
#endif
        }
    }
}
