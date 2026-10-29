# Import the shared Rust extractor, desktop setup and C++ bridge once per build.
include_guard(GLOBAL)
include(FetchContent)
if(DEFINED ENV{OC_CORROSION_SOURCE_DIR} AND
   EXISTS "$ENV{OC_CORROSION_SOURCE_DIR}/CMakeLists.txt")
    set(FETCHCONTENT_SOURCE_DIR_CORROSION "$ENV{OC_CORROSION_SOURCE_DIR}" CACHE PATH
        "Pinned Corrosion source bundled by the host toolchain" FORCE)
endif()
if(WIN32 AND CMAKE_CXX_COMPILER_ID STREQUAL "Clang")
    set(Rust_CARGO_TARGET x86_64-pc-windows-gnullvm CACHE STRING "Rust target for llvm-mingw" FORCE)
endif()
FetchContent_Declare(corrosion
    URL https://github.com/corrosion-rs/corrosion/archive/refs/tags/v0.6.1.zip
    URL_HASH SHA256=5c1c5c6ab7196fe4466321c14e9698ec2d0a977c89fd9f1639a37fcffdf604f2
    DOWNLOAD_EXTRACT_TIMESTAMP TRUE)
set(CORROSION_BUILD_TESTS OFF CACHE BOOL "" FORCE)
FetchContent_MakeAvailable(corrosion)

get_filename_component(OC_RUST_MANIFEST "${CMAKE_CURRENT_LIST_DIR}/../Cargo.toml" ABSOLUTE)
if(CMAKE_SYSTEM_NAME STREQUAL "iOS")
    # The desktop setup window (eframe, rfd) does not target iOS, which extracts without it.
    corrosion_import_crate(MANIFEST_PATH ${OC_RUST_MANIFEST} CRATES opencloud_core NO_DEFAULT_FEATURES)
else()
    corrosion_import_crate(MANIFEST_PATH ${OC_RUST_MANIFEST} CRATES opencloud_core)
endif()
# Keep the build's seed separate from a completed extraction in the build directory.
set(OC_GAMEPLAY_ARCHIVE "${CMAKE_BINARY_DIR}/assets/data.zip")
corrosion_set_env_vars(opencloud_core "OPENCLOUD_GAMEPLAY_ARCHIVE=${OC_GAMEPLAY_ARCHIVE}")
include(GNUInstallDirs)
install(FILES "${OC_GAMEPLAY_ARCHIVE}" DESTINATION ${CMAKE_INSTALL_BINDIR})
include("${CMAKE_CURRENT_LIST_DIR}/OpenCloudFfmpeg.cmake")
oc_ffmpeg(opencloud_core INTERFACE)
if(DEFINED OPENCLOUD_GAMEPLAY_ROOT)
    corrosion_set_env_vars(opencloud_core "OPENCLOUD_GAMEPLAY_ROOT=${OPENCLOUD_GAMEPLAY_ROOT}")
else()
    get_filename_component(OC_GAMEPLAY_ROOT "${CMAKE_CURRENT_LIST_DIR}/../../games/dc2/port/gameplay" ABSOLUTE)
    if(EXISTS "${OC_GAMEPLAY_ROOT}/character_clips.json")
        corrosion_set_env_vars(opencloud_core "OPENCLOUD_GAMEPLAY_ROOT=${OC_GAMEPLAY_ROOT}")
    endif()
endif()
find_program(OC_FFMPEG_EXECUTABLE NAMES ffmpeg.exe ffmpeg HINTS "${OC_FFMPEG_DIR}/bin")
if(OC_FFMPEG_EXECUTABLE)
    corrosion_set_env_vars(opencloud_core "OPENCLOUD_FFMPEG=${OC_FFMPEG_EXECUTABLE}")
endif()
if(OC_FFMPEG_DIR)
    corrosion_set_env_vars(opencloud_core "OPENCLOUD_FFMPEG_INCLUDE=${OC_FFMPEG_DIR}/include" "OPENCLOUD_FFMPEG_LIB=${OC_FFMPEG_DIR}/lib")
endif()
corrosion_add_cxxbridge(opencloud_bridge CRATE opencloud_core FILES rust/lib.rs rust/host_jobs.rs)
target_include_directories(opencloud_bridge PUBLIC "${CMAKE_CURRENT_LIST_DIR}/../include")

# Corrosion imports Rust's standard native libraries, but not the GUI crates'
# additional OS dependencies when their static archive is linked from C++.
if(WIN32)
    target_link_libraries(opencloud_core INTERFACE imm32 uxtheme opengl32 dwmapi)
elseif(CMAKE_SYSTEM_NAME STREQUAL "iOS")
    # iOS: no AppKit/Carbon/OpenGL. Link the Apple frameworks the Rust stdlib and
    # CXX bridge need; the game target already links UIKit/Metal/QuartzCore/CoreMotion/CoreGraphics.
    target_link_libraries(opencloud_core INTERFACE
        "-framework Foundation" "-framework CoreFoundation" "-framework CoreGraphics"
        "-framework CoreMotion" "-framework QuartzCore" "-framework UIKit"
        "-framework Metal" objc)
elseif(APPLE)
    target_link_libraries(opencloud_core INTERFACE
        "-framework AppKit" "-framework Carbon" "-framework CoreFoundation" "-framework CoreGraphics"
        "-framework CoreVideo" "-framework Foundation" "-framework IOKit"
        "-framework OpenGL" "-framework QuartzCore" "-framework UniformTypeIdentifiers" objc)
    # Corrosion adds the Command Line Tools SDK's usr/lib by a fixed path for Rust's native libraries (System, c, m).
    # The link's own sysroot supplies them, and a CLT SDK newer than the selected linker is unreadable to it.
    set_property(TARGET opencloud_core-static PROPERTY INTERFACE_LINK_DIRECTORIES "")
endif()
