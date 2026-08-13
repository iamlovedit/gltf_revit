using System.Collections.Generic;
using Autodesk.Revit.DB;
using GltfExporter.Shared;
using RevitGltfExporter.Core;

namespace RevitGltfExporter.Adapters
{
    internal sealed class RevitExportContext : IExportContext
    {
        private readonly Document _document;
        private readonly ExportOptions _options;
        private readonly List<RevitElementData> _elements = new List<RevitElementData>();
        private readonly List<RevitMaterialData> _materials = new List<RevitMaterialData>();
        private readonly Dictionary<string, int> _materialIndices = new Dictionary<string, int>();
        private readonly Stack<Transform> _transforms = new Stack<Transform>();
        private RevitElementData _current;
        private int _currentMaterial = -1;

        public RevitExportContext(Document document, ExportOptions options)
        {
            _document = document;
            _options = options;
            _transforms.Push(Transform.Identity);
        }

        public IList<RevitElementData> Elements => _elements;
        public IList<RevitMaterialData> Materials => _materials;
        public bool Start() => true;
        public void Finish() { }
        public bool IsCanceled() => false;
        public RenderNodeAction OnViewBegin(ViewNode node) => RenderNodeAction.Proceed;
        public void OnViewEnd(ElementId elementId) { }

        public RenderNodeAction OnElementBegin(ElementId elementId)
        {
            var element = _document.GetElement(elementId);
            _current = new RevitElementData
            {
                ElementId = RevitApiVersion.ElementIdToString(elementId),
                Name = SafeName(element),
                Category = element?.Category?.Name,
                Parameters = _options.IncludeProperties ? RevitPropertyCollector.Collect(element) : null
            };
            var familyInstance = element as FamilyInstance;
            if (familyInstance != null)
            {
                _current.Family = familyInstance.Symbol?.FamilyName;
                _current.Type = familyInstance.Symbol?.Name;
            }
            else if (element != null)
            {
                _current.Type = element.Name;
            }
            return RenderNodeAction.Proceed;
        }

        public void OnElementEnd(ElementId elementId)
        {
            if (_current != null && _current.Primitives.Count > 0) _elements.Add(_current);
            _current = null;
        }

        public RenderNodeAction OnInstanceBegin(InstanceNode node)
        {
            _transforms.Push(_transforms.Peek().Multiply(node.GetTransform()));
            return RenderNodeAction.Proceed;
        }
        public void OnInstanceEnd(InstanceNode node) { if (_transforms.Count > 1) _transforms.Pop(); }
        public RenderNodeAction OnLinkBegin(LinkNode node)
        {
            _transforms.Push(_transforms.Peek().Multiply(node.GetTransform()));
            return RenderNodeAction.Proceed;
        }
        public void OnLinkEnd(LinkNode node) { if (_transforms.Count > 1) _transforms.Pop(); }
        public RenderNodeAction OnFaceBegin(FaceNode node) => RenderNodeAction.Proceed;
        public void OnFaceEnd(FaceNode node) { }
        public void OnRPC(RPCNode node) { }
        public void OnLight(LightNode node) { }

        public void OnMaterial(MaterialNode node)
        {
            var id = node.MaterialId;
            var key = id == null || id == ElementId.InvalidElementId ? "fallback" : "rvt:" + RevitApiVersion.ElementIdToString(id);
            int index;
            if (_materialIndices.TryGetValue(key, out index))
            {
                _currentMaterial = index;
                return;
            }

            var material = id == null || id == ElementId.InvalidElementId ? null : _document.GetElement(id) as Material;
            var color = material?.Color ?? node.Color;
            var data = new RevitMaterialData
            {
                Key = key,
                Name = material?.Name ?? "material",
                Red = Byte01(color?.Red ?? 200),
                Green = Byte01(color?.Green ?? 200),
                Blue = Byte01(color?.Blue ?? 200),
                Alpha = 1f - (material?.Transparency ?? (int)(node.Transparency * 100)) / 100f,
                Metallic = material == null ? 0f : material.Shininess / 128f,
                Roughness = material == null ? 0.8f : 1f - material.Smoothness / 100f
            };
            _currentMaterial = _materials.Count;
            _materials.Add(data);
            _materialIndices[key] = _currentMaterial;
        }

        public void OnPolymesh(PolymeshTopology polymesh)
        {
            if (_current == null || _currentMaterial < 0) return;
            var primitive = _current.Primitives.Find(p => p.MaterialIndex == _currentMaterial);
            if (primitive == null)
            {
                primitive = new RevitPrimitiveData { MaterialIndex = _currentMaterial };
                _current.Primitives.Add(primitive);
            }
            var baseIndex = primitive.Positions.Count / 3;
            var points = polymesh.GetPoints();
            var normals = polymesh.GetNormals();
            var uvs = polymesh.GetUVs();
            var hasUvs = uvs != null && uvs.Count == points.Count;
            var transform = _transforms.Peek();
            for (var i = 0; i < points.Count; i++)
            {
                var point = transform.OfPoint(points[i]);
                const double feetToMeters = 0.3048;
                primitive.Positions.Add((float)(point.X * feetToMeters));
                primitive.Positions.Add((float)(point.Z * feetToMeters));
                primitive.Positions.Add((float)(-point.Y * feetToMeters));
                var normal = XYZ.BasisZ;
                if (normals != null && normals.Count > 0) normal = transform.OfVector(normals[normals.Count == points.Count ? i : 0]).Normalize();
                primitive.Normals.Add((float)normal.X);
                primitive.Normals.Add((float)normal.Z);
                primitive.Normals.Add((float)(-normal.Y));
                if (hasUvs)
                {
                    primitive.Uvs.Add((float)uvs[i].U);
                    primitive.Uvs.Add((float)uvs[i].V);
                }
            }
            foreach (var facet in polymesh.GetFacets())
            {
                primitive.Indices.Add(baseIndex + facet.V1);
                primitive.Indices.Add(baseIndex + facet.V2);
                primitive.Indices.Add(baseIndex + facet.V3);
            }
        }

        private static string SafeName(Element element)
        {
            if (element == null) return "Unknown";
            return (element.Category?.Name ?? "Element") + "_" + (element.Name ?? "Element");
        }
        private static float Byte01(byte value) => value / 255f;
    }
}
