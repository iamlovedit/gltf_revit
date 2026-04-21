using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace RevitGltfExporter.Export
{
    internal class MaterialCollector
    {
        private readonly Document _doc;
        private readonly GltfBuilder _builder;
        private readonly Dictionary<ElementId, int> _materialIndex = new Dictionary<ElementId, int>();
        private int _defaultIndex = -1;

        public MaterialCollector(Document doc, GltfBuilder builder)
        {
            _doc = doc;
            _builder = builder;
        }

        public int GetOrCreate(ElementId materialId, Color fallbackColor, int fallbackTransparency)
        {
            if (materialId == null || materialId == ElementId.InvalidElementId)
            {
                return GetOrCreateDefault(fallbackColor, fallbackTransparency);
            }

            if (_materialIndex.TryGetValue(materialId, out var cached)) return cached;

            var mat = _doc.GetElement(materialId) as Material;
            var gltfMat = mat != null ? ToGltfMaterial(mat) : BuildFallback(fallbackColor, fallbackTransparency);
            var idx = _builder.AddMaterial(gltfMat);
            _materialIndex[materialId] = idx;
            return idx;
        }

        private int GetOrCreateDefault(Color color, int transparency)
        {
            if (_defaultIndex >= 0) return _defaultIndex;
            _defaultIndex = _builder.AddMaterial(BuildFallback(color, transparency));
            return _defaultIndex;
        }

        private static GltfMaterial ToGltfMaterial(Material m)
        {
            var color = m.Color;
            var alpha = 1f - (m.Transparency / 100f);
            var smoothness = m.Smoothness; // 0..100
            var metallic = m.Shininess / 128f;

            var gm = new GltfMaterial
            {
                Name = m.Name,
                PbrMetallicRoughness = new GltfPbr
                {
                    BaseColorFactor = new[]
                    {
                        Byte01(color?.Red ?? 200),
                        Byte01(color?.Green ?? 200),
                        Byte01(color?.Blue ?? 200),
                        alpha
                    },
                    MetallicFactor = Clamp01(metallic),
                    RoughnessFactor = Clamp01(1f - smoothness / 100f)
                }
            };
            if (alpha < 0.999f) gm.AlphaMode = "BLEND";
            return gm;
        }

        private static GltfMaterial BuildFallback(Color color, int transparency)
        {
            var alpha = 1f - transparency / 100f;
            var gm = new GltfMaterial
            {
                Name = "default",
                PbrMetallicRoughness = new GltfPbr
                {
                    BaseColorFactor = new[]
                    {
                        color != null && color.IsValid ? Byte01(color.Red) : 0.8f,
                        color != null && color.IsValid ? Byte01(color.Green) : 0.8f,
                        color != null && color.IsValid ? Byte01(color.Blue) : 0.8f,
                        alpha
                    }
                }
            };
            if (alpha < 0.999f) gm.AlphaMode = "BLEND";
            return gm;
        }

        private static float Byte01(byte v) => v / 255f;
        private static float Byte01(int v) => (v & 0xFF) / 255f;
        private static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;
    }
}
