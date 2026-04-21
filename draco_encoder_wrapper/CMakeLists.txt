cmake_minimum_required(VERSION 3.12)
project(draco_encoder_wrapper CXX)

set(CMAKE_CXX_STANDARD 14)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

# Pin draco to a static library we link into our DLL.
set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
set(DRACO_JS_GLUE OFF CACHE BOOL "" FORCE)
set(DRACO_WASM OFF CACHE BOOL "" FORCE)
set(DRACO_UNITY_PLUGIN OFF CACHE BOOL "" FORCE)
set(DRACO_MAYA_PLUGIN OFF CACHE BOOL "" FORCE)
set(DRACO_TESTS OFF CACHE BOOL "" FORCE)
set(DRACO_INSTALL OFF CACHE BOOL "" FORCE)
set(DRACO_TRANSCODER_SUPPORTED OFF CACHE BOOL "" FORCE)
# Restrict the encoder to features supported by the glTF Draco extension
# so the bitstream is compatible with any standard glTF Draco decoder.
set(DRACO_GLTF_BITSTREAM ON CACHE BOOL "" FORCE)

set(DRACO_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/../draco-1.5.7")
add_subdirectory(${DRACO_ROOT} draco_build EXCLUDE_FROM_ALL)

add_library(draco_encoder_wrapper SHARED
  draco_encoder_c.cc
  draco_encoder_c.h)

target_compile_definitions(draco_encoder_wrapper PRIVATE DRACO_ENCODER_EXPORTS)
target_include_directories(draco_encoder_wrapper PRIVATE
  ${DRACO_ROOT}/src
  ${CMAKE_BINARY_DIR})
target_link_libraries(draco_encoder_wrapper PRIVATE draco::draco)

set_target_properties(draco_encoder_wrapper PROPERTIES
  OUTPUT_NAME "draco_encoder"
  RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")

# Copy the produced DLL into the plugin output folder (../output) so that
# the Revit plugin finds it next to RevitGltfExporter.dll.
add_custom_command(TARGET draco_encoder_wrapper POST_BUILD
  COMMAND ${CMAKE_COMMAND} -E make_directory
    "${CMAKE_SOURCE_DIR}/../output"
  COMMAND ${CMAKE_COMMAND} -E copy_if_different
    "$<TARGET_FILE:draco_encoder_wrapper>"
    "${CMAKE_SOURCE_DIR}/../output/")
