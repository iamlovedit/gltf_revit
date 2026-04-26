using System.Collections.Generic;
using Newtonsoft.Json;

namespace GltfExporter.Shared
{
    // Minimal glTF 2.0 POCOs — only the fields this exporter writes.
    public class GltfRoot
    {
        [JsonProperty("asset")]
        public GltfAsset Asset { get; set; } = new GltfAsset();
        [JsonProperty("scene")]
        public int Scene { get; set; } = 0;
        [JsonProperty("scenes")]
        public List<GltfScene> Scenes { get; set; } = new List<GltfScene>();
        [JsonProperty("nodes")]
        public List<GltfNode> Nodes { get; set; } = new List<GltfNode>();
        [JsonProperty("meshes")]
        public List<GltfMesh> Meshes { get; set; } = new List<GltfMesh>();
        [JsonProperty("materials")]
        public List<GltfMaterial> Materials { get; set; } = new List<GltfMaterial>();
        [JsonProperty("accessors")]
        public List<GltfAccessor> Accessors { get; set; } = new List<GltfAccessor>();
        [JsonProperty("bufferViews")]
        public List<GltfBufferView> BufferViews { get; set; } = new List<GltfBufferView>();
        [JsonProperty("buffers")]
        public List<GltfBuffer> Buffers { get; set; } = new List<GltfBuffer>();
        [JsonProperty("extensionsUsed", NullValueHandling = NullValueHandling.Ignore)]
        public List<string> ExtensionsUsed { get; set; }
        [JsonProperty("extensionsRequired", NullValueHandling = NullValueHandling.Ignore)]
        public List<string> ExtensionsRequired { get; set; }
        [JsonProperty("extras", NullValueHandling = NullValueHandling.Ignore)]
        public object Extras { get; set; }
    }

    public class GltfAsset
    {
        [JsonProperty("version")]
        public string Version { get; set; } = "2.0";
        [JsonProperty("generator")]
        public string Generator { get; set; } = "GltfExporter";
    }

    public class GltfScene
    {
        [JsonProperty("nodes")]
        public List<int> Nodes { get; set; } = new List<int>();
    }

    public class GltfNode
    {
        [JsonProperty("name", NullValueHandling = NullValueHandling.Ignore)]
        public string Name { get; set; }
        [JsonProperty("mesh", NullValueHandling = NullValueHandling.Ignore)]
        public int? Mesh { get; set; }
        [JsonProperty("children", NullValueHandling = NullValueHandling.Ignore)]
        public List<int> Children { get; set; }
        [JsonProperty("matrix", NullValueHandling = NullValueHandling.Ignore)]
        public float[] Matrix { get; set; }
        [JsonProperty("extras", NullValueHandling = NullValueHandling.Ignore)]
        public object Extras { get; set; }
    }

    public class GltfMesh
    {
        [JsonProperty("name", NullValueHandling = NullValueHandling.Ignore)]
        public string Name { get; set; }
        [JsonProperty("primitives")]
        public List<GltfPrimitive> Primitives { get; set; } = new List<GltfPrimitive>();
    }

    public class GltfPrimitive
    {
        [JsonProperty("attributes")]
        public Dictionary<string, int> Attributes { get; set; } = new Dictionary<string, int>();
        [JsonProperty("indices")]
        public int Indices { get; set; }
        [JsonProperty("material", NullValueHandling = NullValueHandling.Ignore)]
        public int? Material { get; set; }
        [JsonProperty("mode")]
        public int Mode { get; set; } = 4; // TRIANGLES
        [JsonProperty("extensions", NullValueHandling = NullValueHandling.Ignore)]
        public Dictionary<string, object> Extensions { get; set; }
    }

    public class GltfMaterial
    {
        [JsonProperty("name", NullValueHandling = NullValueHandling.Ignore)]
        public string Name { get; set; }
        [JsonProperty("pbrMetallicRoughness")]
        public GltfPbr PbrMetallicRoughness { get; set; } = new GltfPbr();
        [JsonProperty("doubleSided")]
        public bool DoubleSided { get; set; } = true;
        [JsonProperty("alphaMode", NullValueHandling = NullValueHandling.Ignore)]
        public string AlphaMode { get; set; }
    }

    public class GltfPbr
    {
        [JsonProperty("baseColorFactor")]
        public float[] BaseColorFactor { get; set; } = new[] { 0.8f, 0.8f, 0.8f, 1f };
        [JsonProperty("metallicFactor")]
        public float MetallicFactor { get; set; } = 0.0f;
        [JsonProperty("roughnessFactor")]
        public float RoughnessFactor { get; set; } = 0.8f;
    }

    public class GltfAccessor
    {
        // Nullable: KHR_draco_mesh_compression accessors omit bufferView because
        // the extension supplies the decoded data.
        [JsonProperty("bufferView", NullValueHandling = NullValueHandling.Ignore)]
        public int? BufferView { get; set; }
        [JsonProperty("byteOffset")]
        public int ByteOffset { get; set; }
        [JsonProperty("componentType")]
        public int ComponentType { get; set; }
        [JsonProperty("count")]
        public int Count { get; set; }
        [JsonProperty("type")]
        public string Type { get; set; }
        [JsonProperty("min", NullValueHandling = NullValueHandling.Ignore)]
        public float[] Min { get; set; }
        [JsonProperty("max", NullValueHandling = NullValueHandling.Ignore)]
        public float[] Max { get; set; }
    }

    public class GltfBufferView
    {
        [JsonProperty("buffer")]
        public int Buffer { get; set; }
        [JsonProperty("byteOffset")]
        public int ByteOffset { get; set; }
        [JsonProperty("byteLength")]
        public int ByteLength { get; set; }
        [JsonProperty("target", NullValueHandling = NullValueHandling.Ignore)]
        public int? Target { get; set; }
    }

    public class GltfBuffer
    {
        [JsonProperty("byteLength")]
        public int ByteLength { get; set; }
        [JsonProperty("uri", NullValueHandling = NullValueHandling.Ignore)]
        public string Uri { get; set; }
    }

    public static class GltfComponentType
    {
        public const int UnsignedInt = 5125;
        public const int Float = 5126;
    }

    public static class GltfTarget
    {
        public const int ArrayBuffer = 34962;
        public const int ElementArrayBuffer = 34963;
    }

    public static class GltfPrimitiveMode
    {
        public const int Points = 0;
        public const int Lines = 1;
        public const int LineLoop = 2;
        public const int LineStrip = 3;
        public const int Triangles = 4;
        public const int TriangleStrip = 5;
        public const int TriangleFan = 6;
    }
}
