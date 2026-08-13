using System.Collections.Generic;

namespace RevitGltfExporter.Core
{
    public sealed class RevitMaterialData
    {
        public string Key { get; set; }
        public string Name { get; set; }
        public float Red { get; set; }
        public float Green { get; set; }
        public float Blue { get; set; }
        public float Alpha { get; set; } = 1f;
        public float Metallic { get; set; }
        public float Roughness { get; set; } = 0.8f;
    }

    public sealed class RevitPrimitiveData
    {
        public int MaterialIndex { get; set; }
        public List<float> Positions { get; } = new List<float>();
        public List<float> Normals { get; } = new List<float>();
        public List<float> Uvs { get; } = new List<float>();
        public List<int> Indices { get; } = new List<int>();
    }

    public sealed class RevitElementData
    {
        public string ElementId { get; set; }
        public string Name { get; set; }
        public string Category { get; set; }
        public string Family { get; set; }
        public string Type { get; set; }
        public Dictionary<string, object> Parameters { get; set; }
        public List<RevitPrimitiveData> Primitives { get; } = new List<RevitPrimitiveData>();
    }
}
