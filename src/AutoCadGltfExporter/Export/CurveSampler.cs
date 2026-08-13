using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AutoCadGltfExporter.Export
{
    // Samples DWG 2D/3D analytic curves into a flat list of Point3d
    // suitable for emission as glTF LINES (mode=1) primitives.
    //
    // The output convention is "polyline as connected segments":
    // pts[0]-pts[1], pts[1]-pts[2], ..., pts[n-2]-pts[n-1].
    // The DwgExportContext is responsible for converting the polyline
    // form into LINES index pairs (v0,v1, v1,v2, ...).
    internal class CurveSampler
    {
        private readonly double _chordHeight;

        public CurveSampler(double chordHeight)
        {
            _chordHeight = chordHeight;
        }

        public List<Point3d> Sample(Curve curve)
        {
            switch (curve)
            {
                case Line line:
                    return new List<Point3d> { line.StartPoint, line.EndPoint };
                case Arc arc:
                    return SampleArc(arc.Center, arc.Radius, arc.Normal, arc.StartAngle, arc.EndAngle);
                case Circle circle:
                    return SampleArc(circle.Center, circle.Radius, circle.Normal, 0.0, Math.PI * 2);
                case Ellipse ellipse:
                    return SampleEllipse(ellipse);
                case Polyline pl:
                    return SamplePolyline(pl);
                case Polyline2d pl2:
                    return SamplePolyline2d(pl2);
                case Polyline3d pl3:
                    return SamplePolyline3d(pl3);
                case Spline spline:
                    return SampleSpline(spline);
                default:
                    return SampleByGetSamplePoints(curve);
            }
        }

        private List<Point3d> SampleArc(Point3d center, double radius, Vector3d normal, double startAngle, double endAngle)
        {
            if (radius <= 0)
            {
                return new List<Point3d> { center };
            }

            // chord-height -> max angle step
            var arg = 1.0 - _chordHeight / radius;
            var step = arg <= -1.0 ? Math.PI : (arg >= 1.0 ? 0.05 : 2.0 * Math.Acos(arg));
            if (step < 0.01) step = 0.01;

            var sweep = endAngle - startAngle;
            if (sweep <= 0) sweep += Math.PI * 2;
            var n = Math.Max(2, (int)Math.Ceiling(sweep / step));

            // Build a local frame on the arc plane.
            var z = normal.GetNormal();
            var x = z.GetPerpendicularVector();
            var y = z.CrossProduct(x);

            var pts = new List<Point3d>(n + 1);
            for (int i = 0; i <= n; i++)
            {
                var a = startAngle + sweep * i / n;
                var p = center + radius * Math.Cos(a) * x + radius * Math.Sin(a) * y;
                pts.Add(p);
            }
            return pts;
        }

        private List<Point3d> SampleEllipse(Ellipse e)
        {
            var n = 64;
            var pts = new List<Point3d>(n + 1);
            var sweep = e.EndParam - e.StartParam;
            for (int i = 0; i <= n; i++)
            {
                var t = e.StartParam + sweep * i / n;
                pts.Add(e.GetPointAtParameter(t));
            }
            return pts;
        }

        private List<Point3d> SamplePolyline(Polyline pl)
        {
            // Polyline can have arc segments (bulge != 0). Walk each segment.
            var pts = new List<Point3d>();
            var n = pl.NumberOfVertices;
            for (int i = 0; i < n; i++)
            {
                var segType = i < n - 1 || pl.Closed ? pl.GetSegmentType(i) : SegmentType.Empty;
                if (i == 0) pts.Add(pl.GetPoint3dAt(0));

                switch (segType)
                {
                    case SegmentType.Line:
                    {
                        var next = (i + 1) % n;
                        pts.Add(pl.GetPoint3dAt(next));
                        break;
                    }
                    case SegmentType.Arc:
                    {
                        var arc = pl.GetArcSegmentAt(i);
                        var sub = SampleArc(arc.Center, arc.Radius, arc.Normal, arc.StartAngle, arc.EndAngle);
                        // skip the first point of the sampled arc (already in pts as seg start)
                        for (int k = 1; k < sub.Count; k++) pts.Add(sub[k]);
                        break;
                    }
                    case SegmentType.Coincident:
                    case SegmentType.Empty:
                    case SegmentType.Point:
                    default:
                        break;
                }
            }
            return pts;
        }

        private List<Point3d> SamplePolyline2d(Polyline2d pl2)
        {
            var pts = new List<Point3d>();
            using (var tr = pl2.Database.TransactionManager.StartOpenCloseTransaction())
            {
                foreach (ObjectId id in pl2)
                {
                    var v = tr.GetObject(id, OpenMode.ForRead) as Vertex2d;
                    if (v != null) pts.Add(v.Position);
                }
                tr.Commit();
            }
            return pts;
        }

        private List<Point3d> SamplePolyline3d(Polyline3d pl3)
        {
            var pts = new List<Point3d>();
            using (var tr = pl3.Database.TransactionManager.StartOpenCloseTransaction())
            {
                foreach (ObjectId id in pl3)
                {
                    var v = tr.GetObject(id, OpenMode.ForRead) as PolylineVertex3d;
                    if (v != null) pts.Add(v.Position);
                }
                tr.Commit();
            }
            return pts;
        }

        private List<Point3d> SampleSpline(Spline spline)
        {
            return SampleByGetSamplePoints(spline);
        }

        private List<Point3d> SampleByGetSamplePoints(Curve curve)
        {
            // Fall-back: parametric sampling via Curve.GetParameterAtPoint pairs.
            var pts = new List<Point3d>();
            try
            {
                var s = curve.StartParam;
                var e = curve.EndParam;
                var n = 64;
                for (int i = 0; i <= n; i++)
                {
                    var t = s + (e - s) * i / n;
                    pts.Add(curve.GetPointAtParameter(t));
                }
            }
            catch
            {
                pts.Add(curve.StartPoint);
                pts.Add(curve.EndPoint);
            }
            return pts;
        }
    }
}
