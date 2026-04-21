using System.Collections.Generic;
using Autodesk.Revit.DB;
using RevitGltfExporter.UI;

namespace RevitGltfExporter.Export
{
    // Walks the current view's render data via CustomExporter. Produces one glTF node per Element,
    // with a Mesh whose primitives are grouped by material.
    internal class GlbExportContext : IExportContext
    {
        private readonly Document _doc;
        private readonly ExportOptions _options;
        private readonly GltfBuilder _builder = new GltfBuilder();
        private readonly MaterialCollector _materials;

        private readonly Stack<Transform> _transformStack = new Stack<Transform>();
        private ElementState _current;
        private int _currentMaterialIndex = -1;

        public GlbExportContext(Document doc, ExportOptions options)
        {
            _doc = doc;
            _options = options;
            _materials = new MaterialCollector(doc, _builder);
            _transformStack.Push(Transform.Identity);
            _builder.Root.Extras = new Dictionary<string, object>
            {
                { "schemaVersion", "1.0.0" },
                { "source", "Revit" },
                { "unit", "meter" }
            };
        }

        public bool Start() => true;
        public void Finish() { }
        public bool IsCanceled() => false;

        public RenderNodeAction OnViewBegin(ViewNode node)
        {
            // Do not clip to section box.
            return RenderNodeAction.Proceed;
        }
        public void OnViewEnd(ElementId elementId) { }

        public RenderNodeAction OnElementBegin(ElementId elementId)
        {
            var element = _doc.GetElement(elementId);
            _current = new ElementState
            {
                Element = element,
                ElementId = elementId,
                Primitives = new Dictionary<int, PrimitiveBucket>()
            };
            return RenderNodeAction.Proceed;
        }

        public void OnElementEnd(ElementId elementId)
        {
            if (_current == null || _current.Primitives.Count == 0)
            {
                _current = null;
                return;
            }

            var mesh = new GltfMesh { Name = SafeName(_current.Element) };
            foreach (var kv in _current.Primitives)
            {
                var bucket = kv.Value;
                if (bucket.Indices.Count == 0) continue;

                var minMax = ComputeMinMax(bucket.Positions);

                GltfPrimitive prim;
                if (_options.EnableDraco)
                {
                    prim = _builder.AddDracoPrimitive(
                        bucket.Positions.ToArray(), minMax.min, minMax.max,
                        bucket.Normals.Count > 0 ? bucket.Normals.ToArray() : null,
                        bucket.Uvs.Count > 0 ? bucket.Uvs.ToArray() : null,
                        bucket.Indices.ToArray(),
                        kv.Key,
                        _options.DracoCompressionLevel);
                }
                else
                {
                    var posAcc = _builder.AddFloat3Accessor(bucket.Positions, minMax.min, minMax.max, GltfTarget.ArrayBuffer);
                    var nrmAcc = _builder.AddFloat3Accessor(bucket.Normals, null, null, GltfTarget.ArrayBuffer);
                    var idxAcc = _builder.AddIndexAccessor(bucket.Indices);

                    prim = new GltfPrimitive
                    {
                        Material = kv.Key,
                        Indices = idxAcc
                    };
                    prim.Attributes["POSITION"] = posAcc;
                    prim.Attributes["NORMAL"] = nrmAcc;
                    if (bucket.Uvs.Count > 0)
                    {
                        prim.Attributes["TEXCOORD_0"] = _builder.AddFloat2Accessor(bucket.Uvs, GltfTarget.ArrayBuffer);
                    }
                }
                mesh.Primitives.Add(prim);
            }

            if (mesh.Primitives.Count == 0)
            {
                _current = null;
                return;
            }

            var meshIdx = _builder.AddMesh(mesh);
            var node = new GltfNode
            {
                Name = SafeName(_current.Element) + "_" + _current.ElementId.IntegerValue,
                Mesh = meshIdx,
                Extras = BuildNodeExtras(_current.Element, _current.ElementId)
            };
            _builder.AddNode(node);
            _current = null;
        }

        public RenderNodeAction OnInstanceBegin(InstanceNode node)
        {
            var parent = _transformStack.Peek();
            _transformStack.Push(parent.Multiply(node.GetTransform()));
            return RenderNodeAction.Proceed;
        }

        public void OnInstanceEnd(InstanceNode node)
        {
            if (_transformStack.Count > 1) _transformStack.Pop();
        }

        public RenderNodeAction OnLinkBegin(LinkNode node)
        {
            var parent = _transformStack.Peek();
            _transformStack.Push(parent.Multiply(node.GetTransform()));
            return RenderNodeAction.Proceed;
        }

