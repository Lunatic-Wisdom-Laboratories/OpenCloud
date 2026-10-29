# Links FFmpeg's format, codec, resampling and scaling libraries as oc::ffmpeg: the static tree oc-ffmpeg builds
# (Linux, macOS) or BtbN's win64-lgpl-shared archive (Windows, DLLs). Only LGPL builds are acceptable
# inputs; a GPL or nonfree libavcodec must never reach an OpenCloud consumer.

include_guard(GLOBAL)

function(_oc_ffmpeg_default_dir out)
    string(TOLOWER "${CMAKE_SYSTEM_NAME}" os)
    if(CMAKE_SYSTEM_PROCESSOR MATCHES "^(x86_64|AMD64|x64)$")
        set(arch x64)
    elseif(CMAKE_SYSTEM_PROCESSOR MATCHES "^(arm64|aarch64)$")
        set(arch arm64)
    else()
        string(TOLOWER "${CMAKE_SYSTEM_PROCESSOR}" arch)
    endif()
    set(${out} "${CMAKE_SOURCE_DIR}/.opencloud/ffmpeg/${os}-${arch}" PARENT_SCOPE)
endfunction()

function(_oc_ffmpeg_import)
    if(DEFINED ENV{OC_FFMPEG_DIR})
        set(default "$ENV{OC_FFMPEG_DIR}")
    else()
        _oc_ffmpeg_default_dir(default)
    endif()
    set(OC_FFMPEG_DIR "${default}" CACHE PATH "FFmpeg tree: include/, lib/ and on Windows bin/")

    if(NOT EXISTS "${OC_FFMPEG_DIR}/include/libavcodec/avcodec.h")
        message(FATAL_ERROR "oc_ffmpeg: no include/libavcodec/avcodec.h under ${OC_FFMPEG_DIR}. "
            "Linux: the OpenCloud image provides /opt/opencloud/ffmpeg. "
            "macOS: run tools/opencloud/scripts/build/oc-ffmpeg --out <dir>. "
            "Windows: run tools/windows/acquire.ps1 and pass -DOC_FFMPEG_DIR=<dir>.")
    endif()

    if(WIN32)
        set(runtime_dlls "")
        foreach(lib avutil swresample swscale avcodec avformat)
            file(GLOB dll "${OC_FFMPEG_DIR}/bin/${lib}-*.dll")
            list(LENGTH dll count)
            if(NOT count EQUAL 1)
                message(FATAL_ERROR "oc_ffmpeg: expected one bin/${lib}-*.dll under ${OC_FFMPEG_DIR}, found ${count}")
            endif()
            if(NOT EXISTS "${OC_FFMPEG_DIR}/lib/lib${lib}.dll.a")
                message(FATAL_ERROR "oc_ffmpeg: ${OC_FFMPEG_DIR}/lib/lib${lib}.dll.a is missing")
            endif()
            add_library(oc::${lib} SHARED IMPORTED GLOBAL)
            set_target_properties(oc::${lib} PROPERTIES
                IMPORTED_LOCATION "${dll}"
                IMPORTED_IMPLIB "${OC_FFMPEG_DIR}/lib/lib${lib}.dll.a")
            list(APPEND runtime_dlls "${dll}")
        endforeach()
        # The extraction program uses the bundled FFmpeg command and all its shared components.
        file(GLOB runtime_dlls "${OC_FFMPEG_DIR}/bin/*.dll")
        if(NOT EXISTS "${OC_FFMPEG_DIR}/bin/ffmpeg.exe")
            message(FATAL_ERROR "oc_ffmpeg: the MP4 extractor requires bin/ffmpeg.exe")
        endif()
        list(APPEND runtime_dlls "${OC_FFMPEG_DIR}/bin/ffmpeg.exe")
        set_property(GLOBAL PROPERTY OC_FFMPEG_RUNTIME_DLLS "${runtime_dlls}")
    else()
        foreach(lib avutil swresample swscale avcodec avformat)
            if(NOT EXISTS "${OC_FFMPEG_DIR}/lib/lib${lib}.a")
                message(FATAL_ERROR "oc_ffmpeg: ${OC_FFMPEG_DIR}/lib/lib${lib}.a is missing")
            endif()
            add_library(oc::${lib} STATIC IMPORTED GLOBAL)
            set_target_properties(oc::${lib} PROPERTIES
                IMPORTED_LOCATION "${OC_FFMPEG_DIR}/lib/lib${lib}.a")
        endforeach()
        # The archives reference pthreads and libm; they must follow libavutil on the link line.
        if(CMAKE_SYSTEM_NAME STREQUAL "Linux")
            find_package(Threads REQUIRED)
            set_property(TARGET oc::avutil PROPERTY INTERFACE_LINK_LIBRARIES Threads::Threads m)
        endif()
    endif()

    set_property(TARGET oc::avcodec PROPERTY INTERFACE_LINK_LIBRARIES oc::avutil)
    set_property(TARGET oc::avformat PROPERTY INTERFACE_LINK_LIBRARIES oc::avcodec)
    set_property(TARGET oc::swresample PROPERTY INTERFACE_LINK_LIBRARIES oc::avutil)
    set_property(TARGET oc::swscale PROPERTY INTERFACE_LINK_LIBRARIES oc::avutil)

    add_library(oc::ffmpeg INTERFACE IMPORTED GLOBAL)
    set_property(TARGET oc::ffmpeg PROPERTY INTERFACE_LINK_LIBRARIES "oc::avformat;oc::avcodec;oc::swscale;oc::swresample")
    target_include_directories(oc::ffmpeg SYSTEM INTERFACE "${OC_FFMPEG_DIR}/include")

    message(STATUS "OpenCloud: FFmpeg from ${OC_FFMPEG_DIR}")
endfunction()

function(oc_ffmpeg target)
    set(scope PUBLIC)
    if(ARGC GREATER 1)
        set(scope "${ARGV1}")
    endif()
    if(NOT "${scope}" MATCHES "^(PUBLIC|PRIVATE|INTERFACE)$")
        message(FATAL_ERROR "oc_ffmpeg: scope must be PUBLIC, PRIVATE or INTERFACE, got '${scope}'")
    endif()

    if(NOT TARGET oc::ffmpeg)
        _oc_ffmpeg_import()
    endif()
    target_link_libraries(${target} ${scope} oc::ffmpeg)
endfunction()
