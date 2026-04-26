using System.Collections.Generic;

namespace GltfExporter.Shared
{
    // Helper for emitting glTF LINES (mode=1) primitives. Indices are pairs
    // (v0,v1) (v2,v3) ... matching THREE.LineSegments semantics on the viewer side.
    //
    // Draco does not apply to line geometry, so this always writes uncompressed
    // float32 positions + uint32 indices.
    public class LineBuilder
    {
        private readonly GltfBuilder _builder;

        public LineBuilder(GltfBuilder builder)
        {
            _builder = builder;
        }

        public GltfPrimitive AddLinePrimitive(IList<float> positions, IList<int> indices, int? material)
        {
            var minMax = ComputeMinMax(positions);
            var posAcc = _builder.AddFloat3Accessor(positions, minMax.min, minMax.max, GltfTarget.ArrayBuffer);
            var idxAcc = _builder.AddIndexAccessor(indices);

            var prim = new GltfPrimitive
            {
                Material = material,
                Indices = idxAcc,
                Mode = GltfPrimitiveMode.Lines
            };
            prim.Attributes["POSITION"] = posAcc;
            return prim;
        }

        private static (float[] min, float[] max) ComputeMinMax(IList<float> positions)
        {
            if (positions.Count == 0)
            {
                return (new[] { 0f, 0f, 0f }, new[] { 0f, 0f, 0f });
            }
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
    }
}
