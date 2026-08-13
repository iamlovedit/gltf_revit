using System;
using System.Runtime.InteropServices;

namespace GltfExporter.Shared
{
    // P/Invoke bindings to draco_encoder.dll (built from draco-1.5.7 via
    // ../draco_encoder_wrapper/build.ps1). The DLL is expected to live next
    // to the consuming assembly (Revit/AutoCAD plugin dll).
    public static class DracoNative
    {
        private const string Dll = "draco_encoder";

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int DracoEncodeMesh(
            float[] positions, int positionsFloats,
            float[] normals, int normalsFloats,
            float[] uvs, int uvsFloats,
            int[] indices, int numIndices,
            int compressionLevel,
            int positionBits, int normalBits, int uvBits,
            out IntPtr outBuffer, out int outSize,
            out int posAttrId, out int nrmAttrId, out int uvAttrId);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void DracoFreeBuffer(IntPtr buffer);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr DracoGetVersion();

        public static string GetVersion()
        {
            var p = DracoGetVersion();
            return p == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(p);
        }

        public static DracoEncodedPrimitive Encode(
            float[] positions,
            float[] normals,
            float[] uvs,
            int[] indices,
            int compressionLevel)
        {
            if (positions == null || positions.Length == 0)
                throw new ArgumentException("positions required", nameof(positions));
            if (indices == null || indices.Length == 0)
                throw new ArgumentException("indices required", nameof(indices));

            IntPtr nativeBuf;
            int nativeSize;
            int posId, nrmId, uvId;
            var rc = DracoEncodeMesh(
                positions, positions.Length,
                normals, normals?.Length ?? 0,
                uvs, uvs?.Length ?? 0,
                indices, indices.Length,
                compressionLevel,
                14, 10, 12,
                out nativeBuf, out nativeSize,
                out posId, out nrmId, out uvId);

            if (rc != 0 || nativeBuf == IntPtr.Zero)
                throw new InvalidOperationException("Draco encode failed (rc=" + rc + ")");

            try
            {
                var managed = new byte[nativeSize];
                Marshal.Copy(nativeBuf, managed, 0, nativeSize);
                return new DracoEncodedPrimitive(managed, posId, nrmId, uvId);
            }
            finally
            {
                DracoFreeBuffer(nativeBuf);
            }
        }
    }

    public sealed class DracoEncodedPrimitive
    {
        public readonly byte[] EncodedBytes;
        public readonly int PositionAttrId;
        public readonly int NormalAttrId;
        public readonly int UvAttrId;

        public DracoEncodedPrimitive(byte[] encoded, int posId, int nrmId, int uvId)
        {
            EncodedBytes = encoded;
            PositionAttrId = posId;
            NormalAttrId = nrmId;
            UvAttrId = uvId;
        }
    }
}
