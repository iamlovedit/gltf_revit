using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;

namespace AutoCadGltfExporter.Export
{
    // Captures tessellated triangles from 3D entities by implementing WorldDraw /
    // WorldGeometry and invoking entity.WorldDraw(this). Shell() is the primary
    // sink — AutoCAD pushes polygonal face data there for Solid3d / Surface /
    // Region / Body / (solid-fill Hatch). Mesh() handles SubDMesh / PolygonMesh.
    internal class SolidTessellator
    {
        private readonly double _unitScale;

        public SolidTessellator(double unitScale)
        {
            _unitScale = unitScale;
        }

        public TessellationResult Tessellate(Entity entity)
        {
            var sink = new CaptureGeometry();
            var drawer = new CaptureDraw(sink);

            // WorldDraw returns true when the geometry was drawn; ignore.
            entity.WorldDraw(drawer);

            var positions = new List<float>(sink.Vertices.Count * 3);
            var normals = new List<float>(sink.Vertices.Count * 3);
            foreach (var v in sink.Vertices)
            {
                float x, y, z;
                DwgUnitConverter.ToGltfPoint(v, _unitScale, out x, out y, out z);
                positions.Add(x);
                positions.Add(y);
                positions.Add(z);
            }

            // Compute per-vertex normals by accumulating face normals (area-weighted).
            var tmpNormals = new Vector3d[sink.Vertices.Count];
            for (int i = 0; i < sink.Indices.Count; i += 3)
            {
                var a = sink.Vertices[sink.Indices[i]];
                var b = sink.Vertices[sink.Indices[i + 1]];
                var c = sink.Vertices[sink.Indices[i + 2]];
                var faceN = (b - a).CrossProduct(c - a);
                tmpNormals[sink.Indices[i]] = tmpNormals[sink.Indices[i]].Add(faceN);
                tmpNormals[sink.Indices[i + 1]] = tmpNormals[sink.Indices[i + 1]].Add(faceN);
                tmpNormals[sink.Indices[i + 2]] = tmpNormals[sink.Indices[i + 2]].Add(faceN);
            }
            foreach (var n in tmpNormals)
            {
                var len = n.Length;
                var nn = len > 1e-12 ? n / len : Vector3d.ZAxis;
                float nx, ny, nz;
                DwgUnitConverter.ToGltfNormal(nn, out nx, out ny, out nz);
                normals.Add(nx);
                normals.Add(ny);
                normals.Add(nz);
            }

            return new TessellationResult
            {
                RawPositions = sink.Vertices,
                Positions = positions,
                Normals = normals,
                Indices = sink.Indices
            };
        }

        public class TessellationResult
        {
            public List<Point3d> RawPositions;
            public List<float> Positions;
            public List<float> Normals;
            public List<int> Indices;
            public bool IsEmpty => Indices == null || Indices.Count == 0;
        }

        // Minimal WorldDraw implementation: we don't need traits/context, just a Geometry sink.
        private class CaptureDraw : WorldDraw
        {
            private readonly CaptureGeometry _geom;
            public CaptureDraw(CaptureGeometry g) { _geom = g; }

            public override WorldGeometry Geometry => _geom;
            public override SubEntityTraits SubEntityTraits => null;
            public override Geometry RawGeometry => _geom;
            public override Context Context => null;

            public override bool RegenAbort => false;
            public override bool IsDragging => false;
            public override RegenType RegenType => RegenType.StandardDisplay;
            public override int NumberOfIsolines => 0;

            public override double Deviation(DeviationType type, Point3d point)
            {
                // Smaller value = finer tessellation. AutoCAD clamps by FACETRES anyway.
                return 0.001;
            }
        }

        // Capture only Shell/Mesh polygon data; other drawing primitives are
        // consumed silently so AutoCAD does not fall back to wireframe.
        private class CaptureGeometry : WorldGeometry
        {
            public readonly List<Point3d> Vertices = new List<Point3d>();
            public readonly List<int> Indices = new List<int>();

            private Matrix3d _modelTransform = Matrix3d.Identity;
            private readonly Stack<Matrix3d> _transformStack = new Stack<Matrix3d>();

            public override Matrix3d ModelToWorldTransform => _modelTransform;

            public override Matrix3d WorldToModelTransform
            {
                get
                {
                    try { return _modelTransform.Inverse(); }
                    catch { return Matrix3d.Identity; }
                }
            }

            public override bool PushModelTransform(Matrix3d matrix)
            {
                _transformStack.Push(_modelTransform);
                _modelTransform = _modelTransform * matrix;
                return true;
            }

            public override bool PushModelTransform(Vector3d normal)
            {
                _transformStack.Push(_modelTransform);
                return true;
            }

            public override bool PopModelTransform()
            {
                if (_transformStack.Count > 0) _modelTransform = _transformStack.Pop();
                return true;
            }

            public override Matrix3d PushOrientationTransform(OrientationBehavior behavior)
            {
                var previous = _modelTransform;
                _transformStack.Push(previous);
                return previous;
            }

            public override Matrix3d PushPositionTransform(PositionBehavior behavior, Point3d offset)
            {
                var previous = _modelTransform;
                _transformStack.Push(previous);
                _modelTransform = _modelTransform * Matrix3d.Displacement(offset - Point3d.Origin);
                return previous;
            }

