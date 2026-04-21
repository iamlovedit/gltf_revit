#define DRACO_ENCODER_EXPORTS
#include "draco_encoder_c.h"

#include <cstdlib>
#include <cstring>

#include "draco/attributes/geometry_attribute.h"
#include "draco/attributes/point_attribute.h"
#include "draco/compression/encode.h"
#include "draco/core/draco_version.h"
#include "draco/core/encoder_buffer.h"
#include "draco/mesh/mesh.h"

namespace {

int AddFloatAttribute(draco::Mesh* mesh,
                      draco::GeometryAttribute::Type type,
                      int num_components,
                      const float* data,
                      int num_vertices) {
  draco::GeometryAttribute ga;
  ga.Init(type, nullptr, num_components, draco::DT_FLOAT32,
          /*normalized=*/false,
          /*byte_stride=*/sizeof(float) * num_components,
          /*byte_offset=*/0);
  const int att_id = mesh->AddAttribute(ga, /*identity_mapping=*/true,
                                        /*num_attribute_values=*/num_vertices);
  draco::PointAttribute* att = mesh->attribute(att_id);
  for (int i = 0; i < num_vertices; ++i) {
    att->SetAttributeValue(draco::AttributeValueIndex(i),
                           data + i * num_components);
  }
  return att_id;
}

}  // namespace

extern "C" {

DRACO_API int DracoEncodeMesh(
    const float* positions, int positions_floats,
    const float* normals,   int normals_floats,
    const float* uvs,       int uvs_floats,
    const int*   indices,   int num_indices,
    int compression_level,
    int position_bits, int normal_bits, int uv_bits,
    unsigned char** out_buffer, int* out_size,
    int* pos_attr_id, int* nrm_attr_id, int* uv_attr_id) {

  if (!positions || positions_floats <= 0 || !indices || num_indices <= 0 ||
      !out_buffer || !out_size || !pos_attr_id || !nrm_attr_id || !uv_attr_id) {
    return 1;
  }
  if (positions_floats % 3 != 0) return 2;
  if (num_indices % 3 != 0) return 3;

  const int num_vertices = positions_floats / 3;
  const int num_faces = num_indices / 3;

  draco::Mesh mesh;
  mesh.set_num_points(num_vertices);
  mesh.SetNumFaces(num_faces);

  for (int f = 0; f < num_faces; ++f) {
    draco::Mesh::Face face;
    face[0] = draco::PointIndex(indices[3 * f + 0]);
    face[1] = draco::PointIndex(indices[3 * f + 1]);
    face[2] = draco::PointIndex(indices[3 * f + 2]);
    mesh.SetFace(draco::FaceIndex(f), face);
  }

  *pos_attr_id = AddFloatAttribute(
      &mesh, draco::GeometryAttribute::POSITION, 3, positions, num_vertices);
  *nrm_attr_id = -1;
  *uv_attr_id = -1;

  if (normals && normals_floats == positions_floats) {
    *nrm_attr_id = AddFloatAttribute(
        &mesh, draco::GeometryAttribute::NORMAL, 3, normals, num_vertices);
  }
  if (uvs && uvs_floats == num_vertices * 2) {
    *uv_attr_id = AddFloatAttribute(
        &mesh, draco::GeometryAttribute::TEX_COORD, 2, uvs, num_vertices);
  }

  if (compression_level < 0) compression_level = 0;
  if (compression_level > 10) compression_level = 10;

  draco::Encoder encoder;
  const int speed = 10 - compression_level;
  encoder.SetSpeedOptions(speed, speed);
  encoder.SetAttributeQuantization(draco::GeometryAttribute::POSITION,
                                   position_bits);
  if (*nrm_attr_id >= 0) {
    encoder.SetAttributeQuantization(draco::GeometryAttribute::NORMAL,
                                     normal_bits);
  }
  if (*uv_attr_id >= 0) {
    encoder.SetAttributeQuantization(draco::GeometryAttribute::TEX_COORD,
                                     uv_bits);
  }

  draco::EncoderBuffer buffer;
  const draco::Status status = encoder.EncodeMeshToBuffer(mesh, &buffer);
  if (!status.ok()) return 10;

  const int size = static_cast<int>(buffer.size());
  unsigned char* out = static_cast<unsigned char*>(std::malloc(size));
  if (!out) return 20;
  std::memcpy(out, buffer.data(), size);
  *out_buffer = out;
  *out_size = size;
  return 0;
}

DRACO_API void DracoFreeBuffer(unsigned char* buffer) {
  std::free(buffer);
}

DRACO_API const char* DracoGetVersion() {
  return draco::kDracoVersion;
}

}  // extern "C"
