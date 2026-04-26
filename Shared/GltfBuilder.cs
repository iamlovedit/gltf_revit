using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace GltfExporter.Shared
{
    // Assembles glTF logical data + one big binary buffer, then writes a valid .glb container.
    public class GltfBuilder
    {
        private readonly GltfRoot _root = new GltfRoot();
        private readonly MemoryStream _binary = new MemoryStream();

        public GltfRoot Root => _root;

        public int AddMaterial(GltfMaterial material)
        {
            _root.Materials.Add(material);
            return _root.Materials.Count - 1;
        }

        public int AddNode(GltfNode node)
        {
            _root.Nodes.Add(node);
            return _root.Nodes.Count - 1;
        }

        public int AddMesh(GltfMesh mesh)
        {
            _root.Meshes.Add(mesh);
            return _root.Meshes.Count - 1;
        }

        public int AddFloat3Accessor(IList<float> data, float[] min, float[] max, int target)
        {
            var bv = WriteBufferView(FloatsToBytes(data), target, 12);
            var accessor = new GltfAccessor
            {
                BufferView = bv,
                ComponentType = GltfComponentType.Float,
                Count = data.Count / 3,
                Type = "VEC3",
                Min = min,
                Max = max
            };
            _root.Accessors.Add(accessor);
            return _root.Accessors.Count - 1;
        }

        public int AddFloat2Accessor(IList<float> data, int target)
        {
            var bv = WriteBufferView(FloatsToBytes(data), target, 8);
            var accessor = new GltfAccessor
            {
                BufferView = bv,
                ComponentType = GltfComponentType.Float,
                Count = data.Count / 2,
                Type = "VEC2"
            };
            _root.Accessors.Add(accessor);
            return _root.Accessors.Count - 1;
        }

        public int AddIndexAccessor(IList<int> indices)
        {
            var bytes = new byte[indices.Count * 4];
            System.Buffer.BlockCopy(ToArray(indices), 0, bytes, 0, bytes.Length);
            var bv = WriteBufferView(bytes, GltfTarget.ElementArrayBuffer, 4);
            var accessor = new GltfAccessor
            {
                BufferView = bv,
                ComponentType = GltfComponentType.UnsignedInt,
                Count = indices.Count,
                Type = "SCALAR"
            };
            _root.Accessors.Add(accessor);
            return _root.Accessors.Count - 1;
        }

        internal int WriteBufferView(byte[] data, int target, int alignment)
        {
            // glTF requires bufferView offsets to honor component alignment.
            var pad = (alignment - (_binary.Length % alignment)) % alignment;
            for (int i = 0; i < pad; i++) _binary.WriteByte(0);

            var offset = (int)_binary.Length;
            _binary.Write(data, 0, data.Length);
            _root.BufferViews.Add(new GltfBufferView
            {
                Buffer = 0,
                ByteOffset = offset,
                ByteLength = data.Length,
                // KHR_draco_mesh_compression forbids target on the compressed bufferView;
                // callers signal that by passing target <= 0.
                Target = target > 0 ? (int?)target : null
            });
            return _root.BufferViews.Count - 1;
        }

        public GltfPrimitive AddDracoPrimitive(
            float[] positions, float[] positionsMin, float[] positionsMax,
            float[] normals, float[] uvs, int[] indices,
            int? material, int compressionLevel)
        {
            var enc = DracoNative.Encode(positions, normals, uvs, indices, compressionLevel);

            var bvIndex = WriteBufferView(enc.EncodedBytes, target: 0, alignment: 4);

            var dracoAttrs = new Dictionary<string, int>();
            dracoAttrs["POSITION"] = enc.PositionAttrId;

            var prim = new GltfPrimitive
            {
                Material = material,
                Indices = AddShadowAccessor(indices.Length, GltfComponentType.UnsignedInt, "SCALAR", null, null)
            };
            prim.Attributes["POSITION"] = AddShadowAccessor(
                positions.Length / 3, GltfComponentType.Float, "VEC3", positionsMin, positionsMax);

            if (normals != null && normals.Length > 0 && enc.NormalAttrId >= 0)
            {
                prim.Attributes["NORMAL"] = AddShadowAccessor(
                    normals.Length / 3, GltfComponentType.Float, "VEC3", null, null);
                dracoAttrs["NORMAL"] = enc.NormalAttrId;
            }
            if (uvs != null && uvs.Length > 0 && enc.UvAttrId >= 0)
            {
                prim.Attributes["TEXCOORD_0"] = AddShadowAccessor(
                    uvs.Length / 2, GltfComponentType.Float, "VEC2", null, null);
                dracoAttrs["TEXCOORD_0"] = enc.UvAttrId;
            }

            prim.Extensions = new Dictionary<string, object>
            {
                ["KHR_draco_mesh_compression"] = new Dictionary<string, object>
                {
                    ["bufferView"] = bvIndex,
                    ["attributes"] = dracoAttrs
                }
            };

            EnsureExtensionDeclared("KHR_draco_mesh_compression", required: true);
            return prim;
        }

        internal int AddShadowAccessor(int count, int componentType, string type, float[] min, float[] max)
        {
            _root.Accessors.Add(new GltfAccessor
            {
                BufferView = null,
                ComponentType = componentType,
                Count = count,
                Type = type,
                Min = min,
                Max = max
            });
            return _root.Accessors.Count - 1;
        }

        internal void EnsureExtensionDeclared(string name, bool required)
        {
            if (_root.ExtensionsUsed == null) _root.ExtensionsUsed = new List<string>();
            if (!_root.ExtensionsUsed.Contains(name)) _root.ExtensionsUsed.Add(name);
            if (required)
            {
                if (_root.ExtensionsRequired == null) _root.ExtensionsRequired = new List<string>();
                if (!_root.ExtensionsRequired.Contains(name)) _root.ExtensionsRequired.Add(name);
            }
        }

        public void WriteGlb(string path)
        {
            // Pad binary chunk to 4 bytes.
            while (_binary.Length % 4 != 0) _binary.WriteByte(0);

            _root.Buffers.Clear();
            _root.Buffers.Add(new GltfBuffer { ByteLength = (int)_binary.Length });
            if (_root.Scenes.Count == 0)
            {
                var rootScene = new GltfScene();
                for (int i = 0; i < _root.Nodes.Count; i++) rootScene.Nodes.Add(i);
                _root.Scenes.Add(rootScene);
            }

            var json = JsonConvert.SerializeObject(_root, new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore
            });
            var jsonBytes = System.Text.Encoding.UTF8.GetBytes(json);
            // Pad JSON to 4 bytes with spaces.
            var jsonPad = (4 - jsonBytes.Length % 4) % 4;
            if (jsonPad > 0)
            {
                var padded = new byte[jsonBytes.Length + jsonPad];
                System.Buffer.BlockCopy(jsonBytes, 0, padded, 0, jsonBytes.Length);
                for (int i = 0; i < jsonPad; i++) padded[jsonBytes.Length + i] = 0x20;
                jsonBytes = padded;
            }

            var binBytes = _binary.ToArray();
            var totalLength = 12 + 8 + jsonBytes.Length + 8 + binBytes.Length;

            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write(0x46546C67u);        // "glTF"
                bw.Write(2u);                  // version
                bw.Write((uint)totalLength);

                bw.Write((uint)jsonBytes.Length);
                bw.Write(0x4E4F534Au);         // "JSON"
                bw.Write(jsonBytes);

                bw.Write((uint)binBytes.Length);
                bw.Write(0x004E4942u);         // "BIN\0"
                bw.Write(binBytes);
            }
        }

        private static byte[] FloatsToBytes(IList<float> src)
        {
            var arr = src as float[] ?? ToArray(src);
            var bytes = new byte[arr.Length * 4];
            System.Buffer.BlockCopy(arr, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        private static float[] ToArray(IList<float> src)
        {
            var a = new float[src.Count];
            for (int i = 0; i < src.Count; i++) a[i] = src[i];
            return a;
        }

        private static int[] ToArray(IList<int> src)
        {
            var a = new int[src.Count];
            for (int i = 0; i < src.Count; i++) a[i] = src[i];
            return a;
        }
    }
}
