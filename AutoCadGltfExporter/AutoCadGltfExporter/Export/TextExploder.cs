using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AutoCadGltfExporter.Export
{
    // Decomposes text, dimensions, leaders, and pattern hatches into the
    // primitive 2D entities (Lines, Polylines, Solid fills) AutoCAD uses
    // to draw them, then hands the results back to the caller as a flat
    // collection that the standard CurveSampler / SolidTessellator path
    // can consume.
    //
    // Caller is responsible for disposing the returned DBObjects (they
    // are owned only by us, not by any database).
    internal static class TextExploder
    {
        public static DBObjectCollection ExplodeRecursive(Entity entity, int maxDepth = 4)
        {
            var result = new DBObjectCollection();
            ExplodeInto(entity, result, depth: 0, maxDepth: maxDepth);
            return result;
        }

        private static void ExplodeInto(Entity entity, DBObjectCollection sink, int depth, int maxDepth)
        {
            if (entity == null) return;
            if (depth >= maxDepth)
            {
                sink.Add(entity);
                return;
            }

            // Lines / arcs / polylines and the like are already primitive enough
            // for CurveSampler — pass them through.
            if (entity is Curve)
            {
                sink.Add(entity);
                return;
            }

            DBObjectCollection bits = null;
            try
            {
                bits = new DBObjectCollection();
                entity.Explode(bits);
            }
            catch
            {
                // Entities that can't be exploded (e.g. proxy, MLine in some cases)
                // get added as-is.
                sink.Add(entity);
                return;
            }

            if (bits.Count == 0)
            {
                sink.Add(entity);
                return;
            }

            foreach (DBObject obj in bits)
            {
                var child = obj as Entity;
                if (child != null) ExplodeInto(child, sink, depth + 1, maxDepth);
                else obj.Dispose();
            }
        }

        // Strip non-CAD characters that cause problems in glTF extras strings.
        public static string SanitizeText(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;
            return raw
                .Replace("\\P", " ")           // MText paragraph break
                .Replace("\\~", " ")           // MText non-breaking space
                .Replace("\r\n", " ")
                .Replace("\n", " ")
                .Replace("\t", " ");
        }
    }
}
