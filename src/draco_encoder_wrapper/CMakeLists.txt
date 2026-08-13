cmake_minimum_required(VERSION 3.12)
project(draco_encoder_wrapper CXX)

if(POLICY CMP0135)
  cmake_policy(SET CMP0135 NEW)
endif()
if(POLICY CMP0169)
  # Keep the CMake 3.12-compatible populate flow without a deprecation warning.
  cmake_policy(SET CMP0169 OLD)
endif()

include(FetchContent)

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

set(DRACO_VERSION "1.5.7")
set(
  DRACO_DOWNLOAD_URL
  "https://codeload.github.com/google/draco/tar.gz/refs/tags/${DRACO_VERSION}")
set(
  DRACO_DOWNLOAD_SHA256
  "BF6B105B79223EAB2B86795363DFE5E5356050006A96521477973ABA8F036FE1")
set(
  DRACO_SOURCE_DIR
  ""
  CACHE PATH
  "Optional path to an existing Draco source tree for offline builds")

if(DRACO_SOURCE_DIR)
  get_filename_component(DRACO_ROOT "${DRACO_SOURCE_DIR}" ABSOLUTE)
  if(NOT EXISTS "${DRACO_ROOT}/CMakeLists.txt")
    message(FATAL_ERROR
      "DRACO_SOURCE_DIR does not contain a Draco source tree: ${DRACO_ROOT}")
  endif()
  message(STATUS "Using local Draco source: ${DRACO_ROOT}")
else()
  FetchContent_Declare(
    draco
    URL "${DRACO_DOWNLOAD_URL}"
    URL_HASH "SHA256=${DRACO_DOWNLOAD_SHA256}")
  FetchContent_GetProperties(draco)
  set(
    DRACO_FETCH_STAMP
    "${draco_SOURCE_DIR}/.gltf-revit-${DRACO_DOWNLOAD_SHA256}.stamp")
  if(EXISTS "${DRACO_FETCH_STAMP}")
    set(DRACO_ROOT "${draco_SOURCE_DIR}")
    message(STATUS "Using cached Draco source: ${DRACO_ROOT}")
  else()
    FetchContent_Populate(draco)
    file(WRITE "${DRACO_FETCH_STAMP}" "${DRACO_DOWNLOAD_SHA256}\n")
    set(DRACO_ROOT "${draco_SOURCE_DIR}")
  endif()
endif()

set(DRACO_VERSION_HEADER "${DRACO_ROOT}/src/draco/core/draco_version.h")
if(NOT EXISTS "${DRACO_VERSION_HEADER}")
  message(FATAL_ERROR "Draco version header not found: ${DRACO_VERSION_HEADER}")
endif()
file(READ "${DRACO_VERSION_HEADER}" DRACO_VERSION_HEADER_CONTENT)
if(NOT DRACO_VERSION_HEADER_CONTENT MATCHES
   "kDracoVersion\\[\\][ \t]*=[ \t]*\"${DRACO_VERSION}\"")
  message(FATAL_ERROR
    "Draco source must be version ${DRACO_VERSION}: ${DRACO_ROOT}")
endif()

add_subdirectory("${DRACO_ROOT}" draco_build EXCLUDE_FROM_ALL)

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

# Copy the produced DLL into the plugin output folder (../../output) so that
# the Revit plugin finds it next to RevitGltfExporter.dll.
add_custom_command(TARGET draco_encoder_wrapper POST_BUILD
  COMMAND ${CMAKE_COMMAND} -E make_directory
    "${CMAKE_SOURCE_DIR}/../../output"
  COMMAND ${CMAKE_COMMAND} -E copy_if_different
    "$<TARGET_FILE:draco_encoder_wrapper>"
    "${CMAKE_SOURCE_DIR}/../../output/")
