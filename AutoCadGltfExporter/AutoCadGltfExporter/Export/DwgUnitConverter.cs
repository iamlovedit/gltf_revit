using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AutoCadGltfExporter.Export
{
    // Maps DWG drawing units (INSUNITS) to meters and rotates AutoCAD's
    // Z-up world to glTF's Y-up: (x, y, z) -> (x, z, -y) * unit-factor.
    internal static class DwgUnitConverter
    {
        public static double UnitFactorToMeters(UnitsValue u)
        {
            switch (u)
            {
                case UnitsValue.Millimeters: return 0.001;
                case UnitsValue.Centimeters: return 0.01;
                case UnitsValue.Decimeters:  return 0.1;
                case UnitsValue.Meters:      return 1.0;
                case UnitsValue.Kilometers:  return 1000.0;
                case UnitsValue.Inches:      return 0.0254;
                case UnitsValue.Feet:        return 0.3048;
                case UnitsValue.Yards:       return 0.9144;
                case UnitsValue.Miles:       return 1609.344;
                case UnitsValue.MicroInches: return 0.0254e-6;
                case UnitsValue.Mils:        return 0.0254e-3;
                case UnitsValue.Angstroms:   return 1e-10;
                case UnitsValue.Nanometers:  return 1e-9;
                case UnitsValue.Microns:     return 1e-6;
                case UnitsValue.Hectometers: return 100.0;
                case UnitsValue.Gigameters:  return 1e9;
                case UnitsValue.Astronomical: return 1.495978707e11;
                case UnitsValue.LightYears:  return 9.4607304725808e15;
                case UnitsValue.Parsecs:     return 3.0856775814913673e16;
                case UnitsValue.Undefined:
                default: return 1.0;
            }
        }

        public static void ToGltfPoint(Point3d p, double scale, out float x, out float y, out float z)
        {
            x = (float)(p.X * scale);
            y = (float)(p.Z * scale);
            z = (float)(-p.Y * scale);
        }

        public static void ToGltfNormal(Vector3d n, out float x, out float y, out float z)
        {
            x = (float)n.X;
            y = (float)n.Z;
            z = (float)(-n.Y);
        }

        // Convert an AutoCAD 4x4 transform into a column-major float[16] suitable
        // for GltfNode.Matrix, accounting for axis swap (Z-up -> Y-up) and the
        // unit scale factor.
        public static float[] ToGltfMatrix(Matrix3d m, double scale)
        {
            // Axis swap matrix S = [[1,0,0],[0,0,-1],[0,1,0]] sends (x,y,z) -> (x,z,-y).
            // Final = S * (scale * m); apply S to translation and to each column basis vector.
            var t = new[]
            {
                m[0, 3], m[1, 3], m[2, 3]
            };
            var c0 = new[] { m[0, 0], m[1, 0], m[2, 0] };
            var c1 = new[] { m[0, 1], m[1, 1], m[2, 1] };
            var c2 = new[] { m[0, 2], m[1, 2], m[2, 2] };

            var result = new float[16];
            // glTF expects column-major: result[col*4 + row]
            WriteColumn(result, 0, Swap(c0));
            WriteColumn(result, 1, Swap(c1));
            WriteColumn(result, 2, Swap(c2));

            // Translation column (col 3): swap axes, then scale.
            var tt = Swap(t);
            result[12] = (float)(tt[0] * scale);
            result[13] = (float)(tt[1] * scale);
            result[14] = (float)(tt[2] * scale);
            result[15] = 1f;
            return result;
        }

        private static double[] Swap(double[] v) => new[] { v[0], v[2], -v[1] };

        private static void WriteColumn(float[] dst, int col, double[] basis)
        {
            dst[col * 4 + 0] = (float)basis[0];
            dst[col * 4 + 1] = (float)basis[1];
            dst[col * 4 + 2] = (float)basis[2];
            dst[col * 4 + 3] = 0f;
        }
    }
}