            public override Matrix3d PushPositionTransform(PositionBehavior behavior, Point2d offset)
            {
                return PushPositionTransform(behavior, new Point3d(offset.X, offset.Y, 0.0));
            }

            public override Matrix3d PushScaleTransform(ScaleBehavior behavior, Point3d extents)
            {
                var previous = _modelTransform;
                _transformStack.Push(previous);
                return previous;
            }

            public override Matrix3d PushScaleTransform(ScaleBehavior behavior, Point2d extents)
            {
                var previous = _modelTransform;
                _transformStack.Push(previous);
                return previous;
            }

            public override bool PushClipBoundary(ClipBoundary boundary) => true;

            public override void PopClipBoundary()
            {
            }

            public override void StartAttributesSegment()
            {
            }

            public override void SetExtents(Extents3d extents)
            {
            }

            public override bool Shell(
                Point3dCollection points,
                IntegerCollection faces,
                EdgeData edgeData, FaceData faceData,
                VertexData vertexData,
                bool bAutoGenerateNormals)
            {
                var baseIdx = Vertices.Count;
                for (int i = 0; i < points.Count; i++)
                {
                    Vertices.Add(points[i].TransformBy(_modelTransform));
                }

                int j = 0;
                while (j < faces.Count)
                {
                    int cnt = faces[j++];
                    if (cnt == 0) break;
                    if (cnt < 0)
                    {
                        // Hole loop — skip. Not common in solid tessellation output.
                        j += -cnt;
                        continue;
                    }

                    // Triangulate as fan: (v0, v1, v2), (v0, v2, v3), ...
                    int v0 = baseIdx + faces[j];
                    for (int t = 1; t < cnt - 1; t++)
                    {
                        Indices.Add(v0);
                        Indices.Add(baseIdx + faces[j + t]);
                        Indices.Add(baseIdx + faces[j + t + 1]);
                    }
                    j += cnt;
                }
                return true;
            }

            public override bool Mesh(
                int rows, int columns, Point3dCollection points,
                EdgeData edgeData, FaceData faceData,
                VertexData vertexData,
                bool bAutoGenerateNormals)
            {
                var baseIdx = Vertices.Count;
                for (int i = 0; i < points.Count; i++)
                {
                    Vertices.Add(points[i].TransformBy(_modelTransform));
                }
                // Emit two triangles per quad cell.
                for (int r = 0; r < rows - 1; r++)
                {
                    for (int c = 0; c < columns - 1; c++)
                    {
                        int a = baseIdx + r * columns + c;
                        int b = a + 1;
                        int d = baseIdx + (r + 1) * columns + c;
                        int e = d + 1;
                        Indices.Add(a); Indices.Add(b); Indices.Add(e);
                        Indices.Add(a); Indices.Add(e); Indices.Add(d);
                    }
                }
                return true;
            }

            public override bool WorldLine(Point3d start, Point3d end) => true;
            public override bool Polyline(Point3dCollection points, Vector3d normal, IntPtr subEntityMarker) => true;
            public override bool Polyline(Autodesk.AutoCAD.DatabaseServices.Polyline value, int fromIndex, int segments) => true;
            public override bool Polyline(Autodesk.AutoCAD.GraphicsInterface.Polyline polylineObj) => true;
            public override bool Polygon(Point3dCollection points) => true;
            public override bool Polypoint(Point3dCollection points, Vector3dCollection normals, IntPtrCollection subentityMarkers) => true;
            public override bool PolyPolygon(
                UInt32Collection numPolygonPositions,
                Point3dCollection polygonPositions,
                UInt32Collection numPolygonPoints,
                Point3dCollection polygonPoints,
                EntityColorCollection outlineColors,
                LinetypeCollection outlineTypes,
                EntityColorCollection fillColors,
                TransparencyCollection fillOpacities) => true;
            public override bool PolyPolyline(PolylineCollection polylineCollection) => true;
            public override bool Circle(Point3d center, double radius, Vector3d normal) => true;
            public override bool Circle(Point3d p1, Point3d p2, Point3d p3) => true;
            public override bool CircularArc(Point3d center, double radius, Vector3d normal, Vector3d startVector, double sweepAngle, ArcType type) => true;
            public override bool CircularArc(Point3d p1, Point3d p2, Point3d p3, ArcType type) => true;
            public override bool EllipticalArc(Point3d center, Vector3d normal, double majorAxisLength, double minorAxisLength, double startDegreeInRads, double endDegreeInRads, double tiltDegreeInRads, ArcType arcType) => true;
            public override bool Edge(Curve2dCollection e) => true;
            public override bool Text(Point3d position, Vector3d normal, Vector3d direction, string msg, bool raw, TextStyle style) => true;
            public override bool Text(Point3d position, Vector3d normal, Vector3d direction, double height, double width, double oblique, string msg) => true;
            public override bool Xline(Point3d p1, Point3d p2) => true;
            public override bool Ray(Point3d p1, Point3d p2) => true;
            public override bool Image(ImageBGRA32 image, Point3d position, Vector3d u, Vector3d v, TransparencyMode transparencyMode) => true;
            public override bool Image(ImageBGRA32 image, Point3d position, Vector3d u, Vector3d v) => true;
            public override bool RowOfDots(int count, Point3d start, Vector3d step) => true;
            public override bool OwnerDraw(GdiDrawObject gdiDrawObject, Point3d position, Vector3d u, Vector3d v) => true;
            public override bool Draw(Drawable drawable) => true;
        }
    }
}
