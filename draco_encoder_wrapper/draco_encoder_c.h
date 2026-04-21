#ifndef DRACO_ENCODER_C_H_
#define DRACO_ENCODER_C_H_

#if defined(_MSC_VER)
#  if defined(DRACO_ENCODER_EXPORTS)
#    define DRACO_API __declspec(dllexport)
#  else
#    define DRACO_API __declspec(dllimport)
#  endif
#else
#  define DRACO_API
#endif

#ifdef __cplusplus
extern "C" {
#endif

DRACO_API int DracoEncodeMesh(
    const float* positions, int positions_floats,
    const float* normals,   int normals_floats,
    const float* uvs,       int uvs_floats,
    const int*   indices,   int num_indices,
    int compression_level,
    int position_bits, int normal_bits, int uv_bits,
    unsigned char** out_buffer, int* out_size,
    int* pos_attr_id, int* nrm_attr_id, int* uv_attr_id);

DRACO_API void DracoFreeBuffer(unsigned char* buffer);

DRACO_API const char* DracoGetVersion();

#ifdef __cplusplus
}
#endif

#endif
