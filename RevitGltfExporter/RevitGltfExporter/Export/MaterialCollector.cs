using System.Collections.Generic;
using Autodesk.Revit.DB;
using GltfExporter.Shared;

namespace RevitGltfExporter.Export
{
    // Thin adapter: Revit's Material / ElementId / Color -> Shared MaterialBuilder.
    internal class MaterialCollector
    {
        private readonly Document _doc;
        private readonly MaterialBuilder _materials;
        private readonly HashSet<int> _seenIds = new HashSet<int>();

        public MaterialCollector(Document doc, GltfBuilder builder)
        {
            _doc = doc;
            _materials = new MaterialBuilder(builder);
        }

        public int GetOrCreate(ElementId materialId, Color fallbackColor, int fallbackTransparency)
        {
            if (materialId == null || materialId == ElementId.InvalidElementId)
            {
                var alpha = 1f - fallbackTransparency / 100f;
                return _materials.GetOrCreateDefault(
                    fallbackColor != null && fallbackColor.IsValid ? Byte01(fallbackColor.Red) : 0.8f,
                    fallbackColor != null && fallbackColor.IsValid ? Byte01(fallbackColor.Green) : 0.8f,
                    fallbackColor != null && fallbackColor.IsValid ? Byte01(fallbackColor.Blue) : 0.8f,
                    alpha);
            }

            var key = "rvt:" + materialId.IntegerValue;

            var mat = _doc.GetElement(materialId) as Material;
            if (mat != null)
            {
                var color = mat.Color;
                var alpha = 1f - (mat.Transparency / 100f);
                var metallic = mat.Shininess / 128f;
                var roughness = 1f - mat.Smoothness / 100f;
                return _materials.GetOrCreate(
                    key, mat.Name,
                    Byte01(color?.Red ?? 200),
                    Byte01(color?.Green ?? 200),
                    Byte01(color?.Blue ?? 200),
                    alpha, metallic, roughness);
            }

            // Unknown material id -> fallback color, but cached under the id so we don't thrash.
            var fallA = 1f - fallbackTransparency / 100f;
            return _materials.GetOrCreate(
                key, "material_" + materialId.IntegerValue,
                fallbackColor != null && fallbackColor.IsValid ? Byte01(fallbackColor.Red) : 0.8f,
                fallbackColor != null && fallbackColor.IsValid ? Byte01(fallbackColor.Green) : 0.8f,
                fallbackColor != null && fallbackColor.IsValid ? Byte01(fallbackColor.Blue) : 0.8f,
                fallA, 0f, 0.8f);
        }

        private static float Byte01(byte v) => v / 255f;
        private static float Byte01(int v) => (v & 0xFF) / 255f;
    }
}