        public void OnLinkEnd(LinkNode node)
        {
            if (_transformStack.Count > 1) _transformStack.Pop();
        }

        public RenderNodeAction OnFaceBegin(FaceNode node) => RenderNodeAction.Proceed;
        public void OnFaceEnd(FaceNode node) { }

        public void OnMaterial(MaterialNode node)
        {
            _currentMaterialIndex = _materials.GetOrCreate(node.MaterialId, node.Color, (int)(node.Transparency * 100));
        }

        public void OnRPC(RPCNode node) { }
        public void OnLight(LightNode node) { }

        public void OnPolymesh(PolymeshTopology polymesh)
        {
            if (_current == null || _currentMaterialIndex < 0) return;

            PrimitiveBucket bucket;
            if (!_current.Primitives.TryGetValue(_currentMaterialIndex, out bucket))
            {
                bucket = new PrimitiveBucket();
                _current.Primitives[_currentMaterialIndex] = bucket;
            }

            var xf = _transformStack.Peek();
            var baseIndex = bucket.Positions.Count / 3;

            var pts = polymesh.GetPoints();
            var normals = polymesh.GetNormals();
            var uvs = polymesh.GetUVs();
            var hasUv = uvs != null && uvs.Count == pts.Count;

            for (int i = 0; i < pts.Count; i++)
            {
                var p = xf.OfPoint(pts[i]);
                // Revit: internal feet, Z-up. Convert to meters and Y-up.
                const double ft2m = 0.3048;
                bucket.Positions.Add((float)(p.X * ft2m));
                bucket.Positions.Add((float)(p.Z * ft2m));
                bucket.Positions.Add((float)(-p.Y * ft2m));

                XYZ n = XYZ.BasisZ;
                if (normals != null && normals.Count > 0)
                {
                    // Normal distribution can be PerFace/PerVertex; fall back to first normal.
                    var ni = normals.Count == pts.Count ? i : 0;
                    n = xf.OfVector(normals[ni]).Normalize();
                }
                bucket.Normals.Add((float)n.X);
                bucket.Normals.Add((float)n.Z);
                bucket.Normals.Add((float)(-n.Y));

                if (hasUv)
                {
                    bucket.Uvs.Add((float)uvs[i].U);
                    bucket.Uvs.Add((float)uvs[i].V);
                }
            }

            foreach (var facet in polymesh.GetFacets())
            {
                bucket.Indices.Add(baseIndex + facet.V1);
                bucket.Indices.Add(baseIndex + facet.V2);
                bucket.Indices.Add(baseIndex + facet.V3);
            }
        }

        public void WriteGlb(string path) => _builder.WriteGlb(path);

        private Dictionary<string, object> BuildNodeExtras(Element element, ElementId id)
        {
            var extras = new Dictionary<string, object>
            {
                { "elementId", id.IntegerValue }
            };
            if (element?.Category != null) extras["category"] = element.Category.Name;
            if (element is FamilyInstance fi)
            {
                extras["family"] = fi.Symbol?.FamilyName;
                extras["type"] = fi.Symbol?.Name;
            }
            else if (element != null)
            {
                extras["type"] = element.Name;
            }
            if (_options.IncludeProperties)
            {
                extras["parameters"] = PropertyCollector.Collect(element);
            }
            return extras;
        }

        private static string SafeName(Element element)
        {
            if (element == null) return "Unknown";
            var cat = element.Category?.Name ?? "Element";
            var name = element.Name ?? cat;
            return cat + "_" + name;
        }

        private static (float[] min, float[] max) ComputeMinMax(List<float> positions)
        {
            var min = new[] { float.MaxValue, float.MaxValue, float.MaxValue };
            var max = new[] { float.MinValue, float.MinValue, float.MinValue };
            for (int i = 0; i < positions.Count; i += 3)
            {
                for (int k = 0; k < 3; k++)
                {
                    var v = positions[i + k];
                    if (v < min[k]) min[k] = v;
                    if (v > max[k]) max[k] = v;
                }
            }
            return (min, max);
        }

        private class ElementState
        {
            public Element Element;
            public ElementId ElementId;
            public Dictionary<int, PrimitiveBucket> Primitives;
        }

        private class PrimitiveBucket
        {
            public readonly List<float> Positions = new List<float>();
            public readonly List<float> Normals = new List<float>();
            public readonly List<float> Uvs = new List<float>();
            public readonly List<int> Indices = new List<int>();
        }
    }
}
