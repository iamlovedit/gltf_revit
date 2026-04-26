using System.Collections.Generic;

namespace GltfExporter.Shared
{
    // Host-agnostic material dedup + PBR assembly. Revit/AutoCAD supply
    // simple scalar inputs through GetOrCreate; no CAD types leak in here.
    public class MaterialBuilder
    {
        private readonly GltfBuilder _builder;
        private readonly Dictionary<string, int> _byKey = new Dictionary<string, int>();
        private int _defaultIndex = -1;

        public MaterialBuilder(GltfBuilder builder)
        {
            _builder = builder;
        }

        // Returns the glTF material index, creating one lazily. Dedup by caller-supplied key
        // (e.g. Revit ElementId, AutoCAD layer name, ACI color int).
        public int GetOrCreate(
            string key, string name,
            float r, float g, float b, float alpha,
            float metallic, float roughness)
        {
            if (string.IsNullOrEmpty(key)) return GetOrCreateDefault(r, g, b, alpha);
            if (_byKey.TryGetValue(key, out var cached)) return cached;

            var mat = Build(name, r, g, b, alpha, metallic, roughness);
            var idx = _builder.AddMaterial(mat);
            _byKey[key] = idx;
            return idx;
        }

        public int GetOrCreateDefault(float r = 0.8f, float g = 0.8f, float b = 0.8f, float alpha = 1f)
        {
            if (_defaultIndex >= 0) return _defaultIndex;
            _defaultIndex = _builder.AddMaterial(Build("default", r, g, b, alpha, 0f, 0.8f));
            return _defaultIndex;
        }

        private static GltfMaterial Build(string name, float r, float g, float b, float alpha, float metallic, float roughness)
        {
            var gm = new GltfMaterial
            {
                Name = name,
                PbrMetallicRoughness = new GltfPbr
                {
                    BaseColorFactor = new[] { Clamp01(r), Clamp01(g), Clamp01(b), Clamp01(alpha) },
                    MetallicFactor = Clamp01(metallic),
                    RoughnessFactor = Clamp01(roughness)
                }
            };
            if (alpha < 0.999f) gm.AlphaMode = "BLEND";
            return gm;
        }

        private static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;
    }
}
