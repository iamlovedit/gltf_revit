using System;
using System.Collections.Generic;
using GltfExporter.Shared;

namespace RevitGltfExporter.Core
{
    public sealed class RevitExportWriter
    {
        public void Write(
            IList<RevitElementData> elements,
            IList<RevitMaterialData> materials,
            ExportOptions options,
            string sourceVersion,
            string outputPath)
        {
            if (elements == null) throw new ArgumentNullException(nameof(elements));
            if (materials == null) throw new ArgumentNullException(nameof(materials));
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrWhiteSpace(outputPath)) throw new ArgumentException("Output path is required.", nameof(outputPath));

            var builder = new GltfBuilder();
            builder.Root.Extras = new Dictionary<string, object>
            {
                { "schemaVersion", "1.0.0" },
                { "source", "Revit" },
                { "unit", "meter" },
                { "sourceVersion", sourceVersion ?? string.Empty }
            };

            var materialIndices = new int[materials.Count];
            for (var i = 0; i < materials.Count; i++)
            {
                var material = materials[i] ?? new RevitMaterialData();
                var gltf = new GltfMaterial
                {
                    Name = material.Name,
                    AlphaMode = material.Alpha < 0.999f ? "BLEND" : null
                };
                gltf.PbrMetallicRoughness.BaseColorFactor = new[] { material.Red, material.Green, material.Blue, material.Alpha };
                gltf.PbrMetallicRoughness.MetallicFactor = Clamp01(material.Metallic);
                gltf.PbrMetallicRoughness.RoughnessFactor = Clamp01(material.Roughness);
                materialIndices[i] = builder.AddMaterial(gltf);
            }

            foreach (var element in elements)
            {
                if (element == null || element.Primitives.Count == 0) continue;
                var mesh = new GltfMesh { Name = element.Name };
                foreach (var primitive in element.Primitives)
                {
                    if (primitive == null || primitive.Indices.Count == 0 || primitive.Positions.Count == 0) continue;
                    var minMax = ComputeMinMax(primitive.Positions);
                    var material = primitive.MaterialIndex >= 0 && primitive.MaterialIndex < materialIndices.Length
                        ? (int?)materialIndices[primitive.MaterialIndex]
                        : null;
                    GltfPrimitive gltfPrimitive;
                    if (options.EnableDraco)
                    {
                        gltfPrimitive = builder.AddDracoPrimitive(
                            primitive.Positions.ToArray(), minMax.min, minMax.max,
                            primitive.Normals.Count > 0 ? primitive.Normals.ToArray() : null,
                            primitive.Uvs.Count > 0 ? primitive.Uvs.ToArray() : null,
                            primitive.Indices.ToArray(), material, options.DracoCompressionLevel);
                    }
                    else
                    {
                        var pos = builder.AddFloat3Accessor(primitive.Positions, minMax.min, minMax.max, GltfTarget.ArrayBuffer);
                        var normal = builder.AddFloat3Accessor(primitive.Normals, null, null, GltfTarget.ArrayBuffer);
                        var index = builder.AddIndexAccessor(primitive.Indices);
                        gltfPrimitive = new GltfPrimitive { Material = material, Indices = index };
                        gltfPrimitive.Attributes["POSITION"] = pos;
                        gltfPrimitive.Attributes["NORMAL"] = normal;
                        if (primitive.Uvs.Count > 0)
                        {
                            gltfPrimitive.Attributes["TEXCOORD_0"] = builder.AddFloat2Accessor(primitive.Uvs, GltfTarget.ArrayBuffer);
                        }
                    }
                    mesh.Primitives.Add(gltfPrimitive);
                }
                if (mesh.Primitives.Count == 0) continue;
                var meshIndex = builder.AddMesh(mesh);
                var extras = new Dictionary<string, object> { { "elementId", element.ElementId } };
                if (!string.IsNullOrEmpty(element.Category)) extras["category"] = element.Category;
                if (!string.IsNullOrEmpty(element.Family)) extras["family"] = element.Family;
                if (!string.IsNullOrEmpty(element.Type)) extras["type"] = element.Type;
                if (options.IncludeProperties && element.Parameters != null) extras["parameters"] = element.Parameters;
                builder.AddNode(new GltfNode { Name = element.Name, Mesh = meshIndex, Extras = extras });
            }

            builder.WriteGlbAtomically(outputPath);
        }

        private static float Clamp01(float value)
        {
            return value < 0f ? 0f : (value > 1f ? 1f : value);
        }

        private static (float[] min, float[] max) ComputeMinMax(IList<float> positions)
        {
            var min = new[] { float.MaxValue, float.MaxValue, float.MaxValue };
            var max = new[] { float.MinValue, float.MinValue, float.MinValue };
            for (var i = 0; i + 2 < positions.Count; i += 3)
            {
                for (var k = 0; k < 3; k++)
                {
                    var value = positions[i + k];
                    if (value < min[k]) min[k] = value;
                    if (value > max[k]) max[k] = value;
                }
            }
            return (min, max);
        }
    }
}
