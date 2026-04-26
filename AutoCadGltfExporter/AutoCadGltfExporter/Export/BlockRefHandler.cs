using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;

namespace AutoCadGltfExporter.Export
{
    // Caches BlockTableRecord -> glTF mesh index so multiple BlockReferences
    // pointing at the same definition reuse a single mesh, letting the viewer
    // collapse them into THREE.InstancedMesh on the front end.
    internal class BlockRefHandler
    {
        // Key = BTR ObjectId.Handle (stable across the export session).
        private readonly Dictionary<string, int?> _meshByDef = new Dictionary<string, int?>();

        public bool TryGetMesh(BlockTableRecord btr, out int meshIndex)
        {
            meshIndex = -1;
            if (btr == null) return false;
            var key = btr.ObjectId.Handle.Value.ToString("X");
            if (_meshByDef.TryGetValue(key, out var v) && v.HasValue)
            {
                meshIndex = v.Value;
                return true;
            }
            return false;
        }

        // Pass null to mark "definition was processed but produced no mesh"
        // so we don't repeatedly try to tessellate an empty BTR.
        public void RegisterMesh(BlockTableRecord btr, int? meshIndex)
        {
            if (btr == null) return;
            var key = btr.ObjectId.Handle.Value.ToString("X");
            _meshByDef[key] = meshIndex;
        }

        public bool HasSeen(BlockTableRecord btr)
        {
            if (btr == null) return false;
            return _meshByDef.ContainsKey(btr.ObjectId.Handle.Value.ToString("X"));
        }
    }
}
